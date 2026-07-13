using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BoxPilot.Core.Models;

namespace BoxPilot.App.Controls;

public sealed class TrafficChart : Control
{
    private const int MinimumVisibleSampleSlots = 60;
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

    static TrafficChart()
    {
        AffectsRender<TrafficChart>(
            SamplesProperty,
            DownloadStrokeProperty,
            UploadStrokeProperty,
            GridStrokeProperty);
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

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Math.Max(0, Bounds.Width - 2);
        var height = Math.Max(0, Bounds.Height - 2);
        if (width <= 0 || height <= 0)
            return;

        DrawGrid(context, width, height);
        if (Samples is not { Count: > 0 } samples)
            return;

        long maximum = 0;
        foreach (var sample in samples)
        {
            maximum = Math.Max(maximum, sample.DownloadBytesPerSecond);
            maximum = Math.Max(maximum, sample.UploadBytesPerSecond);
        }
        var scale = Math.Max(1024, maximum) * 1.1;
        var slots = Math.Max(MinimumVisibleSampleSlots, samples.Count);
        var step = width / (slots - 1);
        var start = 1 + width - (samples.Count - 1) * step;

        DrawSeries(
            context,
            samples,
            static sample => sample.DownloadBytesPerSecond,
            DownloadStroke,
            start,
            step,
            height,
            scale);
        DrawSeries(
            context,
            samples,
            static sample => sample.UploadBytesPerSecond,
            UploadStroke,
            start,
            step,
            height,
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

    private void DrawGrid(DrawingContext context, double width, double height)
    {
        if (GridStroke is null)
            return;

        var pen = new Pen(GridStroke, 0.75);
        for (var row = 1; row <= 3; row++)
        {
            var y = 1 + height * row / 4;
            context.DrawLine(pen, new Point(1, y), new Point(1 + width, y));
        }
    }

    private static void DrawSeries(
        DrawingContext context,
        IReadOnlyList<TrafficSnapshot> samples,
        Func<TrafficSnapshot, long> selectValue,
        IBrush? stroke,
        double start,
        double step,
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
                1 + height - height * value / scale);
            if (previous is { } last)
                context.DrawLine(pen, last, point);
            previous = point;
        }

        if (previous is { } latest)
            context.DrawEllipse(stroke, null, latest, 2.25, 2.25);
    }
}
