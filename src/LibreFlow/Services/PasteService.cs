using System.Runtime.InteropServices;
using System.Windows;
using LibreFlow.Input;
using LibreFlow.Native;

namespace LibreFlow.Services;

/// <summary>Puts text where the caret is. Must be called on the UI thread (the clipboard needs it).</summary>
internal sealed class PasteService
{
    private readonly SettingsStore _settings;

    public PasteService(SettingsStore settings) => _settings = settings;

    public async Task PasteAsync(string text)
    {
        if (text.Length == 0) return;
        // If Ctrl or Win were still down, our Ctrl+V would turn into some other shortcut.
        await WaitForModifiersReleasedAsync();

        if (_settings.Current.PasteMethod == "type")
        {
            await Task.Run(() => TextInjector.TypeText(text));
            return;
        }

        var restore = _settings.Current.RestoreClipboard;
        var previous = restore ? Snapshot() : null;
        if (!TrySetText(LibreFlow.Core.Speech.SpeechText.WindowsLines(text)))
        {
            await Task.Run(() => TextInjector.TypeText(text));
            return;
        }
        await Task.Delay(40);
        TextInjector.SendCtrlV();

        if (restore)
        {
            // Give the target app time to read the clipboard before putting the old content back.
            await Task.Delay(450);
            Restore(previous);
        }
    }

    /// <summary>Copies what is on the clipboard (text, rich text, images, files) so it can be put back.</summary>
    private static DataObject? Snapshot()
    {
        try
        {
            var current = Clipboard.GetDataObject();
            if (current is null) return null;
            var copy = new DataObject();
            var any = false;
            foreach (var format in SnapshotFormats)
            {
                if (!current.GetDataPresent(format)) continue;
                var data = current.GetData(format);
                if (data is null) continue;
                copy.SetData(format, data);
                any = true;
            }
            return any ? copy : null;
        }
        catch (Exception ex) when (ex is COMException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static readonly string[] SnapshotFormats =
    [
        DataFormats.UnicodeText, DataFormats.Text, DataFormats.Rtf, DataFormats.Html, DataFormats.CommaSeparatedValue,
        DataFormats.FileDrop, DataFormats.Bitmap,
    ];

    private static void Restore(DataObject? previous)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (previous is null) Clipboard.Clear();
                else Clipboard.SetDataObject(previous, copy: true);
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(25);
            }
        }
    }

    public bool Copy(string text) => TrySetText(text);

    private static async Task WaitForModifiersReleasedAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            var held = Win32.IsDown(Win32.VK_CONTROL) || Win32.IsDown(Win32.VK_LWIN) || Win32.IsDown(Win32.VK_RWIN)
                       || Win32.IsDown(Win32.VK_MENU) || Win32.IsDown(Win32.VK_SHIFT);
            if (!held) return;
            await Task.Delay(20);
        }
    }

    private static bool TrySetText(string text)
    {
        // Another program may hold the clipboard open for a moment; retry briefly.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(25);
            }
        }
        Log.Error("No se pudo escribir en el portapapeles");
        return false;
    }
}
