using System.Runtime.InteropServices;
using LibreFlow.Core.Autocorrect;
using LibreFlow.Native;

namespace LibreFlow.Input;

/// <summary>Sends synthetic keys. Each call is one SendInput batch, which Windows never interleaves with real typing.</summary>
internal static class TextInjector
{
    private static readonly int InputSize = Marshal.SizeOf<Win32.INPUT>();

    public static void Send(TextEdit edit, bool thenEnter = false)
    {
        var inputs = new List<Win32.INPUT>(edit.Backspaces * 2 + edit.Text.Length * 2 + 2);
        for (var i = 0; i < edit.Backspaces; i++) AddKey(inputs, Win32.VK_BACK);
        AddText(inputs, edit.Text);
        if (thenEnter) AddKey(inputs, Win32.VK_RETURN);
        Flush(inputs);
    }

    public static void TypeText(string text)
    {
        // Long dictations go in chunks so slow apps are not flooded.
        const int chunk = 200;
        for (var i = 0; i < text.Length; i += chunk)
        {
            var inputs = new List<Win32.INPUT>(chunk * 2);
            AddText(inputs, text.Substring(i, Math.Min(chunk, text.Length - i)));
            Flush(inputs);
            if (i + chunk < text.Length) Thread.Sleep(15);
        }
    }

    public static void SendCtrlV()
    {
        var inputs = new List<Win32.INPUT>(4)
        {
            Key(Win32.VK_CONTROL, up: false),
            Key('V', up: false),
            Key('V', up: true),
            Key(Win32.VK_CONTROL, up: true),
        };
        Flush(inputs);
    }

    /// <summary>
    /// Presses an unassigned key while Win is held. Windows opens the Start menu only when
    /// Win is released with no other key in between, so this keeps Ctrl+Win from opening it.
    /// </summary>
    public static void SendMaskKey()
    {
        var inputs = new List<Win32.INPUT>(2);
        AddKey(inputs, Win32.VK_MASK);
        Flush(inputs);
    }

    private static void AddText(List<Win32.INPUT> inputs, string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                AddKey(inputs, Win32.VK_RETURN);
                continue;
            }
            if (ch == '\r') continue;
            inputs.Add(Unicode(ch, up: false));
            inputs.Add(Unicode(ch, up: true));
        }
    }

    private static void AddKey(List<Win32.INPUT> inputs, int vk)
    {
        inputs.Add(Key(vk, up: false));
        inputs.Add(Key(vk, up: true));
    }

    private static Win32.INPUT Key(int vk, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0, dwExtraInfo = KeyboardHook.Marker },
        },
    };

    private static Win32.INPUT Unicode(char ch, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT
            {
                wScan = ch,
                dwFlags = Win32.KEYEVENTF_UNICODE | (up ? Win32.KEYEVENTF_KEYUP : 0),
                dwExtraInfo = KeyboardHook.Marker,
            },
        },
    };

    private static void Flush(List<Win32.INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var array = inputs.ToArray();
        Win32.SendInput((uint)array.Length, array, InputSize);
    }
}
