using System.Globalization;
using System.Text;

namespace PaRuX.Server.AgentSkills;

public sealed class ChartSeries
{
    public string Name { get; set; } = string.Empty;
    public List<double> Values { get; set; } = new();
}

public sealed class ChartSpec
{
    /// <summary>bar | line | pie</summary>
    public string Type { get; set; } = "bar";
    public string Title { get; set; } = string.Empty;
    public List<string> Labels { get; set; } = new();
    public List<ChartSeries> Series { get; set; } = new();
}

/// <summary>
/// Deterministic, dependency-free SVG chart renderer: the same spec always yields byte-identical output (invariant
/// culture, fixed palette, no timestamps or random ids), so it is golden-hash testable. Output is bounded and every
/// piece of user text is XML-escaped; no scripts, links or external references are ever emitted.
/// </summary>
public static class ChartRenderer
{
    public const int MaxLabels = 100;
    public const int MaxSeries = 8;
    public const int MaxTitleCharacters = 120;
    public const int MaxLabelCharacters = 40;

    private static readonly string[] Palette = { "#2F6FEB", "#E8743B", "#19A979", "#945ECF", "#13A4B4", "#BF399E", "#6C8893", "#EE6868" };
    private const int Width = 800, Height = 480, Left = 70, Right = 30, Top = 60, Bottom = 80;

