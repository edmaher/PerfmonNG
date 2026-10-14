using System.Windows.Media;
using System.Runtime.Versioning;

namespace PerfmonNG;

public readonly record struct PerformanceLoadProgress(long SamplesRead, uint ExpectedSamples);

public readonly record struct PerformancePlotPoint(
    int BucketIndex,
    DateTime Timestamp,
    double Value,
    double Minimum,
    double Mean,
    double Maximum,
    double Median,
    long SampleCount,
    int RawSampleOffset);

public enum PerformanceAggregationStatistic
{
    Maximum,
    Median,
    Average
}

public sealed record PerformanceAggregationOption(string Label, TimeSpan Interval);

public sealed record PerformanceStatisticOption(string Label, PerformanceAggregationStatistic Statistic);

public sealed record PerformancePlotSeries(
    PdhCounterIdentifier Identifier,
    string Name,
    string Unit,
    Color Color,
    double ScaleFactor,
    int ScaleExponent,
    IReadOnlyList<PerformancePlotPoint> Points,
    IReadOnlyList<double> RawSamples)
{
    public Brush Brush => new SolidColorBrush(Color);
    public string DisplayName => ScaleExponent == 0 ? Name : $"{Name} (÷ 10^{ScaleExponent})";
}

public sealed record PerformanceChartData(
    string FilePath,
    PdhTimeRange TimeRange,
    TimeSpan AggregationInterval,
    PerformanceAggregationStatistic Statistic,
    int TotalBucketCount,
    IReadOnlyList<PdhCounterIdentifier> Counters,
    IReadOnlyList<PerformancePlotSeries> Series,
    double YAxisMinimum,
    double YAxisMaximum,
    long SamplesRead,
    long InvalidValueCount);

[SupportedOSPlatform("windows")]
public sealed class PerformanceChartController
{
    private static readonly SeriesColorPalette ColorPalette = new();

    public static IReadOnlyList<PerformanceAggregationOption> AggregationOptions { get; } =
    [
        new("10 min", TimeSpan.FromMinutes(10)),
        new("1 min", TimeSpan.FromMinutes(1)),
        new("30 sec", TimeSpan.FromSeconds(30)),
        new("10 sec", TimeSpan.FromSeconds(10))
    ];

    public static IReadOnlyList<PerformanceStatisticOption> StatisticOptions { get; } =
    [
        new("Max", PerformanceAggregationStatistic.Maximum),
        new("Median", PerformanceAggregationStatistic.Median),
        new("Average", PerformanceAggregationStatistic.Average)
    ];

    private readonly TimeSeriesPlot _chart;

    public PerformanceChartController(TimeSeriesPlot chart)
    {
        _chart = chart ?? throw new ArgumentNullException(nameof(chart));
    }

    public string? CurrentFilePath { get; private set; }
    public PdhTimeRange? CurrentTimeRange { get; private set; }
    public IReadOnlyList<PdhCounterIdentifier> Counters { get; private set; } = [];
    public PerformanceChartData? CurrentData { get; private set; }

    public PdhTimeRange FindTimeRange(string logFilePath)
    {
        return PdhLogFile.GetTimeRange(logFilePath);
    }

    public IReadOnlyList<PdhCounterIdentifier> EnumerateCounters(string logFilePath)
    {
        return PdhLogFile.EnumerateCounters(logFilePath);
    }

    public async Task<PerformanceChartData> OpenFileAsync(
        string logFilePath,
        IProgress<PerformanceLoadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        double yAxisMinimum = 0,
        double yAxisMaximum = 100,
        TimeSpan? aggregationInterval = null,
        PerformanceAggregationStatistic statistic = PerformanceAggregationStatistic.Average)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        if (!double.IsFinite(yAxisMinimum) || !double.IsFinite(yAxisMaximum) || yAxisMinimum >= yAxisMaximum)
        {
            throw new ArgumentOutOfRangeException(nameof(yAxisMaximum), "The Y-axis maximum must be finite and greater than its minimum.");
        }

