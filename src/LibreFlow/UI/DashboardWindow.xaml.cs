using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using LibreFlow.Native;
using LibreFlow.Services;

namespace LibreFlow.UI;

/// <summary>Main window. The interface is local HTML shown in WebView2; data flows through <see cref="Bridge"/>.</summary>
public partial class DashboardWindow : Window
{
    private const string Host = "app.libreflow";

    private readonly Bridge _bridge;
    private string? _pendingPage;
    private bool _ready;

    internal DashboardWindow(Bridge bridge, string? page)
    {
        _bridge = bridge;
        _pendingPage = page;
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => Web.Dispose();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var dark = 1;
        Win32.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData);
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, AppPaths.WebRoot, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnMessage;
            // The page is local; anything else opens in the user's browser.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                OpenExternal(e.Uri);
            };
            core.Navigate($"https://{Host}/index.html");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo iniciar WebView2", ex);
            Web.Visibility = Visibility.Collapsed;
            Fallback.Visibility = Visibility.Visible;
            Fallback.Text = "No se pudo abrir el panel porque falta el componente WebView2 de Microsoft Edge.\n" +
                            "El dictado y el autocorrector siguen funcionando.";
        }
    }

    private static void OpenExternal(string uri)
    {
        if (!uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo abrir el enlace", ex);
        }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idElement) ? idElement.ToString() : null;
            var method = root.GetProperty("method").GetString() ?? "";

            if (method == "ui.ready")
            {
                _ready = true;
                if (_pendingPage is not null) Push("navigate", new { page = _pendingPage });
                _pendingPage = null;
                Reply(id, null, null);
                return;
            }

            var args = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            var result = await _bridge.HandleAsync(method, args);
            Reply(id, result, null);
        }
        catch (Exception ex)
        {
            if (ex is not Speech.SpeechException) Log.Error("Error en el puente del panel", ex);
            Reply(id, null, ex is Speech.SpeechException or ArgumentException ? ex.Message : "Ocurrió un error inesperado.");
        }
    }

    private void Reply(string? id, object? result, string? error)
    {
        if (id is null || Web.CoreWebView2 is null) return;
        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = error is null, result, error }, Bridge.Json));
    }

    /// <summary>Sends an event to the page ("history.changed", "navigate"…).</summary>
    public void Push(string name, object? data = null)
    {
        if (!_ready || Web.CoreWebView2 is null) return;
        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = name, data }, Bridge.Json));
    }

    public void Navigate(string page)
    {
        if (_ready) Push("navigate", new { page });
        else _pendingPage = page;
    }
}
