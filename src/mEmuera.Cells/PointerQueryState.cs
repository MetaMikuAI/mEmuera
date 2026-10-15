using mEmuera.Contracts;

namespace mEmuera.Cells;

public sealed class PointerQueryState
{
    private readonly Lock gate = new();
    private (int X, int Y)? position;
    private LayoutSnapshot? layout;
    private int height = 25, columns = 80, offset;
    private long layoutEpoch, revision;
    public void Move((int X, int Y)? point) { lock (gate) position = point; }
    public void Observe(LayoutSnapshot visible, long epoch, int rows, int offsetFromBottom)
    {
        lock (gate)
        {
            bool geometryChanged = columns != visible.Width || height != Math.Max(1, rows) || offset != Math.Max(0, offsetFromBottom);
            columns = visible.Width; height = Math.Max(1, rows); offset = Math.Max(0, offsetFromBottom);
            if (layoutEpoch != epoch || geometryChanged || layout is null || layout.DocumentRevision <= visible.DocumentRevision)
            { layout = visible; layoutEpoch = epoch; }
        }
    }
    public PointerMessage Query(PresentationDocument document, long epoch)
    {
        lock (gate)
        {
            if (layout is null || layoutEpoch != epoch || layout.DocumentRevision != document.Revision)
            { layout = CellLayout.Visible(document, columns, height, offset, ++revision); layoutEpoch = epoch; }
            return ScriptPointer.Point(layout, epoch, document.Revision, position, height);
        }
    }
}
