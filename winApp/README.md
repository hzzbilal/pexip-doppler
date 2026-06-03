# winApp — Windows Forms Pexip Pulse video call

A small **.NET 8 Windows Forms** application that places a Pexip Infinity call
through the **Pexip Pulse** NuGet package shipped in this repo under
[`../sdk/windows`](../sdk/windows). It is a slightly different take on the
existing [`demos/windows`](../demos/windows) sample: instead of a single video
panel with a picture-in-picture self-view, this app shows **two side-by-side
containers** — the **incoming** (remote) video on the left and the **outgoing**
(local camera) preview on the right — once the meeting is established.

## What's in the box

| File                | Purpose                                                          |
| ------------------- | ---------------------------------------------------------------- |
| `WinApp.sln`        | Solution file at `winApp/`.                                      |
| `WinApp/WinApp.csproj` | Targets `net8.0-windows` / `win-x64`, references `Pexip.Pulse`. |
| `WinApp/Program.cs` | WinForms bootstrap (`Application.Run`).                          |
| `WinApp/MainForm.cs` | The whole UI + Pulse call lifecycle.                            |
| `WinApp/PulseNative.cs` | A few raw `pexpulse.dll` entry points (default devices + video window handles) the managed wrapper doesn't surface. |
| `WinApp/nuget.config` | Points NuGet at the in-repo `../../sdk/windows` package folder. |

## Build (on Windows)

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) with the
Windows Desktop workload. The `Pexip.Pulse` package only ships native binaries
(`pexpulse.dll` and friends) for `win-x64`, so this targets that runtime.

```powershell
cd winApp
dotnet build -c Release
```

`dotnet` restores `Pexip.Pulse` straight from the `sdk\windows` folder in this
repo (see `nuget.config`) and copies all of the native runtime assets next to
the produced `winapp.exe`.

> The project sets `<EnableWindowsTargeting>true</EnableWindowsTargeting>` so
> it can also be *restored / compiled* (not run) from a non-Windows machine,
> e.g. in CI.

## Run

```powershell
.\WinApp\bin\Release\net8.0-windows\win-x64\winapp.exe
```

In the window, fill out the bottom form:

1. **Server** — your Pexip Infinity node, e.g. `vc.example.com`
2. **Conference ID** — the VMR / conference alias, e.g. `meet.alice`
3. **Name** — how you appear to others
4. **PIN** — only if the conference requires one

Then press **Connect**. The two top panels start showing the **incoming**
remote stream and your **outgoing** camera feed once the call goes live. Press
**Hang up** (the Connect button toggles while in a call) to leave, or just
close the window — the app disconnects and frees the Pulse handle on the way
out.

## How the video gets into the two panels

Before connecting, the app hands Pulse the two panel HWNDs:

* `pulse_options_set_remote_video_window_handle` → the **incoming** (left)
  panel — Pulse paints the far-end video straight into it.
* `pulse_options_set_self_view_window_handle` → the **outgoing** (right) panel
  — Pulse paints the local camera preview into it.

It then attaches the OS default **camera**, **microphone** and **speaker** to
the call with `pulse_device_session_connect_system_default` so media actually
flows in both directions. Without those device sessions the call would connect
but stay silent and dark.

For the deeper Pulse story (RTMP ingest, Twitch mix, presentation rendering,
device enumeration, …) see [`demos/doppler`](../demos/doppler) and
[`demos/pexninja`](../demos/pexninja).
