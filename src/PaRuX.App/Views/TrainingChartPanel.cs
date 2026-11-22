using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Lightweight native chart stack for a training run: one small Polyline chart per metric, no
    /// third-party chart dependency. All data shaping (filtering, min/max downsampling, scaling and the
    /// text alternative) lives in <see cref="TrainingChartModel"/>; this class only draws. Each chart
    /// exposes its text alternative through AutomationProperties.Name and a visible caption line, so the
    /// numbers are available without sight. A series with no data is not drawn at all (no fake zeros).
    /// </summary>
    internal sealed class TrainingChartPanel : StackPanel
    {
        private const double PlotWidth = 320, PlotHeight = 56;

        private static string T(string en, string it) => UiText.Pick(en, it);

        public TrainingChartPanel()
        {
            Spacing = 6;
            AutomationProperties.SetName(this, T("Training charts", "Grafici di training"));
        }

        public void Render(IReadOnlyList<TrainingMetricSample> history)
        {
            Children.Clear();
            history ??= Array.Empty<TrainingMetricSample>();
            (TrainingChartModel.SeriesKind Kind, string En, string It, Func<TrainingMetricSample, double?> Pick)[] defs =
            {
                (TrainingChartModel.SeriesKind.Loss, "Loss", "Perdita", s => s.Loss),
                (TrainingChartModel.SeriesKind.GradientNorm, "Gradient norm", "Norma del gradiente", s => s.GradientNorm),
                (TrainingChartModel.SeriesKind.LearningRate, "Learning rate", "Learning rate", s => s.LearningRate),
                (TrainingChartModel.SeriesKind.TokensPerSecond, "Tokens per second", "Token al secondo", s => s.TokensPerSecond),
                (TrainingChartModel.SeriesKind.GpuUtilPercent, "GPU utilisation %", "Utilizzo GPU %", s => s.GpuUtilPercent),
                (TrainingChartModel.SeriesKind.VramMib, "VRAM (MiB)", "VRAM (MiB)", s => s.VramMib),
            };
            int shown = 0;
            foreach (var def in defs)
            {
                TrainingChartModel.Series series = TrainingChartModel.Build(
                    def.Kind, history.Select(h => ((double)h.Step, def.Pick(h))));
                if (!series.HasData) continue;
                Children.Add(BuildChart(series, T(def.En, def.It)));
                shown++;
            }
            if (shown == 0)
                Children.Add(new TextBlock
                {
                    Text = T("No metrics logged for this run yet.", "Nessuna metrica registrata per questa esecuzione."),
                    Opacity = 0.7, TextWrapping = TextWrapping.Wrap
                });
        }

        private static FrameworkElement BuildChart(TrainingChartModel.Series series, string label)
        {
            string alt = UiText.Pick(
                TrainingChartModel.DescribeEn(series, label),
                TrainingChartModel.DescribeIt(series, label));

            var plot = new Canvas { Width = PlotWidth, Height = PlotHeight };
            var line = new Polyline
            {
                StrokeThickness = 1.5,
                Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                StrokeLineJoin = PenLineJoin.Round
            };
            var points = new PointCollection();
            foreach ((double x, double y) in TrainingChartModel.ToPixels(series, PlotWidth, PlotHeight))
                points.Add(new Windows.Foundation.Point(x, y));
            line.Points = points;
            plot.Children.Add(line);

            var frame = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                Child = plot,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            // The graphic is decorative to assistive tech; the caption below carries the same facts.
            AutomationProperties.SetAccessibilityView(frame, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

            var caption = new TextBlock { Text = alt, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(caption, alt);

            var group = new StackPanel { Spacing = 2 };
            group.Children.Add(frame);
            group.Children.Add(caption);
            AutomationProperties.SetName(group, alt);
            return group;
        }
    }
}
