using mEmuera.Contracts;
using Terminal.Gui.Input;

namespace mEmuera.App.Tui.Views;

public static class ScriptMouse
{
    public static InputPointerInfo? Click(MouseFlags flags, bool mouseInput)
    {
        InputMouseButton button;
        if ((flags & (MouseFlags.LeftButtonClicked | MouseFlags.LeftButtonDoubleClicked | MouseFlags.LeftButtonTripleClicked)) != 0)
            button = InputMouseButton.Left;
        else if (mouseInput && (flags & (MouseFlags.RightButtonClicked | MouseFlags.RightButtonDoubleClicked | MouseFlags.RightButtonTripleClicked)) != 0)
            button = InputMouseButton.Right;
        else if (mouseInput && (flags & (MouseFlags.MiddleButtonClicked | MouseFlags.MiddleButtonDoubleClicked | MouseFlags.MiddleButtonTripleClicked)) != 0)
            button = InputMouseButton.Middle;
        else return null;
        InputModifiers modifiers = InputModifiers.None;
        if (flags.HasFlag(MouseFlags.Shift)) modifiers |= InputModifiers.Shift;
        if (flags.HasFlag(MouseFlags.Ctrl)) modifiers |= InputModifiers.Control;
        if (flags.HasFlag(MouseFlags.Alt)) modifiers |= InputModifiers.Alt;
        return new(button, modifiers);
    }
}