    public static string? Validate(ChartSpec spec)
    {
        string type = (spec.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (type is not ("bar" or "line" or "pie")) return "type must be bar, line or pie.";
        if (spec.Series.Count == 0) return "At least one series is required.";
        if (spec.Series.Count > MaxSeries) return "At most " + MaxSeries + " series are allowed.";
        if (spec.Labels.Count == 0) return "labels are required.";
        if (spec.Labels.Count > MaxLabels) return "At most " + MaxLabels + " labels are allowed.";
        if ((spec.Title ?? string.Empty).Length > MaxTitleCharacters) return "The title is longer than " + MaxTitleCharacters + " characters.";
        if (type == "pie" && spec.Series.Count != 1) return "A pie chart takes exactly one series.";
        foreach (ChartSeries s in spec.Series)
        {
            if (s.Values.Count != spec.Labels.Count) return "Series '" + s.Name + "' must have one value per label (" + spec.Labels.Count + ").";
            if (s.Values.Any(v => double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 1e15)) return "Series '" + s.Name + "' contains a value that is not a finite number.";
            if (type == "pie" && s.Values.Any(v => v < 0)) return "Pie values cannot be negative.";
        }
        if (type == "pie" && spec.Series[0].Values.Sum() <= 0) return "Pie values must sum to more than zero.";
        return null;
    }

    public static string RenderSvg(ChartSpec spec)
    {
        string? problem = Validate(spec);
        if (problem != null) throw new InvalidDataException(problem);
        var sb = new StringBuilder(4096);
        string title = Escape(spec.Title ?? string.Empty);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(Width).Append("\" height=\"").Append(Height)
          .Append("\" viewBox=\"0 0 ").Append(Width).Append(' ').Append(Height).Append("\" role=\"img\" aria-label=\"").Append(title).Append("\">\n");
        sb.Append("<title>").Append(title).Append("</title>\n<rect width=\"100%\" height=\"100%\" fill=\"#FFFFFF\"/>\n");
        sb.Append("<text x=\"").Append(Width / 2).Append("\" y=\"32\" text-anchor=\"middle\" font-family=\"Segoe UI, Arial, sans-serif\" font-size=\"20\" fill=\"#1F2937\">").Append(title).Append("</text>\n");
        switch (spec.Type.Trim().ToLowerInvariant())
        {
            case "pie": Pie(sb, spec); break;
            case "line": Axes(sb, spec, line: true); break;
            default: Axes(sb, spec, line: false); break;
        }
        Legend(sb, spec);
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static void Axes(StringBuilder sb, ChartSpec spec, bool line)
    {
        double min = Math.Min(0, spec.Series.SelectMany(s => s.Values).Min());
        double max = Math.Max(0, spec.Series.SelectMany(s => s.Values).Max());
        if (max - min < 1e-12) max = min + 1;
        double step = NiceStep((max - min) / 5);
        double lo = Math.Floor(min / step) * step, hi = Math.Ceiling(max / step) * step;
        int plotW = Width - Left - Right, plotH = Height - Top - Bottom;
        double Y(double v) => Top + plotH - (v - lo) / (hi - lo) * plotH;

        for (double tick = lo; tick <= hi + step / 2; tick += step)
        {
            sb.Append("<line x1=\"").Append(Left).Append("\" x2=\"").Append(Width - Right).Append("\" y1=\"").Append(F(Y(tick))).Append("\" y2=\"").Append(F(Y(tick))).Append("\" stroke=\"#E5E7EB\"/>\n");
            sb.Append("<text x=\"").Append(Left - 8).Append("\" y=\"").Append(F(Y(tick) + 4)).Append("\" text-anchor=\"end\" font-family=\"Segoe UI, Arial, sans-serif\" font-size=\"11\" fill=\"#4B5563\">").Append(Escape(Num(tick))).Append("</text>\n");
        }
        int n = spec.Labels.Count;
        double band = (double)plotW / n;
        int labelEvery = Math.Max(1, (int)Math.Ceiling(n / 20.0));
        for (int i = 0; i < n; i++)
        {
            if (i % labelEvery != 0) continue;
            double cx = Left + band * (i + 0.5);
            sb.Append("<text x=\"").Append(F(cx)).Append("\" y=\"").Append(Height - Bottom + 18).Append("\" text-anchor=\"middle\" font-family=\"Segoe UI, Arial, sans-serif\" font-size=\"11\" fill=\"#4B5563\">")
              .Append(Escape(Trunc(spec.Labels[i], MaxLabelCharacters))).Append("</text>\n");
        }
        if (!line)
        {
            int count = spec.Series.Count;
            double barW = Math.Max(1, band * 0.8 / count);
            for (int s = 0; s < count; s++)
                for (int i = 0; i < n; i++)
                {
                    double v = spec.Series[s].Values[i];
                    double x = Left + band * i + band * 0.1 + barW * s;
                    double y0 = Y(Math.Max(0, lo)), y1 = Y(v);
                    sb.Append("<rect x=\"").Append(F(x)).Append("\" y=\"").Append(F(Math.Min(y0, y1))).Append("\" width=\"").Append(F(barW)).Append("\" height=\"").Append(F(Math.Abs(y0 - y1)))
                      .Append("\" fill=\"").Append(Palette[s % Palette.Length]).Append("\"/>\n");
                }
        }
        else
        {
            for (int s = 0; s < spec.Series.Count; s++)
            {
                var points = new StringBuilder();
                for (int i = 0; i < n; i++)
                    points.Append(i == 0 ? "" : " ").Append(F(Left + band * (i + 0.5))).Append(',').Append(F(Y(spec.Series[s].Values[i])));
                sb.Append("<polyline fill=\"none\" stroke=\"").Append(Palette[s % Palette.Length]).Append("\" stroke-width=\"2\" points=\"").Append(points).Append("\"/>\n");
            }
        }
        sb.Append("<line x1=\"").Append(Left).Append("\" x2=\"").Append(Width - Right).Append("\" y1=\"").Append(F(Y(Math.Max(lo, Math.Min(0, hi))))).Append("\" y2=\"").Append(F(Y(Math.Max(lo, Math.Min(0, hi))))).Append("\" stroke=\"#6B7280\"/>\n");
    }

    private static void Pie(StringBuilder sb, ChartSpec spec)
    {
        List<double> values = spec.Series[0].Values;
        double total = values.Sum();
        double cx = Width / 2.0 - 100, cy = Top + (Height - Top - 20) / 2.0, r = Math.Min(Width / 2.0 - 160, (Height - Top - 40) / 2.0);
        double angle = -Math.PI / 2;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] <= 0) continue;
            double sweep = values[i] / total * 2 * Math.PI;
            string color = Palette[i % Palette.Length];
            if (sweep >= 2 * Math.PI - 1e-9)
                sb.Append("<circle cx=\"").Append(F(cx)).Append("\" cy=\"").Append(F(cy)).Append("\" r=\"").Append(F(r)).Append("\" fill=\"").Append(color).Append("\"/>\n");
            else
            {
                double x1 = cx + r * Math.Cos(angle), y1 = cy + r * Math.Sin(angle);
                double x2 = cx + r * Math.Cos(angle + sweep), y2 = cy + r * Math.Sin(angle + sweep);
                sb.Append("<path d=\"M").Append(F(cx)).Append(' ').Append(F(cy)).Append(" L").Append(F(x1)).Append(' ').Append(F(y1))
                  .Append(" A").Append(F(r)).Append(' ').Append(F(r)).Append(" 0 ").Append(sweep > Math.PI ? 1 : 0).Append(" 1 ").Append(F(x2)).Append(' ').Append(F(y2))
                  .Append(" Z\" fill=\"").Append(color).Append("\" stroke=\"#FFFFFF\"/>\n");
            }
            angle += sweep;
        }
    }

    private static void Legend(StringBuilder sb, ChartSpec spec)
    {
        bool pie = spec.Type.Trim().Equals("pie", StringComparison.OrdinalIgnoreCase);
        int count = pie ? Math.Min(spec.Labels.Count, 20) : spec.Series.Count;
        int x = pie ? Width - 190 : Left;
        for (int i = 0; i < count; i++)
        {
            int y = pie ? 70 + i * 20 : Height - 28;
            int xi = pie ? x : x + i * 140;
            string name = pie ? spec.Labels[i] : (spec.Series[i].Name.Length == 0 ? "Series " + (i + 1) : spec.Series[i].Name);
            sb.Append("<rect x=\"").Append(xi).Append("\" y=\"").Append(y - 10).Append("\" width=\"12\" height=\"12\" fill=\"").Append(Palette[i % Palette.Length]).Append("\"/>\n");
            sb.Append("<text x=\"").Append(xi + 18).Append("\" y=\"").Append(y).Append("\" font-family=\"Segoe UI, Arial, sans-serif\" font-size=\"12\" fill=\"#374151\">").Append(Escape(Trunc(name, 18))).Append("</text>\n");
        }
    }

    private static double NiceStep(double raw)
    {
        double exp = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double f = raw / exp;
        double nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nice * exp;
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c switch
            {
                '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&apos;",
                _ when char.IsControl(c) => " ",
                _ => c.ToString()
            });
        return sb.ToString();
    }
}
