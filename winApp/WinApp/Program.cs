// Program.cs — WinForms bootstrap for the winApp Pexip Pulse demo.
//
// All of the interesting Pulse / UI code lives in MainForm.cs.

namespace WinApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
