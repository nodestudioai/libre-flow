using System.Drawing;
using System.Windows.Forms;
using LibreFlow.Services;

namespace LibreFlow.UI;

/// <summary>Notification-area icon and its menu.</summary>
internal sealed class Tray : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autocorrect;
    private readonly ToolStripMenuItem _autostart;
    private readonly ToolStripMenuItem _showBar;

    public Tray()
    {
        _autocorrect = new ToolStripMenuItem("Autocorrector  (Shift+Alt+A)") { CheckOnClick = false };
        _autocorrect.Click += (_, _) => AutocorrectToggled?.Invoke();
        _showBar = new ToolStripMenuItem("Mostrar la barrita flotante");
        _showBar.Click += (_, _) => ShowBarToggled?.Invoke();
        _autostart = new ToolStripMenuItem("Iniciar con Windows");
        _autostart.Click += (_, _) => AutostartToggled?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir Libre Flow", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autocorrect);
        menu.Items.Add("Pegar el último dictado  (Shift+Alt+Z)", null, (_, _) => PasteLastRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_showBar);
        menu.Items.Add(_autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Libre Flow — mantené Ctrl + Win para dictar",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    public event Action? OpenRequested;
    public event Action? AutocorrectToggled;
    public event Action? PasteLastRequested;
    public event Action? ShowBarToggled;
    public event Action? AutostartToggled;
    public event Action? ExitRequested;

    public void Update(bool autocorrect, bool showBar, bool autostart)
    {
        _autocorrect.Checked = autocorrect;
        _showBar.Checked = showBar;
        _autostart.Checked = autostart;
    }

    private static Icon LoadIcon()
    {
        try
        {
            return new Icon(AppPaths.Asset("app.ico"), SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cargar el ícono de bandeja", ex);
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
