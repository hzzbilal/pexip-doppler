// PulseNative — raw native entry points that the managed Pexip.Pulse wrapper
// does not (yet) surface, but which the demo needs to get media flowing.
//
// Mirrors the demos/windows sibling: two pieces are required to see / hear
// anything but live outside the generated managed wrapper, so we p/invoke them
// straight out of pexpulse.dll here.
//
//   * pulse_device_session_connect_system_default() — binds the OS default
//     camera / microphone / speaker to the call. Without a device session bound
//     on each direction Pulse has nothing to capture or play.
//
//   * pulse_options_set_*_window_handle() — hands Pulse a native window (HWND)
//     to draw a given video stream into. By default Pulse auto-spawns its own
//     top-level windows for the remote video and self-view; pointing these at
//     our own WinForms panels instead lets the video live *inside* the app
//     window.
//
// Cdecl + SafeDirectories matches how the generated wrapper declares its own
// imports so the loader picks up the pexpulse.dll that the build copies next to
// winapp.exe.

using System.Runtime.InteropServices;

using Pexip.Pulse.NativeEnums;

namespace WinApp;

internal static class PulseNative
{
    private const string Lib = "pexpulse.dll";

    // Attach the OS default device for (media_type, media_direction) to a
    // media-content slot (we only use MAIN here).
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern PulseErrorType pulse_device_session_connect_system_default(
        IntPtr client,
        PulseMediaContent media_content,
        PulseMediaType media_type,
        PulseMediaDirection media_direction);

    // Render the incoming far-end video into window_handle (an HWND), or pass
    // IntPtr.Zero to disable Pulse's own auto-spawned window. Must be called
    // before connecting.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern PulseErrorType pulse_options_set_remote_video_window_handle(
        IntPtr client, IntPtr window_handle);

    // Render the local camera self-view into window_handle (an HWND), or pass
    // IntPtr.Zero to disable. Must be called before connecting.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern PulseErrorType pulse_options_set_self_view_window_handle(
        IntPtr client, IntPtr window_handle);

    // Render an incoming presentation/screen-share into window_handle (an HWND),
    // or pass IntPtr.Zero to disable the auto-spawned presentation window.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern PulseErrorType pulse_options_set_presentation_video_window_handle(
        IntPtr client, IntPtr window_handle);
}
