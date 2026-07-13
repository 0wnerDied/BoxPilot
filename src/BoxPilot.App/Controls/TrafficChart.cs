using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BoxPilot.Core.Models;

namespace BoxPilot.App.Controls;

public sealed class TrafficChart : Control
{
    private const int MinimumVisibleSampleSlots = 60;
    private const double AxisFontSize = 9;
    private const double AxisGap = 5;
    private const double BottomAxisHeight = 14;
    private static readonly string[] RateUnits = ["B/s", "KiB/s", "MiB/s", "GiB/s", "TiB/s"];
    private static readonly Typeface AxisTypeface = new(FontFamily.Default);
    private INotifyCollectionChanged? observedSamples;
    private bool isAttached;

    public static readonly StyledProperty<IReadOnlyList<TrafficSnapshot>?> SamplesProperty =
        AvaloniaProperty.Register<TrafficChart, IReadOnlyList<TrafficSnapshot>?>(nameof(Samples));

    public static readonly StyledProperty<IBrush?> DownloadStrokeProperty =
        AvaloniaProperty.Register<TrafficChart, IBrush?>(nameof(DownloadStroke));

    public static readonly StyledProperty<IBrush?> UploadStrokeProperty =
        AvaloniaProperty.Register<TrafficChart, IBrush?>(nameof(UploadStroke));

    public static readonly StyledProperty<IBrush?> GridStrokeProperty =
        AvaloniaProperty.Register<TrafficChart, IBrush?>(nameof(GridStroke));

    public static readonly StyledProperty<IBrush?> AxisForegroundProperty =
        AvaloniaProperty.Register<TrafficChart, IBrush?>(nameof(AxisForeground));

    static TrafficChart()
    {
        AffectsRender<TrafficChart>(
            SamplesProperty,
            DownloadStrokeProperty,
            UploadStrokeProperty,
            GridStrokeProperty,
            AxisForegroundProperty);
        SamplesProperty.Changed.AddClassHandler<TrafficChart>(
            static (chart, _) => chart.ObserveSamples(chart.isAttached ? chart.Samples : null));
    }

    public TrafficChart()
    {
        ClipToBounds = true;
        AttachedToVisualTree += (_, _) =>
        {
            isAttached = true;
            ObserveSamples(Samples);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            isAttached = false;
            ObserveSamples(null);
        };
    }

    public IReadOnlyList<TrafficSnapshot>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IBrush? DownloadStroke
    {
        get => GetValue(DownloadStrokeProperty);
        set => SetValue(DownloadStrokeProperty, value);
    }

    public IBrush? UploadStroke
    {
        get => GetValue(UploadStrokeProperty);
        set => SetValue(UploadStrokeProperty, value);
    }

    public IBrush? GridStroke
    {
        get => GetValue(GridStrokeProperty);
        set => SetValue(GridStrokeProperty, value);
    }

    public IBrush? AxisForeground
    {
        get => GetValue(AxisForegroundProperty);
        set => SetValue(AxisForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Math.Max(0, Bounds.Width);
        var height = Math.Max(0, Bounds.Height);
        if (width <= 0 || height <= BottomAxisHeight)
            return;

        long maximum = 0;
        if (Samples is { } measuredSamples)
        {
            foreach (var sample in measuredSamples)
            {
                maximum = Math.Max(maximum, sample.DownloadBytesPerSecond);
                maximum = Math.Max(maximum, sample.UploadBytesPerSecond);
            }
        }

        var scale = CalculateScale(maximum);
        var axisWidth = MeasureAxisWidth(scale);
        var plotLeft = axisWidth;
        var plotTop = 1d;
        var plotWidth = Math.Max(0, width - plotLeft - 1);
        var plotHeight = Math.Max(0, height - BottomAxisHeight - plotTop);
        if (plotWidth <= 0 || plotHeight <= 0)
            return;

        DrawAxes(context, plotLeft, plotTop, plotWidth, plotHeight, scale);
        if (Samples is not { Count: > 0 } samples)
            return;

        var slots = Math.Max(MinimumVisibleSampleSlots, samples.Count);
        var step = plotWidth / (slots - 1);
        var start = plotLeft + plotWidth - (samples.Count - 1) * step;

        DrawSeries(
            context,
            samples,
            static sample => sample.DownloadBytesPerSecond,
            DownloadStroke,
            start,
            step,
            plotTop,
            plotHeight,
            scale);
        DrawSeries(
            context,
            samples,
            static sample => sample.UploadBytesPerSecond,
            UploadStroke,
            start,
            step,
            plotTop,
            plotHeight,
            scale);
    }

    private void ObserveSamples(IReadOnlyList<TrafficSnapshot>? samples)
    {
        if (observedSamples is not null)
            observedSamples.CollectionChanged -= OnSamplesChanged;
        observedSamples = samples as INotifyCollectionChanged;
        if (observedSamples is not null)
            observedSamples.CollectionChanged += OnSamplesChanged;
        InvalidateVisual();
    }

    private void OnSamplesChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        InvalidateVisual();
    }

