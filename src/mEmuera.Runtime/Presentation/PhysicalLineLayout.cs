using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using mEmuera.Contracts;

namespace mEmuera.Runtime.Presentation;

public sealed record PhysicalLine(LogicalLine Line, long Width);

public static class PhysicalLineLayout
{
    public static ImmutableArray<PhysicalLine> Wrap(LogicalLine source, int columns, ITextMetrics metrics)
    {
        long limit = (long)Math.Max(1, columns) * metrics.CellWidth;
        var rows = ImmutableArray.CreateBuilder<PhysicalLine>();
        var runs = ImmutableArray.CreateBuilder<StyledRun>();
        long x = 0; float fraction = 0;
        void NewRow()
        {
            rows.Add(new(source with { Runs = runs.ToImmutable() }, x)); runs.Clear(); x = 0;
        }
        void Fit(long width)
        {
            if (!source.NoWrap && width > 0 && x > 0 && x + width > limit) NewRow();
        }
        foreach (var run in source.Runs)
        {
            if (run.Image is not null || run.Panel is not null || run.ExportHtml is not null)
            {
                long width = run.Image?.Width ?? run.Panel?.Width ?? (long)metrics.Columns(run.Text) * metrics.CellWidth;
                if (run.Image is { } raster && !raster.WidthSteps.IsDefaultOrEmpty)
                {
                    width = 0;
                    foreach (float step in raster.WidthSteps)
                    {
                        float edge = fraction + step; int whole = (int)edge;
                        width += whole; fraction = edge - whole;
                    }
                }
                Fit(width);
                runs.Add(run.Image is { } actual && actual.Width != width ? run with { Image = actual with { Width = checked((int)width) } } : run);
                x += width; continue;
            }
            var text = new StringBuilder();
            var elements = StringInfo.GetTextElementEnumerator(run.Text);
            while (elements.MoveNext())
            {
                string element = elements.GetTextElement();
                long width = (long)Math.Max(1, metrics.Columns(element)) * metrics.CellWidth;
                if (!source.NoWrap && x > 0 && x + width > limit)
                {
                    if (text.Length > 0) { runs.Add(run with { Text = text.ToString() }); text.Clear(); }
                    NewRow();
                }
                text.Append(element); x += width;
            }
            if (text.Length > 0) runs.Add(run with { Text = text.ToString() });
        }
        NewRow(); return rows.ToImmutable();
    }
}
