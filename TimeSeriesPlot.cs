using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace PerfmonNG;

public sealed class TimeSeriesPlot : FrameworkElement
{
    private const double LeftMargin = 82;
    private const double RightMargin = 24;
    private static readonly long[] TimeTickIntervalTicks =
    [
        TimeSpan.FromMilliseconds(1).Ticks,
        TimeSpan.FromMilliseconds(2).Ticks,
        TimeSpan.FromMilliseconds(5).Ticks,
        TimeSpan.FromMilliseconds(10).Ticks,
        TimeSpan.FromMilliseconds(20).Ticks,
        TimeSpan.FromMilliseconds(50).Ticks,
        TimeSpan.FromMilliseconds(100).Ticks,
        TimeSpan.FromMilliseconds(200).Ticks,
        TimeSpan.FromMilliseconds(500).Ticks,
        TimeSpan.FromSeconds(1).Ticks,
        TimeSpan.FromSeconds(2).Ticks,
        TimeSpan.FromSeconds(5).Ticks,
        TimeSpan.FromSeconds(10).Ticks,
        TimeSpan.FromSeconds(15).Ticks,
        TimeSpan.FromSeconds(30).Ticks,
        TimeSpan.FromMinutes(1).Ticks,
        TimeSpan.FromMinutes(2).Ticks,
        TimeSpan.FromMinutes(5).Ticks,
        TimeSpan.FromMinutes(10).Ticks,
        TimeSpan.FromMinutes(15).Ticks,
        TimeSpan.FromMinutes(30).Ticks,
        TimeSpan.FromHours(1).Ticks,
        TimeSpan.FromHours(2).Ticks,
        TimeSpan.FromHours(3).Ticks,
        TimeSpan.FromHours(6).Ticks,
        TimeSpan.FromHours(12).Ticks,
        TimeSpan.FromDays(1).Ticks,
        TimeSpan.FromDays(2).Ticks,
        TimeSpan.FromDays(7).Ticks,
        TimeSpan.FromDays(14).Ticks,
        TimeSpan.FromDays(30).Ticks,
        TimeSpan.FromDays(90).Ticks,
        TimeSpan.FromDays(180).Ticks,
        TimeSpan.FromDays(365).Ticks
    ];

    private PerformanceChartData? _chartData;
    private int _viewportStartBucket;
    private int _hoveredBucket = -1;
    private double? _hoverX;
    private HashSet<string>? _visibleSeriesNames;
    private readonly ObservableCollection<HoverCounterSummary> _hoverRows = [];
    private readonly TextBlock _hoverTimeText;
    private readonly ToolTip _hoverToolTip;

    public int TotalBucketCount => _chartData?.TotalBucketCount ?? 0;
    public int ViewportStartBucket => _viewportStartBucket;

    public int VisibleBucketCount => Math.Max(
        1,
        (int)((ActualWidth - LeftMargin - RightMargin) / 3.5));

    public event EventHandler? VisibleRangeChanged;

