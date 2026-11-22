using System.Globalization;

namespace PaRuX.Intelligence;

/// <summary>
/// Pure, UI-free model behind the Training Lab charts. It turns a raw per-step metric series into at
/// most <c>maxPoints</c> drawable points plus the summary numbers a screen reader needs.
///
/// Downsampling is min/max bucketing, not decimation: a loss or grad-norm spike is exactly the thing a
/// user opens the chart to find, and taking every Nth sample can drop it. Each bucket keeps its lowest
/// and highest point (in step order), so the envelope of the curve survives any compression ratio.
/// Missing values stay missing: a run that never logged GPU util yields an empty series, not zeros.
/// </summary>
public static class TrainingChartModel
{
    public enum SeriesKind { Loss, GradientNorm, LearningRate, TokensPerSecond, GpuUtilPercent, VramMib }

    public readonly record struct Point(double Step, double Value);

    public sealed record Series(
        SeriesKind Kind,
        IReadOnlyList<Point> Points,
        int RawCount,
        double Min,
        double Max,
        double First,
        double Last)
    {
        public bool HasData => RawCount > 0;
    }

    public const int DefaultMaxPoints = 240;

    /// <summary>Builds one chart series. Non-finite values are ignored; steps are sorted ascending
    /// (stable) so a journal replayed out of order still draws left to right.</summary>
    public static Series Build(SeriesKind kind, IEnumerable<(double Step, double? Value)> samples, int maxPoints = DefaultMaxPoints)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (maxPoints < 2) maxPoints = 2;

        var clean = new List<Point>();
        foreach ((double step, double? value) in samples)
        {
            if (value is not { } v || !double.IsFinite(v) || !double.IsFinite(step)) continue;
            clean.Add(new Point(step, v));
        }
        if (clean.Count == 0) return new Series(kind, Array.Empty<Point>(), 0, 0, 0, 0, 0);

        List<Point> ordered = clean.OrderBy(p => p.Step).ToList();
        double min = ordered.Min(p => p.Value), max = ordered.Max(p => p.Value);
        return new Series(kind, Downsample(ordered, maxPoints), ordered.Count, min, max, ordered[0].Value, ordered[^1].Value);
    }

    /// <summary>Min/max bucket downsampling. Output length is always &lt;= <paramref name="maxPoints"/>,
    /// the first and last points are always kept, and output stays in step order.</summary>
    public static IReadOnlyList<Point> Downsample(IReadOnlyList<Point> ordered, int maxPoints)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        if (maxPoints < 2) maxPoints = 2;
        if (ordered.Count <= maxPoints) return ordered.ToArray();

        var result = new List<Point>(maxPoints) { ordered[0] };
        int interior = ordered.Count - 2;
        int buckets = Math.Max(1, (maxPoints - 2) / 2);
        for (int b = 0; b < buckets; b++)
        {
            int start = 1 + (int)((long)interior * b / buckets);
            int end = 1 + (int)((long)interior * (b + 1) / buckets); // exclusive
            if (end <= start) continue;
            int lo = start, hi = start;
            for (int i = start + 1; i < end; i++)
            {
                if (ordered[i].Value < ordered[lo].Value) lo = i;
                if (ordered[i].Value > ordered[hi].Value) hi = i;
            }
            if (lo == hi) result.Add(ordered[lo]);
            else if (lo < hi) { result.Add(ordered[lo]); result.Add(ordered[hi]); }
            else { result.Add(ordered[hi]); result.Add(ordered[lo]); }
        }
        result.Add(ordered[^1]);
        return result;
    }

    /// <summary>Maps points to pixel coordinates inside a width x height plot (y grows downward).
    /// A flat series is drawn on the vertical middle instead of dividing by zero.</summary>
    public static IReadOnlyList<(double X, double Y)> ToPixels(Series series, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (!series.HasData || width <= 0 || height <= 0) return Array.Empty<(double, double)>();
        double x0 = series.Points[0].Step, x1 = series.Points[^1].Step;
        double xr = x1 - x0, yr = series.Max - series.Min;
        var pixels = new List<(double, double)>(series.Points.Count);
        foreach (Point p in series.Points)
        {
            double x = xr <= 0 ? width / 2 : (p.Step - x0) / xr * width;
            double y = yr <= 0 ? height / 2 : height - (p.Value - series.Min) / yr * height;
            pixels.Add((x, y));
        }
        return pixels;
    }

    /// <summary>Plain-language summary for AutomationProperties / a text alternative. Invariant numbers.</summary>
    public static string DescribeEn(Series series, string label)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (!series.HasData) return label + ": no data yet.";
        return string.Create(CultureInfo.InvariantCulture,
            $"{label}: {series.RawCount} samples, first {Format(series.First)}, latest {Format(series.Last)}, lowest {Format(series.Min)}, highest {Format(series.Max)}, {Trend(series)}.");
    }

    public static string DescribeIt(Series series, string label)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (!series.HasData) return label + ": nessun dato per ora.";
        string trend = Trend(series) switch { "rising" => "in aumento", "falling" => "in calo", _ => "stabile" };
        return string.Create(CultureInfo.InvariantCulture,
            $"{label}: {series.RawCount} campioni, primo {Format(series.First)}, ultimo {Format(series.Last)}, minimo {Format(series.Min)}, massimo {Format(series.Max)}, {trend}.");
    }

    private static string Trend(Series s)
    {
        double span = s.Max - s.Min;
        double delta = s.Last - s.First;
        if (span <= 0 || Math.Abs(delta) < span * 0.05) return "flat";
        return delta > 0 ? "rising" : "falling";
    }

    private static string Format(double v) =>
        v != 0 && (Math.Abs(v) < 0.001 || Math.Abs(v) >= 1e6)
            ? v.ToString("0.###e+0", CultureInfo.InvariantCulture)
            : v.ToString("0.####", CultureInfo.InvariantCulture);
}
