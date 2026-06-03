// MainForm — winApp WinForms front-end around Pexip Pulse.
//
// The form shows two side-by-side video containers once a meeting is
// established: the *incoming* far-end video on the left and the *outgoing*
// local camera (self-view) on the right. The bottom strip is the connect form
// (Server / Conference / Display name / PIN) plus a log pane.
//
// The Pulse call lifecycle mirrors demos/windows and the C `doppler` demo:
//
//   1. pulse_new()                     -> create a Pulse instance (in the ctor)
//   2. pulse_options_set_*()           -> register callbacks + point Pulse's
//                                         video renderers at our two panels
//   3. pulse_device_session_connect_*  -> attach the default camera/mic/speaker
//   4. pulse_connect_with_rest_async() -> Connect button
//   5. pulse_disconnect_async()        -> Hang up button
//   6. pulse_free()                    -> on form close
//
// Pulse delivers its callbacks on its own threads, so anything that touches the
// UI is marshalled back onto the WinForms thread with BeginInvoke.

using System.Runtime.InteropServices;

using Pexip.Pulse.NativeDelegates;
using Pexip.Pulse.NativeEnums;
using Pexip.Pulse.NativeStructs;

using static Pexip.Pulse.NativeMethods.PulseConnect;
using static Pexip.Pulse.NativeMethods.PulseOptions;

namespace WinApp;

internal sealed class MainForm : Form
{
    // --- Pulse handle + rooted callbacks -----------------------------------

    private IntPtr _pulse;

    // The managed wrapper hands raw function pointers for these delegates to
    // native code, so we MUST keep them rooted for as long as Pulse is alive —
    // otherwise the GC could collect them and Pulse would call into freed
    // memory. Instance fields keep them alive alongside the form.
    private PulseVersionCallback? _versionCb;
    private PulseConferenceStatusInfoCallback? _statusCb;
    private PulseConferenceEventRemoteDisconnectCallback? _remoteDisconnectCb;
    private PulseOperationProgressCallback? _progressCb;
    private PulseAsyncOperationResultCallback? _connectResultCb;
    private PulseAsyncOperationResultCallback? _disconnectResultCb;

    private bool _inCall;
    private bool _devicesAttached;

    // --- controls ----------------------------------------------------------

    // The two video containers. Pulse paints the incoming far-end video into
    // _incomingVideo (left) and our own outgoing camera preview into
    // _outgoingVideo (right). Both panels are docked inside a TableLayoutPanel
    // that splits the available space 50/50, so they always sit side-by-side
    // as the window resizes.
    private readonly Panel _incomingVideo = new()
    {
        Dock = DockStyle.Fill,
        Margin = new Padding(4),
        BackColor = Color.Black,
        BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly Panel _outgoingVideo = new()
    {
        Dock = DockStyle.Fill,
        Margin = new Padding(4),
        BackColor = Color.FromArgb(20, 20, 20),
        BorderStyle = BorderStyle.FixedSingle,
    };

    private readonly TextBox _server = new() { Text = "" };
    private readonly TextBox _conference = new() { Text = "" };
    private readonly TextBox _displayName = new() { Text = "winApp user" };
    private readonly TextBox _pin = new() { UseSystemPasswordChar = true };
    private readonly Button _connectButton = new() { Text = "Connect" };
    private readonly Label _statusLabel = new() { Text = "Idle" };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = false,
    };

    public MainForm()
    {
        BuildLayout();

        // 1. Create the Pulse instance and 2. register callbacks up-front.
        _pulse = pulse_new();
        if (_pulse == IntPtr.Zero)
        {
            MessageBox.Show(this, "pulse_new() returned NULL — the native Pulse " +
                                  "library could not be initialised.",
                            "Pulse error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _connectButton.Enabled = false;
            return;
        }

        ConfigureCallbacks();
        pulse_options_set_application_user_agent_string(_pulse, "winapp/0.1");

        // Host Pulse's video inside our own panels instead of letting it
        // auto-spawn separate top-level windows. Touching .Handle forces the
        // native window to be created so the HWNDs are valid here. These must be
        // set before the first connect.
        PulseNative.pulse_options_set_remote_video_window_handle(_pulse, _incomingVideo.Handle);
        PulseNative.pulse_options_set_self_view_window_handle(_pulse, _outgoingVideo.Handle);
        // We don't surface presentations in this demo; passing NULL keeps Pulse
        // from popping up its own window if someone shares their screen.
        PulseNative.pulse_options_set_presentation_video_window_handle(_pulse, IntPtr.Zero);

        Log("Pulse initialised. Fill in the form and press Connect.");
    }

    // --- UI ----------------------------------------------------------------

    private void BuildLayout()
    {
        Text = "winApp — Pexip Pulse video call";
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1000, 720);
        MinimumSize = new Size(640, 540);

        // --- top: two side-by-side video containers (incoming / outgoing) --
        var videoRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(6),
        };
        videoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        videoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        videoRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // captions
        videoRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // video

