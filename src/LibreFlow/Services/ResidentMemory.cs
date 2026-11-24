using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LibreFlow.Services;

/// <summary>
/// Keeps the memory the keyboard path needs locked in RAM. On a machine short of memory
/// Windows moves an idle background process to the page file within minutes; the next
/// key press then waits on the disk inside the keyboard hook and typing freezes for up
/// to seconds. A minimum working set alone is not honoured under real pressure (measured:
/// 8 MB of 72 MB left in RAM), so the pages are locked, which Windows cannot undo.
/// </summary>
internal static class ResidentMemory
{
    private const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x1;
    private const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_PRIVATE = 0x20000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;

    private const long Headroom = 24L << 20;
    private const long MaxLocked = 320L << 20;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(2);

    /// <summary>Code the hook runs on every key. Everything else (WPF, WinForms, WebView2) can be paged out freely.</summary>
    private static readonly string[] HotModules =
    [
        "LibreFlow.dll", "LibreFlow.Core.dll", "WeCantSpell.Hunspell.dll", "coreclr.dll", "System.Private.CoreLib.dll",
        "System.Runtime.dll", "System.Collections.dll", "System.Memory.dll", "System.Linq.dll", "System.Text.RegularExpressions.dll",
    ];

    private static System.Threading.Timer? _timer;
    private static long _lastLocked;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, UIntPtr minimum, UIntPtr maximum, uint flags);

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint address, out MEMORY_BASIC_INFORMATION info, nint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualLock(nint address, nint size);

    /// <summary>
    /// Call once everything the keyboard path needs has been loaded and used. Repeats every
    /// couple of minutes because the garbage collector moves data into newly committed memory.
    /// </summary>
    public static void Start()
    {
        Pin();
        _timer = new System.Threading.Timer(_ => Pin(), null, Period, Period);
    }

    private static void Pin()
    {
        try
        {
            var regions = Lockable();
            long wanted = 0;
            foreach (var (_, size) in regions) wanted += size;
            if (wanted > MaxLocked)
            {
                Log.Error($"La memoria a fijar ({wanted >> 20} MB) supera el tope; no se fija");
                return;
            }

            // Windows only lets a process lock as many pages as its minimum working set.
            var minimum = wanted + Headroom;
            if (!SetProcessWorkingSetSizeEx(GetCurrentProcess(), (UIntPtr)(ulong)minimum, (UIntPtr)(ulong)(minimum + (1L << 30)),
                    QUOTA_LIMITS_HARDWS_MIN_ENABLE | QUOTA_LIMITS_HARDWS_MAX_DISABLE))
            {
                Log.Error($"No se pudo reservar memoria en RAM (error {Marshal.GetLastWin32Error()}); el teclado puede demorar tras un rato sin uso");
                return;
            }

            long locked = 0;
            var failed = 0;
            foreach (var (address, size) in regions)
            {
                if (VirtualLock(address, (nint)size)) locked += size;
                else failed++;
            }

            if (Math.Abs(locked - _lastLocked) > (8L << 20))
            {
                Log.Info($"Memoria fijada en RAM: {locked >> 20} MB de {wanted >> 20} MB ({regions.Count} bloques, {failed} sin fijar)");
                _lastLocked = locked;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error al fijar la memoria en RAM", ex);
        }
    }

    /// <summary>The app's own committed data, plus the code of the modules the hook runs.</summary>
    private static List<(nint Address, long Size)> Lockable()
    {
        var hot = new List<(long Start, long End)>();
        using (var process = Process.GetCurrentProcess())
        {
            foreach (ProcessModule module in process.Modules)
            {
                if (Array.Exists(HotModules, name => string.Equals(name, module.ModuleName, StringComparison.OrdinalIgnoreCase)))
                    hot.Add((module.BaseAddress, module.BaseAddress + module.ModuleMemorySize));
            }
        }

        var regions = new List<(nint, long)>();
        long address = 0x10000;
        const long end = 0x7FFF_FFFE_FFFF;
        var infoSize = (nint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (address < end && VirtualQuery((nint)address, out var info, infoSize) != 0)
        {
            long size = info.RegionSize;
            if (size <= 0) break;
            var usable = info.State == MEM_COMMIT && (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) == 0 && info.Protect != 0;
            if (usable && (info.Type == MEM_PRIVATE || InAny(hot, info.BaseAddress)))
                regions.Add((info.BaseAddress, size));
            address = info.BaseAddress + size;
        }
        return regions;
    }

    private static bool InAny(List<(long Start, long End)> ranges, long address)
    {
        foreach (var (start, end) in ranges)
            if (address >= start && address < end) return true;
        return false;
    }
}
