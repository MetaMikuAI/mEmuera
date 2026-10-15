using mEmuera.Contracts;

namespace mEmuera.Cells;

public static class ScriptPointer
{
    // 脚本坐标：每列 8、每行 16 个单位，Y 从视口底部往上为负。
    public static PointerMessage Point(LayoutSnapshot layout, long epoch, long documentRevision, (int X, int Y)? position, int height)
        => new(epoch, documentRevision, position is { } point ? layout.Hit(point.X, point.Y) : null)
        { X = position is { } x ? x.X * 8 : -1, Y = position is { } y ? (y.Y - height) * 16 : -1 };
}
