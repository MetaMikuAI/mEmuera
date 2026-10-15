namespace mEmuera.Infrastructure.Media;

internal class AudioChannelState
{
    public long Duration, Position;
    public bool StopRequested(bool background, bool playing)
    {
        if (!background && !playing) return false;
        Release(); return true;
    }
    public void Release() { Duration = 0; Volatile.Write(ref Position, 0); }
}
