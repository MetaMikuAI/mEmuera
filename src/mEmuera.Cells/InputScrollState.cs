using mEmuera.Contracts;

namespace mEmuera.Cells;

public sealed class InputScrollState
{
    private (long Epoch, long Id)? request;
    private bool noFocus;
    public int OffsetFromBottom { get; private set; }
    public void ObserveInput(InputRequest? input)
    {
        if (input is null || request == (input.Epoch, input.Id)) return;
        bool sameEpoch = request?.Epoch == input.Epoch;
        bool nextNoFocus = input.NoFocus && !input.IgnoreInput;
        if (!sameEpoch || !nextNoFocus || !noFocus) OffsetFromBottom = 0;
        request = (input.Epoch, input.Id);
        noFocus = nextNoFocus;
    }
    public void DocumentChanged() { if (!noFocus) OffsetFromBottom = 0; }
    public void Scroll(int delta) => OffsetFromBottom = Math.Max(0, OffsetFromBottom - delta);
    public void Clamp(int maximum) => OffsetFromBottom = Math.Min(OffsetFromBottom, Math.Max(0, maximum));
}
