using System.Windows.Automation;

namespace LibreFlow.Services;

/// <summary>
/// Asks Windows accessibility whether the focused field is a password box. The answer can
/// take a while, so it runs on its own thread and the keyboard hook only reads the result.
/// </summary>
internal sealed class PasswordProbe
{
    private readonly Action<bool> _onResult;
    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEventSlim _answeredOnce = new();
    private int _delayMs;

    public PasswordProbe(Action<bool> onResult)
    {
        _onResult = onResult;
        var thread = new Thread(Run) { IsBackground = true, Name = "LibreFlow password probe" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Checks the focused element after the given delay (focus moves a moment after a click or Tab).</summary>
    public void Request(int delayMs)
    {
        Volatile.Write(ref _delayMs, delayMs);
        _signal.Set();
    }

    /// <summary>The first accessibility query loads several libraries; do it once before typing starts.</summary>
    public void WarmUp(TimeSpan timeout)
    {
        Request(0);
        _answeredOnce.Wait(timeout);
    }

    private void Run()
    {
        while (true)
        {
            _signal.WaitOne();
            var delay = Volatile.Read(ref _delayMs);
            if (delay > 0) Thread.Sleep(delay);
            var isPassword = false;
            try
            {
                isPassword = AutomationElement.FocusedElement?.Current.IsPassword ?? false;
            }
            catch
            {
                // Some apps expose no accessibility tree; treat the field as a normal one.
            }
            _onResult(isPassword);
            _answeredOnce.Set();
        }
    }
}
