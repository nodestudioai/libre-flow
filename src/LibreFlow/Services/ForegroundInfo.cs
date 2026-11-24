using System.Text;
using LibreFlow.Native;

namespace LibreFlow.Services;

internal sealed record ForegroundApp(IntPtr Window, string Process, string Title);

internal static class ForegroundInfo
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public static ForegroundApp Current()
    {
        var hwnd = Win32.GetForegroundWindow();
        var title = new StringBuilder(256);
        Win32.GetWindowText(hwnd, title, title.Capacity);
        return new ForegroundApp(hwnd, ProcessName(hwnd), title.ToString());
    }

    /// <summary>Lower-case executable name without extension ("chrome"), or "" when unavailable.</summary>
    public static string ProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return "";
        var handle = Win32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(520);
            var size = (uint)sb.Capacity;
            return Win32.QueryFullProcessImageName(handle, 0, sb, ref size)
                ? Path.GetFileNameWithoutExtension(sb.ToString()).ToLowerInvariant()
                : "";
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }
}
