using mEmuera.Contracts;

namespace mEmuera.Runtime.Execution;

public sealed partial class GameSession
{
    private readonly Func<int, bool?>? nativeKeyState;
    private readonly Func<bool>? inputActive;
    private readonly bool[] keysDown = new bool[256], keysPressed = new bool[256], keysObserved = new bool[256];
    void IBuiltinContext.ClearKeyLatches() => Array.Clear(keysPressed);
    bool IBuiltinContext.KeyInputActive
    {
        get
        {
            bool active = inputActive?.Invoke() != false;
            if (!active) { Array.Clear(keysPressed); Array.Clear(keysObserved); Array.Clear(keysDown); }
            return active;
        }
    }
    bool IBuiltinContext.KeyState(long key, bool triggered)
    {
        if (key is < 0 or > 255) return false;
        if (inputActive?.Invoke() == false) { Array.Clear(keysPressed); Array.Clear(keysObserved); Array.Clear(keysDown); return false; }
        int code = (int)key;
        bool down = nativeKeyState?.Invoke(code) ?? keysDown[code];
        bool edge = down && !keysObserved[code];
        keysObserved[code] = down;
        if (!triggered) return down;
        bool pressed = keysPressed[code]; keysPressed[code] = false;
        return pressed || edge;
    }
}