    private double MeasureAxisWidth(double scale)
    {
        if (AxisForeground is null)
            return 0;

        var maximum = CreateAxisText(FormatRate(scale));
        var midpoint = CreateAxisText(FormatRate(scale / 2));
        var zero = CreateAxisText("0 B/s");
        return Math.Ceiling(Math.Max(maximum.Width, Math.Max(midpoint.Width, zero.Width)) + AxisGap);
    }

    private void DrawAxes(
        DrawingContext context,
        double left,
        double top,
        double width,
        double height,
        double scale)
    {
        var right = left + width;
        var bottom = top + height;

        if (GridStroke is not null)
        {
            var gridPen = new Pen(GridStroke, 0.75);
            context.DrawLine(gridPen, new Point(left, top), new Point(right, top));
            context.DrawLine(
                gridPen,
                new Point(left, top + height / 2),
                new Point(right, top + height / 2));
            context.DrawLine(
                gridPen,
                new Point(left + width / 2, top),
                new Point(left + width / 2, bottom));

            var axisPen = new Pen(GridStroke, 1);
            context.DrawLine(axisPen, new Point(left, top), new Point(left, bottom));
            context.DrawLine(axisPen, new Point(left, bottom), new Point(right, bottom));
        }

        if (AxisForeground is null)
            return;

        DrawYAxisLabel(context, FormatRate(scale), left, top, top);
        DrawYAxisLabel(
            context,
            FormatRate(scale / 2),
            left,
            top + height / 2,
            top);
        DrawYAxisLabel(context, "0 B/s", left, bottom, top);
        DrawXAxisLabel(context, "−60 s", left, bottom, alignRight: false);
        DrawXAxisLabel(context, "−30 s", left + width / 2, bottom, alignRight: false, center: true);
        DrawXAxisLabel(context, "0 s", right, bottom, alignRight: true);
    }

    private void DrawYAxisLabel(
        DrawingContext context,
        string label,
        double axisX,
        double axisY,
        double top)
    {
        var text = CreateAxisText(label);
        var x = Math.Max(0, axisX - AxisGap - text.Width);
        var y = Math.Max(top - 1, axisY - text.Height / 2);
        context.DrawText(text, new Point(x, y));
    }

    private void DrawXAxisLabel(
        DrawingContext context,
        string label,
        double axisX,
        double axisY,
        bool alignRight,
        bool center = false)
    {
        var text = CreateAxisText(label);
        var x = alignRight
            ? axisX - text.Width
            : center
                ? axisX - text.Width / 2
                : axisX;
        context.DrawText(text, new Point(Math.Max(0, x), axisY + 2));
    }

    private FormattedText CreateAxisText(string text)
    {
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            AxisTypeface,
            AxisFontSize,
            AxisForeground!);
    }

    private static double CalculateScale(long maximum)
    {
        var target = Math.Max(1024, maximum * 1.1);
        var unitPower = Math.Max(0, Math.Floor(Math.Log(target, 1024)));
        var unit = Math.Pow(1024, unitPower);
        var normalized = target / unit;
        var multiplier = Math.Pow(2, Math.Ceiling(Math.Log(normalized, 2)));
        return Math.Max(1024, multiplier * unit);
    }

    private static string FormatRate(double bytesPerSecond)
    {
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (value >= 1024 && unit < RateUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {RateUnits[unit]}";
    }

    private static void DrawSeries(
        DrawingContext context,
        IReadOnlyList<TrafficSnapshot> samples,
        Func<TrafficSnapshot, long> selectValue,
        IBrush? stroke,
        double start,
        double step,
        double top,
        double height,
        double scale)
    {
        if (stroke is null)
            return;

        var pen = new Pen(stroke, 1.75);
        Point? previous = null;
        for (var index = 0; index < samples.Count; index++)
        {
            var value = Math.Max(0, selectValue(samples[index]));
            var point = new Point(
                start + index * step,
                top + height - height * value / scale);
            if (previous is { } last)
                context.DrawLine(pen, last, point);
            previous = point;
        }

        if (previous is { } latest)
            context.DrawEllipse(stroke, null, latest, 2.25, 2.25);
    }
}