        static Label VideoCaption(string text) => new()
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        };

        videoRow.Controls.Add(VideoCaption("Incoming (remote)"), 0, 0);
        videoRow.Controls.Add(VideoCaption("Outgoing (camera)"), 1, 0);
        videoRow.Controls.Add(_incomingVideo, 0, 1);
        videoRow.Controls.Add(_outgoingVideo, 1, 1);

        // --- bottom: connect form + log -----------------------------------
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 6,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); // button + status
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // log

        static Label Caption(string text) => new()
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        foreach (var box in new[] { _server, _conference, _displayName, _pin })
            box.Dock = DockStyle.Fill;

        grid.Controls.Add(Caption("Server"), 0, 0);
        grid.Controls.Add(_server, 1, 0);
        grid.Controls.Add(Caption("Conference ID"), 0, 1);
        grid.Controls.Add(_conference, 1, 1);
        grid.Controls.Add(Caption("Name"), 0, 2);
        grid.Controls.Add(_displayName, 1, 2);
        grid.Controls.Add(Caption("PIN (optional)"), 0, 3);
        grid.Controls.Add(_pin, 1, 3);

        var actionRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
        };
        _connectButton.AutoSize = true;
        _connectButton.Click += OnConnectOrHangup;
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(12, 8, 0, 0);
        actionRow.Controls.Add(_connectButton);
        actionRow.Controls.Add(_statusLabel);
        grid.Controls.Add(actionRow, 0, 4);
        grid.SetColumnSpan(actionRow, 2);

        _log.Dock = DockStyle.Fill;
        _log.Font = new Font("Consolas", 9f);
        grid.Controls.Add(_log, 0, 5);
        grid.SetColumnSpan(_log, 2);

        // Two stacked rows: the video row fills the top, the form + log take a
        // fixed strip along the bottom.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // video
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 260)); // form + log
        root.Controls.Add(videoRow, 0, 0);
        root.Controls.Add(grid, 0, 1);

        Controls.Add(root);
        AcceptButton = _connectButton;
    }

    // --- actions -----------------------------------------------------------

    private void OnConnectOrHangup(object? sender, EventArgs e)
    {
        if (_pulse == IntPtr.Zero)
            return;

        if (!_inCall)
            StartCall();
        else
            HangUp();
    }

    private void StartCall()
    {
        string server = _server.Text.Trim();
        string conference = _conference.Text.Trim();
        string displayName = _displayName.Text.Trim();
        if (server.Length == 0 || conference.Length == 0 || displayName.Length == 0)
        {
            MessageBox.Show(this,
                "Please fill in Server, Conference ID and Name before connecting.",
                "Missing details", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var cfg = new PulseRestConnectionConfig
        {
            server_address = server,
            conference_name = conference,
            display_name = displayName,
            pin_code = _pin.Text, // may be empty
        };

        // Bind the system default camera / microphone / speaker before we
        // connect. Pulse needs a device session on each direction before any
        // media flows, so without this the call connects but stays silent and
        // dark in both directions.
        AttachDefaultDevices();

        _progressCb = OnProgress;
        _connectResultCb = OnConnectResult;
        var progressCfg = new PulseOperationProgressCallbackConfig
        {
            func = _progressCb,
            user_context = IntPtr.Zero,
        };
        var connectResultCfg = new PulseAsyncOperationResultCallbackConfig
        {
            func = _connectResultCb,
            user_context = IntPtr.Zero,
        };

        Log($"Connecting to {server} / {conference} as \"{cfg.display_name}\" ...");
        // 3. Kick off the async connect.
        PulseErrorType err = pulse_connect_with_rest_async(_pulse, cfg, connectResultCfg, progressCfg);
        if (err != PulseErrorType.PULSE_SUCCESS)
        {
            Log($"pulse_connect_with_rest_async failed: {err}");
            MessageBox.Show(this, $"Connect failed: {err}", "Pulse error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _inCall = true;
        SetBusyUi("Connecting...");
    }

    // Attach the operating system's default camera, microphone and speaker to
    // the MAIN media content. Idempotent: we only do it once per Pulse instance.
    private void AttachDefaultDevices()
    {
        if (_devicesAttached)
            return;

        (string name, PulseMediaType type, PulseMediaDirection direction)[] bindings =
        {
            ("camera",     PulseMediaType.PULSE_MEDIA_VIDEO, PulseMediaDirection.PULSE_MEDIA_INPUT),
            ("microphone", PulseMediaType.PULSE_MEDIA_AUDIO, PulseMediaDirection.PULSE_MEDIA_INPUT),
            ("speaker",    PulseMediaType.PULSE_MEDIA_AUDIO, PulseMediaDirection.PULSE_MEDIA_OUTPUT),
        };

        foreach (var (name, type, direction) in bindings)
        {
            PulseErrorType err = PulseNative.pulse_device_session_connect_system_default(
                _pulse, PulseMediaContent.PULSE_MEDIA_CONTENT_MAIN, type, direction);
            if (err != PulseErrorType.PULSE_SUCCESS)
                Log($"Failed to attach default {name}: {err}");
        }

        _devicesAttached = true;
    }

    private void HangUp()
    {
        _disconnectResultCb = OnDisconnectResult;
        _progressCb ??= OnProgress;
        var disconnectResultCfg = new PulseAsyncOperationResultCallbackConfig
        {
            func = _disconnectResultCb,
            user_context = IntPtr.Zero,
        };
        var progressCfg = new PulseOperationProgressCallbackConfig
        {
            func = _progressCb,
            user_context = IntPtr.Zero,
        };

        Log("Disconnecting ...");
        _connectButton.Enabled = false;
        _statusLabel.Text = "Disconnecting...";
        // 4. Async disconnect; the status callback resets the UI when done.
        PulseErrorType err = pulse_disconnect_async(_pulse, disconnectResultCfg, progressCfg);
        if (err != PulseErrorType.PULSE_SUCCESS)
        {
            Log($"pulse_disconnect_async failed: {err}");
            _connectButton.Enabled = true;
        }
    }

    private void SetBusyUi(string status)
    {
        _connectButton.Text = "Hang up";
        _statusLabel.Text = status;
        SetInputsEnabled(false);
    }

    private void SetIdleUi()
    {
        _inCall = false;
        _connectButton.Text = "Connect";
        _connectButton.Enabled = true;
        _statusLabel.Text = "Idle";
        SetInputsEnabled(true);
    }

    private void SetInputsEnabled(bool enabled)
    {
        _server.Enabled = enabled;
        _conference.Enabled = enabled;
        _displayName.Enabled = enabled;
        _pin.Enabled = enabled;
    }

    // --- Pulse callbacks (called on native threads) ------------------------

    private void ConfigureCallbacks()
    {
        _versionCb = OnVersion;
        pulse_options_set_version_callback(_pulse, new PulseVersionCallbackConfig
        {
            func = _versionCb,
            user_context = IntPtr.Zero,
        });

        _statusCb = OnStatus;
        pulse_options_set_conference_state_callback(_pulse, new PulseConferenceStatusCallbackConfig
        {
            func = _statusCb,
            user_context = IntPtr.Zero,
        });

        _remoteDisconnectCb = OnRemoteDisconnect;
        pulse_options_set_conference_event_remote_disconnect_callback(_pulse, _remoteDisconnectCb, IntPtr.Zero);
    }

    private bool OnVersion(ulong serverMajor, ulong serverMinor, IntPtr ctx)
    {
        Log($"Infinity server reports v{serverMajor}.{serverMinor}");
        return true; // proceed regardless of version.
    }

    private void OnStatus(PulseConferenceStatusInfo info, IntPtr ctx)
    {
        Log($"status: {info.status} (service={info.current_service_type}, blocked={info.is_blocked})");

        BeginInvoke(() =>
        {
            switch (info.status)
            {
                case PulseConnectionStatus.PULSE_CONNECTION_STATUS_CONNECTING:
                    SetBusyUi("Connecting...");
                    break;
                case PulseConnectionStatus.PULSE_CONNECTION_STATUS_RECONNECTING:
                    SetBusyUi("Reconnecting...");
                    break;
                case PulseConnectionStatus.PULSE_CONNECTION_STATUS_CONNECTED:
                    SetBusyUi("Connected");
                    break;
                case PulseConnectionStatus.PULSE_CONNECTION_STATUS_DISCONNECTING:
                    _statusLabel.Text = "Disconnecting...";
                    break;
                case PulseConnectionStatus.PULSE_CONNECTION_STATUS_DISCONNECTED:
                    SetIdleUi();
                    break;
            }
        });
    }

    private void OnRemoteDisconnect(string reason, IntPtr ctx)
    {
        Log($"disconnected by server: {reason}");
    }

    private void OnProgress(IntPtr progressInfo, IntPtr ctx)
    {
        if (progressInfo == IntPtr.Zero)
            return;

        var info = Marshal.PtrToStructure<PulseOperationProgressInfo>(progressInfo);
        Log($"progress: {info.progress,5:P0}  {info.desc}");
    }

    private void OnConnectResult(PulseErrorType err, IntPtr ctx)
    {
        if (err == PulseErrorType.PULSE_SUCCESS)
        {
            Log("connect: success");
        }
        else
        {
            Log($"connect: failed ({err})");
            BeginInvoke(SetIdleUi);
        }
    }

    private void OnDisconnectResult(PulseErrorType err, IntPtr ctx)
    {
        Log(err == PulseErrorType.PULSE_SUCCESS
            ? "disconnect: success"
            : $"disconnect: failed ({err})");
        BeginInvoke(SetIdleUi);
    }

    // --- helpers / teardown ------------------------------------------------

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }

        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 5. Tear the call down (synchronously) and release the handle.
        if (_pulse != IntPtr.Zero)
        {
            if (_inCall)
            {
                _progressCb ??= OnProgress;
                var progressCfg = new PulseOperationProgressCallbackConfig
                {
                    func = _progressCb,
                    user_context = IntPtr.Zero,
                };
                pulse_disconnect(_pulse, progressCfg);
            }

            pulse_free(_pulse);
            _pulse = IntPtr.Zero;
        }

        base.OnFormClosing(e);
    }
}
