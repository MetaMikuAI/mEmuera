namespace mEmuera.Contracts;

public interface ITextMetrics
{
    int CellWidth { get; }
    int Columns(string text);
}
public sealed record ViewportMessage(int Columns, int Rows) : SessionMessage;
