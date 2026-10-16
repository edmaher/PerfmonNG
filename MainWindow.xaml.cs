using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PerfmonNG;

public partial class MainWindow : Window
{
    private readonly PerformanceChartController _chartController;
    private readonly CounterTreeViewModel _counterTree;
    private string? _currentFilePath;
    private bool _isLoading;
    private bool _isUpdatingScrollBar;

    public MainWindow()
    {
        InitializeComponent();
        _chartController = new PerformanceChartController(Chart);
        _counterTree = new CounterTreeViewModel();
        CounterRows.ItemsSource = _counterTree.Rows;
        Chart.VisibleRangeChanged += Chart_VisibleRangeChanged;
        _counterTree.VisibilityChanged += (_, _) =>
            Chart.SetVisibleSeries(_counterTree.GetVisibleSeriesNames());
        AggregationSelector.ItemsSource = PerformanceChartController.AggregationOptions;
        AggregationSelector.DisplayMemberPath = nameof(PerformanceAggregationOption.Label);
        AggregationSelector.SelectedItem = PerformanceChartController.AggregationOptions[^1];
        StatisticSelector.ItemsSource = PerformanceChartController.StatisticOptions;
        StatisticSelector.DisplayMemberPath = nameof(PerformanceStatisticOption.Label);
        StatisticSelector.SelectedItem = PerformanceChartController.StatisticOptions[2];
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open performance data",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            _currentFilePath = dialog.FileName;
            SourceText.Text = $"Selected file: {Path.GetFileName(_currentFilePath)}";
            await LoadCurrentFileAsync();
        }
    }

    private async void Aggregation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentFilePath is not null && !_isLoading)
        {
            await LoadCurrentFileAsync();
        }
    }

    private void Statistic_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentFilePath is null
            || _isLoading
            || StatisticSelector.SelectedItem is not PerformanceStatisticOption option)
        {
            return;
        }

        var data = _chartController.SetStatistic(option.Statistic);
        UpdateLoadedStatus(data);
    }

    private async Task LoadCurrentFileAsync()
    {
        if (_currentFilePath is null
            || AggregationSelector.SelectedItem is not PerformanceAggregationOption aggregation
            || StatisticSelector.SelectedItem is not PerformanceStatisticOption statistic)
        {
            return;
        }

        _isLoading = true;
        OpenMenuItem.IsEnabled = false;
        AggregationSelector.IsEnabled = false;
        StatisticSelector.IsEnabled = false;
        StatusText.Text = $"Reading performance data ({statistic.Label}, {aggregation.Label} per point)...";

        try
        {
            var progress = new Progress<PerformanceLoadProgress>(update =>
            {
                StatusText.Text = $"Reading {update.SamplesRead:N0} of {update.ExpectedSamples:N0} samples...";
            });
            var data = await _chartController.OpenFileAsync(
                _currentFilePath,
                progress,
                aggregationInterval: aggregation.Interval,
                statistic: statistic.Statistic);
            _counterTree.Load(data.Series);
            UpdateCounterTreeStatistics();
            Chart.SetVisibleSeries(_counterTree.GetVisibleSeriesNames());
            UpdateTimelineScrollBar();
            UpdateLoadedStatus(data);
        }
        catch (Exception exception)
        {
            StatusText.Text = "Unable to load performance data.";
            MessageBox.Show(this, exception.Message, "Open performance data", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isLoading = false;
            OpenMenuItem.IsEnabled = true;
            AggregationSelector.IsEnabled = true;
            StatisticSelector.IsEnabled = true;
        }
    }

    private void UpdateLoadedStatus(PerformanceChartData data)
    {
        var statistic = PerformanceChartController.StatisticOptions
            .First(option => option.Statistic == data.Statistic);
        var aggregation = PerformanceChartController.AggregationOptions
            .First(option => option.Interval == data.AggregationInterval);
        var skippedText = data.InvalidValueCount == 0
            ? string.Empty
            : $"; skipped {data.InvalidValueCount:N0} invalid/off-axis values";
        var unavailablePaths = data.UnavailableCounters
            .Select(PdhLogFile.BuildCounterPath)
            .ToArray();
        var unavailableText = unavailablePaths.Length == 0
            ? string.Empty
            : $"; skipped {unavailablePaths.Length:N0} counter paths not present in the log";
        StatusText.ToolTip = unavailablePaths.Length == 0
            ? null
            : "Counter paths not present in this log:" + Environment.NewLine + string.Join(Environment.NewLine, unavailablePaths);
        StatusText.Text = $"{data.Series.Count:N0} counters plotted; {data.SamplesRead:N0} samples; " +
            $"{data.TimeRange.FirstTimePoint:G} to {data.TimeRange.LastTimePoint:G} UTC; " +
            $"{statistic.Label}, {aggregation.Label} per point{skippedText}{unavailableText}";
    }

    private void CounterExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CounterTreeNode node })
        {
            _counterTree.ToggleExpanded(node);
        }
    }

    private void CounterCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: CounterTreeNode node })
        {
            node.IsChecked = node.IsChecked != true;
        }
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTimelineScrollBar();
    }

    private void Chart_VisibleRangeChanged(object? sender, EventArgs e)
    {
        UpdateTimelineScrollBar();
    }

    private void TimelineScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingScrollBar)
        {
            return;
        }

        var startBucket = (int)Math.Round(e.NewValue);
        Chart.SetViewportStartBucket(startBucket);
    }

    private void UpdateTimelineScrollBar()
    {
        if (!IsInitialized)
        {
            return;
        }

        var totalBuckets = Chart.TotalBucketCount;
        var visibleBuckets = Chart.VisibleBucketCount;
        Chart.SetViewportStartBucket(Chart.ViewportStartBucket);
        _isUpdatingScrollBar = true;
        TimelineScrollBar.Maximum = Math.Max(0, totalBuckets - visibleBuckets);
        TimelineScrollBar.ViewportSize = visibleBuckets;
        TimelineScrollBar.SmallChange = 1;
        TimelineScrollBar.LargeChange = visibleBuckets;
        TimelineScrollBar.Value = Chart.ViewportStartBucket;
        TimelineScrollBar.IsEnabled = totalBuckets > visibleBuckets;
        _isUpdatingScrollBar = false;
        UpdateCounterTreeStatistics();
        UpdateVisibleRangeText(Chart.ViewportStartBucket);
    }

    private void UpdateCounterTreeStatistics()
    {
        var firstBucket = Chart.ViewportStartBucket;
        _counterTree.UpdateStatistics(firstBucket, firstBucket + Chart.VisibleBucketCount);
    }

    private void UpdateVisibleRangeText(int startBucket)
    {
        if (_chartController.CurrentTimeRange is not PdhTimeRange timeRange
            || AggregationSelector.SelectedItem is not PerformanceAggregationOption option)
        {
            VisibleRangeText.Text = string.Empty;
            return;
        }

        var start = timeRange.FirstTimePoint.AddTicks((long)startBucket * option.Interval.Ticks);
        var endOffset = Math.Min(
            Chart.TotalBucketCount,
            (long)startBucket + Chart.VisibleBucketCount) * option.Interval.Ticks;
        var end = timeRange.FirstTimePoint.AddTicks(Math.Min(
            endOffset,
            Math.Max(0, (timeRange.LastTimePoint - timeRange.FirstTimePoint).Ticks)));
        VisibleRangeText.Text = $"{start:g} - {end:g} UTC";
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
