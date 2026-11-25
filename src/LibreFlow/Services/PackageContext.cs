using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LibreFlow.Services;

/// <summary>
/// A program started by a Store-style (MSIX) app runs inside that app's sandbox: Windows silently
/// diverts the new files it writes under AppData, and its registry writes, into a private area of
/// the launcher. Started that way (for instance by an installer running inside a packaged terminal
/// or assistant), Libre Flow would keep its dictionary, history and key where no normal launch can
/// see them, and "start with Windows" would never really be registered. So it hands itself over to
/// the desktop shell, which starts it as a plain program, and leaves.
///
/// The sandbox gives a child process no package identity to ask about, so the only reliable test
/// is the effect itself: create a file under AppData and see where it really landed.
/// </summary>
internal static class PackageContext
{
    private const string LeavingMarker = "LibreFlow.LeavingPackage";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    public static bool IsAppDataRedirected()
    {
        try
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var probe = Path.Combine(roaming, "LibreFlow-" + Guid.NewGuid().ToString("N") + ".tmp");
            using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var actual = new StringBuilder(1024);
            if (GetFinalPathNameByHandle(file.SafeFileHandle, actual, (uint)actual.Capacity, 0) == 0) return false;
            return IsPackagePath(actual.ToString()) && !IsPackagePath(probe);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsPackagePath(string path) =>
        path.Contains(@"\Packages\", StringComparison.OrdinalIgnoreCase) && path.Contains(@"\LocalCache\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this instance was relaunched through the shell and must exit without touching any data.
    /// A test run with its own data folder stays where it is.
    /// </summary>
    public static bool RelaunchOutside()
    {
        if (AppPaths.IsCustomData || Environment.ProcessPath is not { } exe || !IsAppDataRedirected()) return false;

        // The marker stops a loop in case the shell launch did not get the new instance out.
        if (Mutex.TryOpenExisting(LeavingMarker, out var previous))
        {
            previous.Dispose();
            return false;
        }

        using var marker = new Mutex(false, LeavingMarker);
        try
        {
            // explorer.exe passes the request to the running desktop shell, which does the actual launch.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception)
        {
            return false;
        }

        Thread.Sleep(TimeSpan.FromSeconds(6));
        return true;
    }
}
