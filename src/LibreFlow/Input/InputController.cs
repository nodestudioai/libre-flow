using LibreFlow.Core.Autocorrect;
using LibreFlow.Native;
using LibreFlow.Services;

namespace LibreFlow.Input;

/// <summary>
/// Runs on the hook thread. Two jobs: the dictation hotkeys (hold Ctrl+Win, double-tap to
/// lock hands-free) and feeding typed characters to the autocorrector. Every event it
/// raises fires on the hook thread, so handlers must hand off and return immediately.
/// </summary>
internal sealed class InputController : IInputSink
{
    private enum State { Idle, Holding, TapWait, HandsFree }

    /// <summary>A press shorter than this is a tap, not a hold.</summary>
    private const int TapMs = 320;
    /// <summary>How long after a tap the second tap may arrive.</summary>
    private const int TapWindowMs = 420;

    private readonly object _gate = new();
    private readonly System.Threading.Timer _tapTimer;
    private readonly KeyTranslator _translator = new();
    private readonly PasswordProbe _passwordProbe;
    private readonly bool _acceptInjected = Environment.GetEnvironmentVariable("LIBREFLOW_ACCEPT_INJECTED") == "1";

    private State _state = State.Idle;
    private bool _latched;
    private bool _handsFreeArmed;
    private int _holdStartTick;

    private volatile TypingTracker? _tracker;
    private volatile bool _autocorrectEnabled = true;
    private volatile HashSet<string> _excludedApps = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _lastWindow;
    private string _currentProcess = "";
    private volatile bool _resetRequested;

    // What the last key made the hook do, for diagnosing slow answers. Never the key itself.
    private string _lastKind = "";
    private int _lastInjectedKeys;
    private double _lastInjectMs;
    private bool _lastWindowChanged;

