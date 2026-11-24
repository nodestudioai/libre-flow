using System.Runtime.InteropServices;
using LibreFlow.Native;
using LibreFlow.Services;

namespace LibreFlow.Input;

internal readonly record struct KeyEvent(int Vk, uint Scan, bool Down, bool Injected, bool Ours);

internal interface IInputSink
{
    /// <summary>What the last key made the sink do, for the log. Never the key itself.</summary>
    string DescribeLastKey();

    /// <summary>Called on the hook thread for every key. Return true to swallow the key. Must be fast.</summary>
    bool OnKey(in KeyEvent e);

    /// <summary>Called on the hook thread when any mouse button goes down.</summary>
    void OnMouseDown();
}

/// <summary>
/// System-wide low-level keyboard and mouse hooks on their own thread, so a busy UI can
/// never make typing lag. Windows silently drops a hook whose callback is slow, so a
/// watchdog reinstalls them when input keeps arriving that the hooks no longer see.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    /// <summary>Marks the key events this app injects so the hook can ignore them.</summary>
    public static readonly UIntPtr Marker = new(0x4F574850);

    private const uint WM_REINSTALL = Win32.WM_APP + 1;

    private readonly IInputSink _sink;
    private readonly Win32.HookProc _keyboardProc;
    private readonly Win32.HookProc _mouseProc;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private readonly System.Threading.Timer _watchdog;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private uint _threadId;
    private int _lastEventTick = Environment.TickCount;
    private int _lastReinstallTick;
    private int _lastReinstallLogTick = Environment.TickCount - 600_001;
    private int _reinstalls;

    public KeyboardHook(IInputSink sink)
    {
        _sink = sink;
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "LibreFlow input hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
        _watchdog = new System.Threading.Timer(_ => CheckAlive(), null, 2000, 2000);
    }

    private void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        Install();
        _ready.Set();
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_REINSTALL)
            {
                Uninstall();
                Install();
            }
        }
        Uninstall();
    }

    private void Install()
    {
        var module = Win32.GetModuleHandle(null);
        _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyboardHook == IntPtr.Zero)
            Log.Error("No se pudo instalar el gancho de teclado: " + Marshal.GetLastWin32Error());
    }

    private void Uninstall()
    {
        if (_keyboardHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Volatile.Write(ref _lastEventTick, Environment.TickCount);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var data = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                var message = (int)wParam;
                var down = message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
                var injected = (data.flags & Win32.LLKHF_INJECTED) != 0;
                var e = new KeyEvent((int)data.vkCode, data.scanCode, down, injected, injected && data.dwExtraInfo == Marker);
                var swallow = _sink.OnKey(in e);
                // Windows drops a hook that answers too slowly, so slow answers are worth knowing about.
                var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (ms > 60) Log.Info($"El gancho de teclado tardó {ms:0} ms en responder ({_sink.DescribeLastKey()})");
                if (swallow) return 1;
            }
            catch (Exception ex)
            {
                Log.Error("Error en el gancho de teclado", ex);
            }
        }
        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Volatile.Write(ref _lastEventTick, Environment.TickCount);
            var message = (int)wParam;
            if (message is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN or Win32.WM_XBUTTONDOWN)
            {
                try { _sink.OnMouseDown(); }
                catch (Exception ex) { Log.Error("Error en el gancho de mouse", ex); }
            }
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void CheckAlive()
    {
        var info = new Win32.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Win32.LASTINPUTINFO>() };
        if (!Win32.GetLastInputInfo(ref info)) return;

        var unseen = unchecked((int)info.dwTime - Volatile.Read(ref _lastEventTick));
        if (unseen < 1500) return;
        // Input reached Windows that the hooks never saw: they were dropped. (It also happens
        // while an elevated window has focus, where reinstalling is harmless.)
        var now = Environment.TickCount;
        if (unchecked(now - _lastReinstallTick) < 15_000) return;
        _lastReinstallTick = now;
        _reinstalls++;
        if (unchecked(now - _lastReinstallLogTick) > 600_000)
        {
            _lastReinstallLogTick = now;
            Log.Info($"Hubo entrada que el gancho de teclado no vio; se reinstala ({_reinstalls} veces desde el aviso anterior)");
            _reinstalls = 0;
        }
        Reinstall();
    }

    public void Reinstall()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, WM_REINSTALL, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}