        var interval = aggregationInterval ?? TimeSpan.FromSeconds(10);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(aggregationInterval), "Aggregation interval must be positive.");
        }

        var data = await Task.Run(
            () => ReadFile(logFilePath, interval, statistic, yAxisMinimum, yAxisMaximum, progress, cancellationToken),
            cancellationToken);

        _chart.SetChartData(data);
        CurrentFilePath = data.FilePath;
        CurrentTimeRange = data.TimeRange;
        Counters = data.Counters;
        CurrentData = data;
        return data;
    }

    public PerformanceChartData SetStatistic(PerformanceAggregationStatistic statistic)
    {
        var currentData = CurrentData
            ?? throw new InvalidOperationException("Open a performance log before changing the chart statistic.");

        if (currentData.Statistic == statistic)
        {
            return currentData;
        }

        var series = currentData.Series
            .Select(currentSeries =>
            {
                var points = currentSeries.Points
                    .Select(point => point with { Value = GetStatisticValue(point, statistic) })
                    .ToArray();
                var maximumValue = points.Length == 0 ? 0 : points.Max(point => point.Value);
                var (scaleFactor, scaleExponent) = DetermineScaleFactor(maximumValue, currentData.YAxisMaximum);
                return currentSeries with
                {
                    Points = points,
                    ScaleFactor = scaleFactor,
                    ScaleExponent = scaleExponent
                };
            })
            .ToArray();

        CurrentData = currentData with { Statistic = statistic, Series = series };
        _chart.SetChartData(CurrentData, resetViewport: false);
        return CurrentData;
    }

    private static PerformanceChartData ReadFile(
        string logFilePath,
        TimeSpan aggregationInterval,
        PerformanceAggregationStatistic statistic,
        double yAxisMinimum,
        double yAxisMaximum,
        IProgress<PerformanceLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var timeRange = PdhLogFile.GetTimeRange(logFilePath);
        var bucketCount = CalculateBucketCount(timeRange, aggregationInterval);
        var counterIdentifiers = PdhLogFile.EnumerateCounters(logFilePath);
        if (counterIdentifiers.Count == 0)
        {
            return CreateEmptyChartData(
                logFilePath,
                timeRange,
                aggregationInterval,
                statistic,
                bucketCount,
                yAxisMinimum,
                yAxisMaximum);
        }

        using var query = PdhQuery.OpenLogFile(logFilePath);
        var counters = AddCounters(query, counterIdentifiers);

        var accumulators = counters
            .Select(_ => new SeriesAccumulator(bucketCount, statistic))
            .ToArray();
        var sampleResult = ReadSamples(
            query,
            counters,
            accumulators,
            timeRange,
            aggregationInterval,
            timeRange.SampleCount,
            bucketCount,
            yAxisMinimum,
            progress,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var series = BuildPlotSeries(counters, accumulators, yAxisMaximum);
        progress?.Report(new PerformanceLoadProgress(sampleResult.SamplesRead, timeRange.SampleCount));

        return new PerformanceChartData(
            logFilePath,
            timeRange,
            aggregationInterval,
            statistic,
            bucketCount,
            counterIdentifiers,
            series,
            yAxisMinimum,
            yAxisMaximum,
            sampleResult.SamplesRead,
            sampleResult.InvalidValueCount);
    }

    private static PerformanceChartData CreateEmptyChartData(
        string filePath,
        PdhTimeRange timeRange,
        TimeSpan aggregationInterval,
        PerformanceAggregationStatistic statistic,
        int bucketCount,
        double yAxisMinimum,
        double yAxisMaximum)
    {
        return new PerformanceChartData(
            filePath,
            timeRange,
            aggregationInterval,
            statistic,
            bucketCount,
            [],
            [],
            yAxisMinimum,
            yAxisMaximum,
            0,
            0);
    }

            private static int CalculateBucketCount(PdhTimeRange timeRange, TimeSpan aggregationInterval)
            {
            var durationTicks = Math.Max(0, (timeRange.LastTimePoint - timeRange.FirstTimePoint).Ticks);
            var bucketCount = durationTicks / aggregationInterval.Ticks + 1;
            return checked((int)Math.Max(1, bucketCount));
            }

    private static CounterBinding[] AddCounters(
        PdhQuery query,
        IReadOnlyList<PdhCounterIdentifier> identifiers)
    {
        return identifiers
            .Select(identifier =>
            {
                var path = PdhLogFile.BuildCounterPath(identifier);
                return new CounterBinding(identifier, path, query.AddCounter(path));
            })
            .ToArray();
    }

    private static SampleReadResult ReadSamples(
        PdhQuery query,
        IReadOnlyList<CounterBinding> counters,
        IReadOnlyList<SeriesAccumulator> accumulators,
        PdhTimeRange timeRange,
        TimeSpan aggregationInterval,
        uint reportedSampleCount,
        int bucketCount,
        double yAxisMinimum,
        IProgress<PerformanceLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        long invalidValueCount = 0;
        long samplesRead = 0;

        while (query.TryCollectData(out var timestamp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsedTicks = Math.Max(0, (timestamp - timeRange.FirstTimePoint).Ticks);
            var bucket = (int)Math.Clamp(elapsedTicks / aggregationInterval.Ticks, 0, bucketCount - 1L);

            for (var counterIndex = 0; counterIndex < counters.Count; counterIndex++)
            {
                if (query.TryGetDoubleValue(counters[counterIndex].Counter, out var value)
                    && value >= yAxisMinimum)
                {
                    accumulators[counterIndex].Add(bucket, timestamp, value);
                }
                else
                {
                    invalidValueCount++;
                }
            }

            samplesRead++;
            if ((samplesRead & 0x3FFF) == 0)
            {
                progress?.Report(new PerformanceLoadProgress(samplesRead, reportedSampleCount));
            }
        }

        return new SampleReadResult(samplesRead, invalidValueCount);
    }

    private static IReadOnlyList<PerformancePlotSeries> BuildPlotSeries(
        IReadOnlyList<CounterBinding> counters,
        IReadOnlyList<SeriesAccumulator> accumulators,
        double yAxisMaximum)
    {
        var series = new List<PerformancePlotSeries>(counters.Count);
        for (var index = 0; index < counters.Count; index++)
        {
            var points = accumulators[index].Complete();
            if (points.Count == 0)
            {
                continue;
            }

            var (scaleFactor, scaleExponent) = DetermineScaleFactor(
                accumulators[index].MaximumValue,
                yAxisMaximum);
            series.Add(new PerformancePlotSeries(
                counters[index].Identifier,
                counters[index].Path,
                counters[index].Counter.Unit,
                GetSeriesColor(index),
                scaleFactor,
                scaleExponent,
                points,
                accumulators[index].RawSamples.ToArray()));
        }

        return series;
    }

    private static double GetStatisticValue(
        PerformancePlotPoint point,
        PerformanceAggregationStatistic statistic)
    {
        return statistic switch
        {
            PerformanceAggregationStatistic.Maximum => point.Maximum,
            PerformanceAggregationStatistic.Median => point.Median,
            _ => point.Mean
        };
    }

    private static (double Factor, int Exponent) DetermineScaleFactor(double maximumValue, double yAxisMaximum)
    {
        if (maximumValue <= yAxisMaximum || maximumValue <= 0)
        {
            return (1, 0);
        }

        var exponent = Math.Max(1, (int)Math.Ceiling(Math.Log10(maximumValue / yAxisMaximum)));
        var factor = Math.Pow(10, exponent);
        while (maximumValue / factor > yAxisMaximum)
        {
            exponent++;
            factor *= 10;
        }

        return (factor, exponent);
    }

    private static Color GetSeriesColor(int index)
    {
        return ColorPalette.GetColor(index);
    }

    private static byte ToByte(double component)
    {
        return (byte)Math.Clamp(Math.Round(component * 255), 0, 255);
    }

    private sealed class SeriesColorPalette
    {
        private static readonly double[] LightnessLevels = [0.42, 0.55, 0.68];
        private static readonly double[] SaturationLevels = [0.78, 0.96];

        private readonly object _sync = new();
        private readonly List<ColorCandidate> _candidates = CreateCandidates();
        private readonly bool[] _selected;
        private readonly double[] _nearestDistanceSquared;
        private readonly List<Color> _colors = [];
        private int _remainingCandidates;
        private int? _uniqueColorCount;

        public SeriesColorPalette()
        {
            var seedColor = Color.FromRgb(0x00, 0x7F, 0x5F);
            var seedLab = ToLab(seedColor);
            _colors.Add(seedColor);
            _selected = new bool[_candidates.Count];
            _nearestDistanceSquared = new double[_candidates.Count];
            _remainingCandidates = _candidates.Count;

            for (var index = 0; index < _candidates.Count; index++)
            {
                _nearestDistanceSquared[index] = DistanceSquared(seedLab, _candidates[index].Lab);
            }
        }

        public Color GetColor(int index)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);

            lock (_sync)
            {
                while (_colors.Count <= index)
                {
                    AppendNextColor();
                }

                return _colors[index];
            }
        }

        private void AppendNextColor()
        {
            if (_remainingCandidates == 0)
            {
                _uniqueColorCount ??= _colors.Count;
                _colors.Add(_colors[_colors.Count % _uniqueColorCount.Value]);
                return;
            }

            var bestIndex = -1;
            var bestDistanceSquared = double.NegativeInfinity;
            for (var candidateIndex = 0; candidateIndex < _candidates.Count; candidateIndex++)
            {
                if (!_selected[candidateIndex]
                    && _nearestDistanceSquared[candidateIndex] > bestDistanceSquared)
                {
                    bestIndex = candidateIndex;
                    bestDistanceSquared = _nearestDistanceSquared[candidateIndex];
                }
            }

            var selectedCandidate = _candidates[bestIndex];
            _selected[bestIndex] = true;
            _remainingCandidates--;
            _colors.Add(selectedCandidate.Color);

            for (var candidateIndex = 0; candidateIndex < _candidates.Count; candidateIndex++)
            {
                if (_selected[candidateIndex])
                {
                    continue;
                }

                var distanceSquared = DistanceSquared(
                    selectedCandidate.Lab,
                    _candidates[candidateIndex].Lab);
                _nearestDistanceSquared[candidateIndex] = Math.Min(
                    _nearestDistanceSquared[candidateIndex],
                    distanceSquared);
            }
        }

        private static List<ColorCandidate> CreateCandidates()
        {
            var candidates = new List<ColorCandidate>(360 * LightnessLevels.Length * SaturationLevels.Length / 2);
            var seenColors = new HashSet<uint>();

            for (var hue = 0; hue < 360; hue += 2)
            {
                foreach (var lightness in LightnessLevels)
                {
                    foreach (var saturation in SaturationLevels)
                    {
                        var color = FromHsl(hue, saturation, lightness);
                        var key = (uint)(color.R << 16 | color.G << 8 | color.B);
                        if (seenColors.Add(key))
                        {
                            candidates.Add(new ColorCandidate(color, ToLab(color)));
                        }
                    }
                }
            }

            return candidates;
        }

        private static Color FromHsl(double hue, double saturation, double lightness)
        {
            var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            var hueSector = hue / 60d;
            var secondary = chroma * (1 - Math.Abs(hueSector % 2 - 1));
            var rgb = (int)hueSector switch
            {
                0 => (chroma, secondary, 0d),
                1 => (secondary, chroma, 0d),
                2 => (0d, chroma, secondary),
                3 => (0d, secondary, chroma),
                4 => (secondary, 0d, chroma),
                _ => (chroma, 0d, secondary)
            };
            var match = lightness - chroma / 2;

            return Color.FromRgb(
                ToByte(rgb.Item1 + match),
                ToByte(rgb.Item2 + match),
                ToByte(rgb.Item3 + match));
        }

        private static LabColor ToLab(Color color)
        {
            var red = ToLinear(color.R / 255d);
            var green = ToLinear(color.G / 255d);
            var blue = ToLinear(color.B / 255d);
            var x = LabPivot((red * 0.4124564 + green * 0.3575761 + blue * 0.1804375) / 0.95047);
            var y = LabPivot(red * 0.2126729 + green * 0.7151522 + blue * 0.0721750);
            var z = LabPivot((red * 0.0193339 + green * 0.1191920 + blue * 0.9503041) / 1.08883);

            return new LabColor(
                116 * y - 16,
                500 * (x - y),
                200 * (y - z));
        }

        private static double ToLinear(double component)
        {
            return component <= 0.04045
                ? component / 12.92
                : Math.Pow((component + 0.055) / 1.055, 2.4);
        }

        private static double LabPivot(double component)
        {
            const double threshold = 0.008856451679;
            return component > threshold
                ? Math.Cbrt(component)
                : 7.787037037 * component + 16d / 116;
        }

        private static double DistanceSquared(LabColor left, LabColor right)
        {
            var lightness = left.Lightness - right.Lightness;
            var a = left.A - right.A;
            var b = left.B - right.B;
            return lightness * lightness + a * a + b * b;
        }

        private readonly record struct ColorCandidate(Color Color, LabColor Lab);
        private readonly record struct LabColor(double Lightness, double A, double B);
    }

    private sealed record CounterBinding(
        PdhCounterIdentifier Identifier,
        string Path,
        PdhCounter Counter);

    private readonly record struct SampleReadResult(long SamplesRead, long InvalidValueCount);

    private sealed class SeriesAccumulator
    {
        private readonly List<PerformancePlotPoint> _points;
        private readonly List<double> _bucketValues = [];
        private readonly List<double> _rawSamples = [];
        private readonly PerformanceAggregationStatistic _statistic;
        private int _currentBucket = -1;
        private bool _hasValue;
        private long _sampleCount;
        private double _averageValue;
        private double _minimumValue;
        private double _maximumValue;
        private double _averageTimeOffsetTicks;
        private DateTime _firstTimestamp;

        public double MaximumValue { get; private set; }
        public IReadOnlyList<double> RawSamples => _rawSamples;

        public SeriesAccumulator(int bucketCount, PerformanceAggregationStatistic statistic)
        {
            _points = new List<PerformancePlotPoint>(bucketCount);
            _statistic = statistic;
        }

        public void Add(int bucket, DateTime timestamp, double value)
        {
            if (_hasValue && bucket != _currentBucket)
            {
                Flush();
            }

            _currentBucket = bucket;
            _rawSamples.Add(value);
            _bucketValues.Add(value);
            if (!_hasValue)
            {
                _firstTimestamp = timestamp;
                _averageValue = value;
                _minimumValue = value;
                _maximumValue = value;
                _averageTimeOffsetTicks = 0;
                _sampleCount = 1;
                _hasValue = true;
                return;
            }

            _sampleCount++;
            _averageValue += (value - _averageValue) / _sampleCount;
            _minimumValue = Math.Min(_minimumValue, value);
            _maximumValue = Math.Max(_maximumValue, value);
            var timeOffset = (timestamp - _firstTimestamp).Ticks;
            _averageTimeOffsetTicks += (timeOffset - _averageTimeOffsetTicks) / _sampleCount;
        }

        public IReadOnlyList<PerformancePlotPoint> Complete()
        {
            Flush();
            return _points;
        }

        private void Flush()
        {
            if (!_hasValue)
            {
                return;
            }

            var median = CalculateMedian();
            var aggregateValue = _statistic switch
            {
                PerformanceAggregationStatistic.Maximum => _maximumValue,
                PerformanceAggregationStatistic.Median => median,
                _ => _averageValue
            };
            MaximumValue = Math.Max(MaximumValue, aggregateValue);
            var meanTimestamp = _firstTimestamp.AddTicks((long)Math.Round(_averageTimeOffsetTicks));
            _points.Add(new PerformancePlotPoint(
                _currentBucket,
                meanTimestamp,
                aggregateValue,
                _minimumValue,
                _averageValue,
                _maximumValue,
                median,
                _sampleCount,
                checked(_rawSamples.Count - (int)_sampleCount)));
            _bucketValues.Clear();
            _hasValue = false;
        }

        private double CalculateMedian()
        {
            var values = _bucketValues;
            values.Sort();
            var middle = values.Count / 2;
            return values.Count % 2 == 0
                ? values[middle - 1] + (values[middle] - values[middle - 1]) / 2
                : values[middle];
        }
    }
}