    public InputController()
    {
        _tapTimer = new System.Threading.Timer(_ => OnTapWindowElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        _passwordProbe = new PasswordProbe(isPassword =>
        {
            if (_tracker is not { } t) return;
            if (isPassword != t.Suppressed)
                Log.Info(isPassword ? $"Campo de contraseña en {_currentProcess}: autocorrector en pausa" : "Autocorrector reanudado");
            t.Suppressed = isPassword;
        });
    }

    public event Action? HoldStarted;
    public event Action? HoldReleased;
    public event Action? HandsFreeLocked;
    public event Action? HandsFreeStopped;
    public event Action? Cancelled;
    public event Action? EscapePressed;
    public event Action? PasteLastRequested;
    public event Action? ToggleAutocorrectRequested;
    public event Action<CorrectionEvent, string>? Corrected;
    public event Action<CorrectionEvent>? Undone;

    /// <summary>True while a dictation is recording or being transcribed, so Esc can cancel it.</summary>
    public volatile bool DictationBusy;

    public string CurrentProcess => _currentProcess;

    /// <summary>
    /// Runs every code path the hook will need once, off the hook thread. The first run of
    /// anything is slow (loading, compiling), and a slow hook gets dropped by Windows.
    /// </summary>
    public void WarmUp(CorrectionEngine engine)
    {
        var rehearsal = new TypingTracker(engine);
        foreach (var key in "ohla hol acomo tambien\b. bien\n")
        {
            switch (key)
            {
                case ' ': rehearsal.OnSpace(); break;
                case '\b': rehearsal.OnBackspace(); break;
                case '\n': rehearsal.OnEnter(); break;
                default: rehearsal.OnChar(key); break;
            }
        }
        engine.ForgetSessionIgnores();
        new KeyTranslator().Translate('A', 0x1E, shift: false, altGr: false);
        ForegroundInfo.ProcessName(Win32.GetForegroundWindow());
        _passwordProbe.WarmUp(TimeSpan.FromSeconds(6));
    }

    public void AttachTracker(TypingTracker tracker)
    {
        tracker.Corrected += c => Corrected?.Invoke(c, _currentProcess);
        tracker.Undone += c => Undone?.Invoke(c);
        tracker.Enabled = _autocorrectEnabled;
        _tracker = tracker;
    }

    public void SetAutocorrect(bool enabled, IEnumerable<string> excludedApps)
    {
        _autocorrectEnabled = enabled;
        _excludedApps = new HashSet<string>(excludedApps.Select(a => a.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        _lastWindow = IntPtr.Zero; // re-evaluate the current app on the next key
        if (_tracker is { } t) t.Enabled = enabled;
    }

    /// <summary>
    /// The app just put text into the focused field (a pasted dictation), so what the
    /// autocorrector remembered about the line no longer matches the screen.
    /// </summary>
    public void RequestTypingReset() => _resetRequested = true;

    /// <summary>The dictation did not start or has ended: go back to waiting for Ctrl+Win.</summary>
    public void ForceIdle()
    {
        lock (_gate)
        {
            _state = State.Idle;
            _tapTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>Hands-free was started from the floating bar instead of the keyboard.</summary>
    public void EnterHandsFree()
    {
        lock (_gate)
        {
            _state = State.HandsFree;
            _handsFreeArmed = true;
        }
    }

    /// <summary>Summary of the work done for the most recent key, e.g. "espacio, corrección de 14 teclas en 3 ms".</summary>
    public string DescribeLastKey()
    {
        var text = _lastKind;
        if (_lastInjectedKeys > 0) text += $", corrección de {_lastInjectedKeys} teclas enviada en {_lastInjectMs:0} ms";
        if (_lastWindowChanged) text += ", ventana nueva";
        return text;
    }

    private void Inject(TextEdit edit, bool thenEnter = false)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        TextInjector.Send(edit, thenEnter);
        _lastInjectMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _lastInjectedKeys = edit.Backspaces + edit.Text.Length + (thenEnter ? 1 : 0);
    }

    public bool OnKey(in KeyEvent e)
    {
        if (e.Ours) return false;
        _lastKind = e.Down ? "tecla" : "soltar tecla";
        _lastInjectedKeys = 0;
        _lastWindowChanged = false;
        if (e.Injected && !_acceptInjected)
        {
            // Another program is typing (a paste tool, a macro): what we tracked is now stale.
            if (e.Down && !IsModifier(e.Vk)) _tracker?.Reset();
            return false;
        }

        var lctrl = SideDown(Win32.VK_LCONTROL, e);
        var rctrl = SideDown(Win32.VK_RCONTROL, e);
        var lwin = SideDown(Win32.VK_LWIN, e);
        var rwin = SideDown(Win32.VK_RWIN, e);
        var alt = SideDown(Win32.VK_LMENU, e) || SideDown(Win32.VK_RMENU, e);
        var shift = SideDown(Win32.VK_LSHIFT, e) || SideDown(Win32.VK_RSHIFT, e);
        var ctrl = lctrl || rctrl;
        var win = lwin || rwin;

        var isComboKey = e.Vk is Win32.VK_LCONTROL or Win32.VK_RCONTROL or Win32.VK_LWIN or Win32.VK_RWIN;
        var combo = ctrl && win && !alt && !shift;

        if (isComboKey)
        {
            _lastKind = e.Down ? "Ctrl/Win" : "soltar Ctrl/Win";
            if (e.Down && combo && !_latched)
            {
                _latched = true;
                OnComboPressed();
            }
            else if (!e.Down && _latched && !combo)
            {
                _latched = false;
                OnComboReleased();
            }
            return false;
        }

        if (e.Down)
        {
            if (!IsModifier(e.Vk) && CancelHoldForOtherShortcut()) return false;

            if (e.Vk == Win32.VK_ESCAPE)
            {
                if (CancelHandsFree()) return true;
                if (DictationBusy) EscapePressed?.Invoke();
            }

            if (shift && alt && !ctrl && !win)
            {
                if (e.Vk == 'Z') { PasteLastRequested?.Invoke(); return true; }
                if (e.Vk == 'A') { ToggleAutocorrectRequested?.Invoke(); return true; }
            }
        }

        return HandleTyping(in e, ctrl, alt, win, shift);
    }

    public void OnMouseDown()
    {
        _tracker?.Reset();
        _translator.ClearDeadKey();
        _passwordProbe.Request(150);
    }

    // ---------- hotkeys ----------

    private void OnComboPressed()
    {
        Action? raise = null;
        lock (_gate)
        {
            switch (_state)
            {
                case State.Idle:
                    _state = State.Holding;
                    _holdStartTick = Environment.TickCount;
                    raise = HoldStarted;
                    break;
                case State.TapWait:
                    _tapTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    _state = State.HandsFree;
                    _handsFreeArmed = false;
                    raise = HandsFreeLocked;
                    break;
                case State.HandsFree when _handsFreeArmed:
                    _state = State.Idle;
                    raise = HandsFreeStopped;
                    break;
            }
        }
        TextInjector.SendMaskKey();
        raise?.Invoke();
    }

    private void OnComboReleased()
    {
        Action? raise = null;
        lock (_gate)
        {
            switch (_state)
            {
                case State.Holding:
                    var held = unchecked(Environment.TickCount - _holdStartTick);
                    if (held < TapMs)
                    {
                        // Keep recording: this may be the first tap of a double tap.
                        _state = State.TapWait;
                        _tapTimer.Change(TapWindowMs, Timeout.Infinite);
                    }
                    else
                    {
                        _state = State.Idle;
                        raise = HoldReleased;
                    }
                    break;
                case State.HandsFree:
                    _handsFreeArmed = true;
                    break;
            }
        }
        raise?.Invoke();
    }

    private void OnTapWindowElapsed()
    {
        lock (_gate)
        {
            if (_state != State.TapWait) return;
            _state = State.Idle;
        }
        Cancelled?.Invoke();
    }

    /// <summary>Ctrl+Win+something is a Windows shortcut (Ctrl+Win+D…), not a dictation.</summary>
    private bool CancelHoldForOtherShortcut()
    {
        lock (_gate)
        {
            if (_state != State.Holding) return false;
            _state = State.Idle;
        }
        Cancelled?.Invoke();
        return true;
    }

    private bool CancelHandsFree()
    {
        lock (_gate)
        {
            if (_state is not (State.HandsFree or State.TapWait)) return false;
            _tapTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _state = State.Idle;
        }
        Cancelled?.Invoke();
        return true;
    }

    // ---------- autocorrect ----------

    private bool HandleTyping(in KeyEvent e, bool ctrl, bool alt, bool win, bool shift)
    {
        var tracker = _tracker;
        if (tracker is null || !e.Down || IsModifier(e.Vk)) return false;

        if (_resetRequested)
        {
            _resetRequested = false;
            tracker.Reset();
            _translator.ClearDeadKey();
        }

        var window = Win32.GetForegroundWindow();
        if (window != _lastWindow)
        {
            _lastWindow = window;
            _lastWindowChanged = true;
            _currentProcess = ForegroundInfo.ProcessName(window);
            tracker.Reset();
            tracker.Suppressed = false;
            tracker.Enabled = _autocorrectEnabled && !_excludedApps.Contains(_currentProcess);
            _translator.ClearDeadKey();
            _passwordProbe.Request(0);
        }

        // Ctrl+Alt together is AltGr, which types characters (@, #, {…) on Spanish layouts.
        var altGr = ctrl && alt;
        if (win || (ctrl && !alt) || (alt && !ctrl))
        {
            tracker.Reset();
            _translator.ClearDeadKey();
            return false;
        }

        switch (e.Vk)
        {
            case Win32.VK_BACK:
                _lastKind = "borrar";
                _translator.ClearDeadKey();
                if (tracker.OnBackspace() is { } undo)
                {
                    Inject(undo);
                    return true;
                }
                return false;

            case Win32.VK_SPACE:
                if (_translator.HasPendingDeadKey)
                {
                    foreach (var ch in _translator.FlushDeadKey()) tracker.OnChar(ch);
                    return false;
                }
                _lastKind = "espacio";
                if (tracker.OnSpace() is { } edit)
                {
                    Inject(edit);
                    return true;
                }
                return false;

            case Win32.VK_RETURN:
                _translator.ClearDeadKey();
                _lastKind = "Enter";
                if (tracker.OnEnter() is { } beforeEnter)
                {
                    Inject(beforeEnter, thenEnter: true);
                    return true;
                }
                _passwordProbe.Request(200);
                return false;

            case Win32.VK_TAB:
                tracker.Reset();
                _translator.ClearDeadKey();
                _passwordProbe.Request(120);
                return false;

            case Win32.VK_ESCAPE or Win32.VK_PRIOR or Win32.VK_NEXT or Win32.VK_END or Win32.VK_HOME or Win32.VK_LEFT
                or Win32.VK_UP or Win32.VK_RIGHT or Win32.VK_DOWN or Win32.VK_INSERT or Win32.VK_DELETE or Win32.VK_APPS
                or Win32.VK_SNAPSHOT or Win32.VK_PAUSE:
            case >= Win32.VK_F1 and <= Win32.VK_F24:
                tracker.Reset();
                _translator.ClearDeadKey();
                return false;

            default:
                foreach (var ch in _translator.Translate(e.Vk, e.Scan, shift, altGr)) tracker.OnChar(ch);
                return false;
        }
    }

    private static bool SideDown(int vk, in KeyEvent e) => e.Vk == vk ? e.Down : Win32.IsDown(vk);

    private static bool IsModifier(int vk) => vk is Win32.VK_SHIFT or Win32.VK_CONTROL or Win32.VK_MENU or Win32.VK_LSHIFT
        or Win32.VK_RSHIFT or Win32.VK_LCONTROL or Win32.VK_RCONTROL or Win32.VK_LMENU or Win32.VK_RMENU or Win32.VK_LWIN
        or Win32.VK_RWIN or Win32.VK_CAPITAL or Win32.VK_NUMLOCK or Win32.VK_SCROLL or Win32.VK_MASK or Win32.VK_PACKET;
}
