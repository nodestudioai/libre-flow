using System.Windows;
using LibreFlow.Core.Autocorrect;
using LibreFlow.Core.Storage;
using LibreFlow.Input;
using LibreFlow.Services;
using LibreFlow.Speech;
using LibreFlow.UI;

namespace LibreFlow;

public partial class App : Application
{
    private const string InstanceName = "LibreFlow.SingleInstance";
    private const string ShowEventName = "LibreFlow.ShowDashboard";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private readonly SettingsStore _settings = new();
    private Database _db = null!;
    private InputController _input = null!;
    private KeyboardHook _hook = null!;
    private FlowBarWindow _bar = null!;
    private Tray _tray = null!;
    private DictationController _dictation = null!;
    private Bridge _bridge = null!;
    private DashboardWindow? _dashboard;
    private CorrectionEngine? _engine;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, InstanceName, out var isFirst);
        if (!isFirst)
        {
            // Already running: ask that instance to show its window, then leave.
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing)) existing.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Error no controlado en la interfaz", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Error fatal", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Error en tarea en segundo plano", args.Exception);
            args.SetObserved();
        };

        // The only WPF surface is a tiny pill. Drawing it on the CPU keeps the graphics driver
        // out of this process, which otherwise reserves close to a gigabyte for it.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        Log.Info("Inicio de Libre Flow");
        _settings.Load();
        _db = new Database(AppPaths.Database);

        _bar = new FlowBarWindow();
        _input = new InputController();
        var client = new SpeechClient(_settings);
        var paste = new PasteService(_settings);
        _dictation = new DictationController(_settings, _db, _input, new DictationPipeline(_settings, _db, client), paste, _bar);
        _bridge = new Bridge(_settings, _db, client, _dictation, paste)
        {
            LexiconReady = () => _engine is not null,
            Quit = Quit,
        };
        _tray = new Tray();

        WireInput();
        WireUi();
        _settings.Changed += () => Dispatcher.BeginInvoke(ApplySettings);
        _hook = new KeyboardHook(_input);

        ApplySettings();
        Task.Run(LoadAutocorrect);
        ListenForSecondInstance();

        var minimized = e.Args.Contains("--minimized");
        if (!minimized || _settings.IsFirstRun) ShowDashboard(_settings.HasApiKey ? null : "ajustes");
    }

    /// <summary>Hotkey events arrive on the keyboard hook thread; hand each one to the UI thread and return at once.</summary>
    private void WireInput()
    {
        _input.HoldStarted += () => Dispatcher.BeginInvoke(() => _dictation.Start("ptt"));
        _input.HoldReleased += () => Dispatcher.BeginInvoke(() => _ = _dictation.FinishAsync());
        _input.HandsFreeLocked += () => Dispatcher.BeginInvoke(_dictation.LockHandsFree);
        _input.HandsFreeStopped += () => Dispatcher.BeginInvoke(() => _ = _dictation.FinishAsync());
        _input.Cancelled += () => Dispatcher.BeginInvoke(_dictation.Cancel);
        _input.EscapePressed += () => Dispatcher.BeginInvoke(_dictation.Cancel);
        _input.PasteLastRequested += () => Dispatcher.BeginInvoke(() => _ = _dictation.PasteLastAsync());
        _input.ToggleAutocorrectRequested += () => Dispatcher.BeginInvoke(() => ToggleAutocorrect(announce: true));

        _input.Corrected += (c, app) =>
        {
            _bar.FlashCorrection();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                _db.AddCorrection(c.Original, c.Corrected, c.Kind.ToString(), app);
                Dispatcher.BeginInvoke(() => _dashboard?.Push("corrections.changed"));
            });
        };
        _input.Undone += c => ThreadPool.QueueUserWorkItem(_ =>
        {
            _db.MarkCorrectionUndone(c.Original, c.Corrected);
            Dispatcher.BeginInvoke(() => _dashboard?.Push("corrections.changed"));
        });
    }

    private void WireUi()
    {
        _bar.LogoClicked += _dictation.ToggleFromBar;
        _bar.StopClicked += _dictation.ToggleFromBar;
        _bar.CancelClicked += _dictation.Cancel;
        _bar.AutocorrectClicked += () => ToggleAutocorrect(announce: true);
        _bar.DashboardRequested += () => ShowDashboard(null);

        _dictation.HistoryChanged += () => _dashboard?.Push("history.changed");
        _dictation.ApiKeyNeeded += () => ShowDashboard("ajustes");
        _bridge.WordsChanged += ReloadWords;

        _tray.OpenRequested += () => ShowDashboard(null);
        _tray.AutocorrectToggled += () => ToggleAutocorrect(announce: false);
        _tray.PasteLastRequested += () => _ = _dictation.PasteLastAsync();
        _tray.ShowBarToggled += () => _settings.Update(s => s.ShowBar = !s.ShowBar);
        _tray.AutostartToggled += () =>
        {
            Autostart.Set(!Autostart.IsEnabled);
            ApplySettings();
        };
        _tray.ExitRequested += Quit;
    }

    private void ToggleAutocorrect(bool announce)
    {
        _settings.Update(s => s.AutocorrectEnabled = !s.AutocorrectEnabled);
        if (announce)
            _bar.ShowMessage(_settings.Current.AutocorrectEnabled ? "Autocorrector activado" : "Autocorrector desactivado", isError: false);
    }

    private void ApplySettings()
    {
        var s = _settings.Current;
        _input.SetAutocorrect(s.AutocorrectEnabled, s.AutocorrectExcludedApps);
        if (_engine is { } engine)
        {
            engine.Options.Typos = s.AutocorrectTypos;
            engine.Options.Accents = s.AutocorrectAccents;
            engine.Options.Capitalize = s.AutocorrectCapitalize;
            engine.Options.CorrectOnEnter = s.AutocorrectOnEnter;
        }
        _bar.SetAutocorrect(s.AutocorrectEnabled);
        _bar.SetBarVisible(s.ShowBar);
        _tray.Update(s.AutocorrectEnabled, s.ShowBar, Autostart.IsEnabled);
        _dashboard?.Push("settings.changed");
    }

    /// <summary>Loading the dictionaries takes about a second, so it happens after the app is already usable.</summary>
    private void LoadAutocorrect()
    {
        try
        {
            var engine = new CorrectionEngine(Lexicon.Load(AppPaths.LexiconData));
            engine.WordLearned += word => _db.AddLearned(word);
            _engine = engine;
            ReloadWords();
            _input.WarmUp(engine);
            _input.AttachTracker(new TypingTracker(engine));
            Dispatcher.BeginInvoke(ApplySettings);
            ReleaseLoadingMemory();
            ResidentMemory.Start();
            Log.Info("Autocorrector listo");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cargar el autocorrector", ex);
        }
    }

    /// <summary>
    /// Building the dictionaries churns through close to a gigabyte of temporary objects.
    /// The app then sits idle for hours, so hand that memory back to Windows right away.
    /// </summary>
    private static void ReleaseLoadingMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private void ReloadWords()
    {
        if (_engine is not { } engine) return;
        var entries = _db.GetDictionary();
        engine.SetUserWords(entries.Select(d => d.Phrase).Concat(entries.Where(d => d.ReplaceFrom is not null).Select(d => d.ReplaceFrom!)));
        engine.SetLearnedWords(_db.GetLearned());
    }

    private void ShowDashboard(string? page)
    {
        if (_dashboard is null)
        {
            _dashboard = new DashboardWindow(_bridge, page);
            _dashboard.Closed += (_, _) => _dashboard = null;
            _dashboard.Show();
        }
        else
        {
            if (_dashboard.WindowState == WindowState.Minimized) _dashboard.WindowState = WindowState.Normal;
            if (page is not null) _dashboard.Navigate(page);
        }
        _dashboard.Activate();
    }

    private void ListenForSecondInstance()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(() => ShowDashboard(null));
        })
        { IsBackground = true, Name = "LibreFlow single instance" };
        thread.Start();
    }

    private void Quit()
    {
        Log.Info("Cierre de Libre Flow");
        try
        {
            _hook.Dispose();
            _dictation.Shutdown();
            _tray.Dispose();
            _db.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Error al cerrar", ex);
        }
        Shutdown();
    }
}
