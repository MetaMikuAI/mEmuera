using System.Runtime.InteropServices;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace mEmuera.App.Tui.Views;

public sealed class ScriptKeys
{
    private readonly nint window = OperatingSystem.IsWindows() ? GetForegroundWindow() : 0;
    public bool IsActive() => !OperatingSystem.IsWindows() || GetForegroundWindow() == window;
    public bool? IsDown(int key) => OperatingSystem.IsWindows()
        ? IsActive() && (GetAsyncKeyState(key) & 0x8000) != 0 : null;
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    public static int Code(Key key)
    {
        var code = key.KeyCode & ~(KeyCode.ShiftMask | KeyCode.CtrlMask | KeyCode.AltMask);
        if (code is >= KeyCode.A and <= KeyCode.Z or >= KeyCode.D0 and <= KeyCode.D9) return (int)code;
        if (code is >= KeyCode.F1 and <= KeyCode.F24) return 112 + (int)(code - KeyCode.F1);
        return code switch
        {
            KeyCode.Backspace => 8, KeyCode.Tab => 9, KeyCode.Enter => 13, KeyCode.Clear => 12,
            KeyCode.Esc => 27, KeyCode.Space => 32, KeyCode.PageUp => 33, KeyCode.PageDown => 34,
            KeyCode.End => 35, KeyCode.Home => 36, KeyCode.CursorLeft => 37, KeyCode.CursorUp => 38,
            KeyCode.CursorRight => 39, KeyCode.CursorDown => 40, KeyCode.Insert => 45, KeyCode.Delete => 46,
            _ => -1
        };
    }
}
