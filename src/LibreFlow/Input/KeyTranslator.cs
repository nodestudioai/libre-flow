using System.Text;
using LibreFlow.Native;

namespace LibreFlow.Input;

/// <summary>
/// Turns a key press into the characters it types under the foreground window's keyboard
/// layout. Dead keys (´ then a → á) are composed here: asking Windows to do it from a hook
/// would corrupt the dead-key state of the app being typed into.
/// </summary>
internal sealed class KeyTranslator
{
    private const uint DoNotChangeKeyboardState = 0x4;

    private readonly byte[] _state = new byte[256];
    private readonly StringBuilder _buffer = new(8);
    private char _deadChar;

    public bool HasPendingDeadKey => _deadChar != '\0';

    public void ClearDeadKey() => _deadChar = '\0';

    /// <summary>The dead key typed on its own (dead key followed by space prints just the accent).</summary>
    public string FlushDeadKey()
    {
        var s = _deadChar == '\0' ? "" : _deadChar.ToString();
        _deadChar = '\0';
        return s;
    }

    public string Translate(int vk, uint scan, bool shift, bool altGr)
    {
        Array.Clear(_state);
        if (shift) _state[Win32.VK_SHIFT] = 0x80;
        if ((Win32.GetKeyState(Win32.VK_CAPITAL) & 1) != 0) _state[Win32.VK_CAPITAL] = 1;
        if (altGr)
        {
            _state[Win32.VK_CONTROL] = 0x80;
            _state[Win32.VK_MENU] = 0x80;
        }

        var thread = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
        var layout = Win32.GetKeyboardLayout(thread);

        _buffer.Clear();
        var rc = Win32.ToUnicodeEx((uint)vk, scan, _state, _buffer, _buffer.Capacity, DoNotChangeKeyboardState, layout);
        if (rc == 0) return "";

        if (rc < 0)
        {
            var dead = _buffer.Length > 0 ? _buffer[0] : '\0';
            if (_deadChar != '\0')
            {
                // Two dead keys in a row print both accents.
                var both = new string([_deadChar, dead]);
                _deadChar = '\0';
                return both;
            }
            _deadChar = dead;
            return "";
        }

        var text = _buffer.ToString(0, Math.Min(rc, _buffer.Length));
        if (_deadChar == '\0' || text.Length == 0) return text;

        var pending = _deadChar;
        _deadChar = '\0';
        var mark = CombiningMark(pending);
        if (mark != '\0')
        {
            var composed = new string([text[0], mark]).Normalize(NormalizationForm.FormC);
            if (composed.Length == 1) return composed + text[1..];
        }
        return pending + text;
    }

    private static char CombiningMark(char dead) => dead switch
    {
        '´' or '\'' => '́',
        '`' => '̀',
        '^' or 'ˆ' => '̂',
        '¨' or '"' => '̈',
        '~' or '˜' => '̃',
        _ => '\0',
    };
}