    public TimeSeriesPlot()
    {
        _hoverTimeText = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(35, 48, 40))
        };

        var counterTable = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserReorderColumns = false,
            CanUserResizeRows = false,
            CanUserSortColumns = false,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            IsReadOnly = true,
            ItemsSource = _hoverRows,
            MaxHeight = 340,
            RowHeaderWidth = 0,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            Width = 650
        };
        counterTable.Columns.Add(new DataGridTextColumn
        {
            Header = "Counter",
            Binding = new Binding(nameof(HoverCounterSummary.Counter)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 180
        });
        counterTable.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Colour",
            CellTemplate = (DataTemplate)Application.Current.FindResource("CounterColorSwatchTemplate"),
            Width = 58
        });
        counterTable.Columns.Add(new DataGridTextColumn
        {
            Header = "Unit",
            Binding = new Binding(nameof(HoverCounterSummary.Unit)),
            Width = 75
        });
        counterTable.Columns.Add(CreateNumericColumn("Mean", nameof(HoverCounterSummary.Mean), 90));
        counterTable.Columns.Add(CreateNumericColumn("Max", nameof(HoverCounterSummary.Maximum), 90));
        counterTable.Columns.Add(new DataGridTextColumn
        {
            Header = "Num points",
            Binding = new Binding(nameof(HoverCounterSummary.SampleCount)),
            Width = 90
        });

        var tooltipContent = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(250, 252, 249)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(177, 190, 179)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    _hoverTimeText,
                    counterTable
                }
            },
            Padding = new Thickness(12)
        };
        _hoverToolTip = new ToolTip
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = tooltipContent,
            HasDropShadow = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.RelativePoint,
            PlacementTarget = this,
            StaysOpen = true
        };
    }

    public void SetChartData(PerformanceChartData chartData, bool resetViewport = true)
    {
        _chartData = chartData;
        if (resetViewport)
        {
            _viewportStartBucket = 0;
        }

        _hoveredBucket = -1;
        _hoverX = null;
        _hoverToolTip.IsOpen = false;
        InvalidateVisual();
        VisibleRangeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetVisibleSeries(IEnumerable<string> seriesNames)
    {
        var visibleNames = new HashSet<string>(seriesNames, StringComparer.Ordinal);
        if (_visibleSeriesNames is not null && _visibleSeriesNames.SetEquals(visibleNames))
        {
            return;
        }

        _visibleSeriesNames = visibleNames;
        CloseHoverToolTip();
        InvalidateVisual();
    }

    public void SetViewportStartBucket(int startBucket)
    {
        var maxStart = Math.Max(0, TotalBucketCount - VisibleBucketCount);
        var nextStartBucket = Math.Clamp(startBucket, 0, maxStart);
        if (_viewportStartBucket == nextStartBucket)
        {
            return;
        }

        _viewportStartBucket = nextStartBucket;
        CloseHoverToolTip();
        InvalidateVisual();
        VisibleRangeChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var maxStart = Math.Max(0, TotalBucketCount - VisibleBucketCount);
        _viewportStartBucket = Math.Clamp(_viewportStartBucket, 0, maxStart);
        VisibleRangeChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        const double top = 24;
        const double bottom = 72;
        var plotWidth = width - LeftMargin - RightMargin;
        var plotHeight = height - top - bottom;

        if (plotWidth <= 0 || plotHeight <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(229, 235, 229)), 1);
        var minorGridPen = new Pen(new SolidColorBrush(Color.FromRgb(241, 244, 241)), 0.75);
        var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(116, 130, 119)), 1);
        var labelBrush = new SolidColorBrush(Color.FromRgb(91, 105, 95));
        var titleBrush = new SolidColorBrush(Color.FromRgb(43, 59, 48));
        var emptyBrush = new SolidColorBrush(Color.FromRgb(126, 139, 129));
        var data = _chartData;
        var yMinimum = data?.YAxisMinimum ?? 0;
        var yMaximum = data?.YAxisMaximum ?? 100;
        var startTime = GetViewportStartTime(data);
        var endTime = GetViewportEndTime(data, startTime);
        var timeTicks = Math.Max(1, (endTime - startTime).Ticks);
        var plotLeft = LeftMargin;
        var plotTop = top;
        var plotRight = plotLeft + plotWidth;
        var plotBottom = top + plotHeight;

        for (var index = 0; index <= 4; index++)
        {
            var y = plotTop + plotHeight * index / 4;
            drawingContext.DrawLine(gridPen, new Point(plotLeft, y), new Point(plotRight, y));

            var value = (yMaximum - (yMaximum - yMinimum) * index / 4)
                .ToString("0.##", CultureInfo.CurrentCulture);
            var tick = CreateText(value, 11, labelBrush, dpi);
            drawingContext.DrawText(tick, new Point(plotLeft - tick.Width - 12, y - tick.Height / 2));
        }

        DrawTimeAxis(
            drawingContext,
            startTime,
            endTime,
            timeTicks,
            plotLeft,
            plotTop,
            plotBottom,
            plotWidth,
            dpi,
            gridPen,
            minorGridPen,
            axisPen,
            labelBrush);

        drawingContext.DrawLine(axisPen, new Point(plotLeft, plotTop), new Point(plotLeft, plotBottom));
        drawingContext.DrawLine(axisPen, new Point(plotLeft, plotBottom), new Point(plotRight, plotBottom));

        if (data is not null)
        {
            DrawSeries(drawingContext, data, startTime, endTime, plotLeft, plotTop, plotWidth, plotHeight, timeTicks);
            DrawHoverHighlights(
                drawingContext,
                data,
                startTime,
                timeTicks,
                plotLeft,
                plotTop,
                plotWidth,
                plotHeight);
        }

        var xTitle = CreateText("Time (UTC)", 12, titleBrush, dpi);
        drawingContext.DrawText(xTitle, new Point(width / 2 - xTitle.Width / 2, height - 15));

        var yTitle = CreateText("Scaled value", 12, titleBrush, dpi);
        var centerX = LeftMargin / 2;
        var centerY = plotTop + plotHeight / 2;
        drawingContext.PushTransform(new RotateTransform(-90, centerX, centerY));
        drawingContext.DrawText(yTitle, new Point(centerX - yTitle.Width / 2, centerY - yTitle.Height / 2));
        drawingContext.Pop();

        if (data is null || data.Series.Count == 0)
        {
            var message = data is null ? "No series loaded" : "No valid samples in this file";
            var emptyMessage = CreateText(message, 14, emptyBrush, dpi);
            drawingContext.DrawText(emptyMessage, new Point(
                plotLeft + plotWidth / 2 - emptyMessage.Width / 2,
                plotTop + plotHeight / 2 - emptyMessage.Height / 2));
        }
    }

    private static void DrawTimeAxis(
        DrawingContext drawingContext,
        DateTime startTime,
        DateTime endTime,
        long timeTicks,
        double plotLeft,
        double plotTop,
        double plotBottom,
        double plotWidth,
        double pixelsPerDip,
        Pen majorGridPen,
        Pen minorGridPen,
        Pen minorTickPen,
        Brush labelBrush)
    {
        var targetMajorCount = Math.Max(2, (int)(plotWidth / 112));
        var desiredInterval = Math.Max(1, timeTicks / targetMajorCount);
        var majorInterval = TimeTickIntervalTicks.FirstOrDefault(interval => interval >= desiredInterval);
        if (majorInterval == 0)
        {
            majorInterval = TimeTickIntervalTicks[^1];
        }

        var minorInterval = Math.Max(TimeSpan.TicksPerMillisecond, majorInterval / 5);
        var firstTick = startTime.Ticks / minorInterval * minorInterval;
        if (firstTick < startTime.Ticks)
        {
            firstTick += minorInterval;
        }

        var useDate = startTime.Date != endTime.Date || endTime - startTime >= TimeSpan.FromDays(1);
        var labelFormat = useDate ? "MM-dd HH:mm:ss" : "HH:mm:ss";
        var plotRight = plotLeft + plotWidth;

        for (var tickTime = firstTick; tickTime <= endTime.Ticks; tickTime += minorInterval)
        {
            var x = plotLeft + (tickTime - startTime.Ticks) / (double)timeTicks * plotWidth;
            if (tickTime % majorInterval == 0)
            {
                drawingContext.DrawLine(majorGridPen, new Point(x, plotTop), new Point(x, plotBottom));
                var label = CreateText(
                    new DateTime(tickTime, startTime.Kind).ToString(labelFormat, CultureInfo.CurrentCulture),
                    11,
                    labelBrush,
                    pixelsPerDip);
                var labelX = Math.Clamp(x - label.Width / 2, plotLeft, plotRight - label.Width);
                drawingContext.DrawText(label, new Point(labelX, plotBottom + 9));
                drawingContext.DrawLine(minorTickPen, new Point(x, plotBottom), new Point(x, plotBottom + 5));
            }
            else
            {
                drawingContext.DrawLine(minorGridPen, new Point(x, plotTop), new Point(x, plotBottom));
                drawingContext.DrawLine(minorTickPen, new Point(x, plotBottom), new Point(x, plotBottom + 3));
            }
        }

        var startDate = CreateText(startTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 9, labelBrush, pixelsPerDip);
        var startClock = CreateText(startTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture), 9, labelBrush, pixelsPerDip);
        var endDate = CreateText(endTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 9, labelBrush, pixelsPerDip);
        var endClock = CreateText(endTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture), 9, labelBrush, pixelsPerDip);
        drawingContext.DrawText(startDate, new Point(plotLeft, plotBottom + 26));
        drawingContext.DrawText(startClock, new Point(plotLeft, plotBottom + 38));
        drawingContext.DrawText(endDate, new Point(plotRight - endDate.Width, plotBottom + 26));
        drawingContext.DrawText(endClock, new Point(plotRight - endClock.Width, plotBottom + 38));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var data = _chartData;
        var pointer = e.GetPosition(this);
        if (data is null || !TryGetBucketAt(pointer, data, out var bucket))
        {
            CloseHoverToolTip();
            return;
        }

        var xChanged = _hoverX is null || Math.Abs(_hoverX.Value - pointer.X) >= 1;
        var bucketChanged = _hoveredBucket != bucket;
        _hoverX = pointer.X;
        if (bucketChanged)
        {
            UpdateHoverRows(data, bucket);
            _hoveredBucket = bucket;
        }

        _hoverToolTip.HorizontalOffset = pointer.X + 14;
        _hoverToolTip.VerticalOffset = pointer.Y + 14;
        _hoverToolTip.IsOpen = true;
        if (xChanged || bucketChanged)
        {
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        CloseHoverToolTip();
    }

    private void DrawSeries(
        DrawingContext drawingContext,
        PerformanceChartData data,
        DateTime viewStart,
        DateTime viewEnd,
        double plotLeft,
        double plotTop,
        double plotWidth,
        double plotHeight,
        long timeTicks)
    {
        drawingContext.PushClip(new RectangleGeometry(new Rect(plotLeft, plotTop, plotWidth, plotHeight)));

        foreach (var series in data.Series)
        {
            if (series.Points.Count == 0 || !IsSeriesVisible(series))
            {
                continue;
            }

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var firstPointIndex = FindFirstPointAtOrAfter(series.Points, viewStart);
                var isFirstPoint = true;
                for (var pointIndex = firstPointIndex; pointIndex < series.Points.Count; pointIndex++)
                {
                    var point = series.Points[pointIndex];
                    if (point.Timestamp > viewEnd)
                    {
                        break;
                    }

                    var x = plotLeft + (point.Timestamp - viewStart).Ticks
                        / (double)timeTicks * plotWidth;
                    var scaledValue = point.Value / series.ScaleFactor;
                    var y = plotTop + (data.YAxisMaximum - scaledValue)
                        / (data.YAxisMaximum - data.YAxisMinimum) * plotHeight;
                    var position = new Point(x, y);

                    if (isFirstPoint)
                    {
                        context.BeginFigure(position, isFilled: false, isClosed: false);
                        isFirstPoint = false;
                    }
                    else
                    {
                        context.LineTo(position, isStroked: true, isSmoothJoin: false);
                    }
                }
            }

            geometry.Freeze();
            var pen = new Pen(new SolidColorBrush(series.Color), 1.5);
            pen.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }

        drawingContext.Pop();
    }

    private void DrawHoverHighlights(
        DrawingContext drawingContext,
        PerformanceChartData data,
        DateTime viewStart,
        long timeTicks,
        double plotLeft,
        double plotTop,
        double plotWidth,
        double plotHeight)
    {
        if (_hoveredBucket < 0 || _hoverX is not double hoverX)
        {
            return;
        }

        var plotRight = plotLeft + plotWidth;
        hoverX = Math.Clamp(hoverX, plotLeft, plotRight);
        var guidePen = new Pen(new SolidColorBrush(Color.FromArgb(210, 45, 63, 50)), 1)
        {
            DashStyle = DashStyles.Dash
        };
        guidePen.Freeze();
        drawingContext.DrawLine(guidePen, new Point(hoverX, plotTop), new Point(hoverX, plotTop + plotHeight));
        drawingContext.PushClip(new RectangleGeometry(new Rect(plotLeft, plotTop, plotWidth, plotHeight)));

        foreach (var series in data.Series)
        {
            if (!IsSeriesVisible(series))
            {
                continue;
            }

            var sample = FindPointForBucket(series.Points, _hoveredBucket);
            if (sample is not { } point)
            {
                continue;
            }

            var x = plotLeft + (point.Timestamp - viewStart).Ticks / (double)timeTicks * plotWidth;
            var scaledValue = point.Value / series.ScaleFactor;
            var y = plotTop + (data.YAxisMaximum - scaledValue)
                / (data.YAxisMaximum - data.YAxisMinimum) * plotHeight;
            var markerPen = new Pen(Brushes.White, 1.5);
            markerPen.Freeze();
            drawingContext.DrawEllipse(
                new SolidColorBrush(series.Color),
                markerPen,
                new Point(x, y),
                5,
                5);
        }

        drawingContext.Pop();
    }

    private DateTime GetViewportStartTime(PerformanceChartData? data)
    {
        if (data is null)
        {
            return DateTime.Today;
        }

        var offset = (long)_viewportStartBucket * data.AggregationInterval.Ticks;
        var duration = Math.Max(0, (data.TimeRange.LastTimePoint - data.TimeRange.FirstTimePoint).Ticks);
        return data.TimeRange.FirstTimePoint.AddTicks(Math.Min(offset, duration));
    }

    private DateTime GetViewportEndTime(PerformanceChartData? data, DateTime startTime)
    {
        if (data is null)
        {
            return startTime.AddMinutes(1);
        }

        var bucketEnd = Math.Min(
            data.TotalBucketCount,
            _viewportStartBucket + VisibleBucketCount);
        var endOffset = (long)bucketEnd * data.AggregationInterval.Ticks;
        var duration = Math.Max(0, (data.TimeRange.LastTimePoint - data.TimeRange.FirstTimePoint).Ticks);
        var endTime = data.TimeRange.FirstTimePoint.AddTicks(Math.Min(endOffset, duration));
        return endTime > startTime
            ? endTime
            : startTime.Add(data.AggregationInterval);
    }

    private static int FindFirstPointAtOrAfter(IReadOnlyList<PerformancePlotPoint> points, DateTime timestamp)
    {
        var low = 0;
        var high = points.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (points[middle].Timestamp < timestamp)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static DataGridTextColumn CreateNumericColumn(string header, string property, double width)
    {
        return new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(property)
            {
                StringFormat = "N2",
                TargetNullValue = "-"
            },
            Width = width
        };
    }

    private bool TryGetBucketAt(Point pointer, PerformanceChartData data, out int bucket)
    {
        var plotWidth = ActualWidth - LeftMargin - RightMargin;
        if (data.TotalBucketCount <= 0 || plotWidth <= 0)
        {
            bucket = -1;
            return false;
        }

        var viewStart = GetViewportStartTime(data);
        var viewEnd = GetViewportEndTime(data, viewStart);
        var clampedX = Math.Clamp(pointer.X, LeftMargin, ActualWidth - RightMargin);
        var proportion = (clampedX - LeftMargin) / plotWidth;
        var pointerTime = viewStart.AddTicks((long)((viewEnd - viewStart).Ticks * proportion));
        var offsetTicks = Math.Max(0, (pointerTime - data.TimeRange.FirstTimePoint).Ticks);
        bucket = (int)Math.Clamp(
            offsetTicks / data.AggregationInterval.Ticks,
            0,
            data.TotalBucketCount - 1L);
        return data.TotalBucketCount > 0;
    }

    private void UpdateHoverRows(PerformanceChartData data, int bucket)
    {
        var durationTicks = Math.Max(0, (data.TimeRange.LastTimePoint - data.TimeRange.FirstTimePoint).Ticks);
        var startTicks = Math.Min((long)bucket * data.AggregationInterval.Ticks, durationTicks);
        var endTicks = Math.Min(startTicks + data.AggregationInterval.Ticks, durationTicks);
        var bucketStart = data.TimeRange.FirstTimePoint.AddTicks(startTicks);
        var bucketEnd = data.TimeRange.FirstTimePoint.AddTicks(endTicks);
        _hoverTimeText.Text = bucketStart == bucketEnd
            ? $"{bucketStart:g} UTC"
            : $"{bucketStart:g} - {bucketEnd:g} UTC";

        _hoverRows.Clear();
        foreach (var series in data.Series)
        {
            if (!IsSeriesVisible(series))
            {
                continue;
            }

            var point = FindPointForBucket(series.Points, bucket);
            _hoverRows.Add(point is { } sample
                ? new HoverCounterSummary(series.DisplayName, series.Brush, series.Unit, sample.Mean, sample.Maximum, sample.SampleCount)
                : new HoverCounterSummary(series.DisplayName, series.Brush, series.Unit, null, null, 0));
        }
    }

    private static PerformancePlotPoint? FindPointForBucket(
        IReadOnlyList<PerformancePlotPoint> points,
        int bucket)
    {
        var low = 0;
        var high = points.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var point = points[middle];
            if (point.BucketIndex == bucket)
            {
                return point;
            }

            if (point.BucketIndex < bucket)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return null;
    }

    private void CloseHoverToolTip()
    {
        var hadHover = _hoveredBucket >= 0 || _hoverX is not null;
        _hoveredBucket = -1;
        _hoverX = null;
        _hoverToolTip.IsOpen = false;
        if (hadHover)
        {
            InvalidateVisual();
        }
    }

    private bool IsSeriesVisible(PerformancePlotSeries series)
    {
        return _visibleSeriesNames is null || _visibleSeriesNames.Contains(series.Name);
    }

    private sealed record HoverCounterSummary(
        string Counter,
        Brush ColourBrush,
        string Unit,
        double? Mean,
        double? Maximum,
        long SampleCount);

    private static FormattedText CreateText(string text, double fontSize, Brush brush, double pixelsPerDip)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            fontSize,
            brush,
            pixelsPerDip);
    }
}
