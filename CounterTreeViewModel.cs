using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace PerfmonNG;

public sealed class CounterTreeViewModel
{
    private readonly List<CounterTreeNode> _roots = [];

    public ObservableCollection<CounterTreeRow> Rows { get; } = [];

    public event EventHandler? VisibilityChanged;

    public void Load(IEnumerable<PerformancePlotSeries> series)
    {
        _roots.Clear();
        Rows.Clear();

        foreach (var plotSeries in series)
        {
            var identifier = plotSeries.Identifier;
            var computerName = string.IsNullOrWhiteSpace(identifier.Computer)
                ? "Local computer"
                : identifier.Computer;
            var computer = FindOrCreateRoot(computerName);
            var objectNode = FindOrCreateChild(computer, identifier.Object, "Object");
            var counterParent = objectNode;
            if (!string.IsNullOrWhiteSpace(identifier.Parent))
            {
                counterParent = FindOrCreateChild(objectNode, identifier.Parent, "Parent");
            }

            var counter = FindOrCreateChild(counterParent, identifier.Counter, "Counter");
            if (string.IsNullOrWhiteSpace(identifier.Instance))
            {
                counter.SetSeries(plotSeries);
            }
            else
            {
                var instance = FindOrCreateChild(counter, identifier.Instance, "Instance");
                instance.SetSeries(plotSeries);
            }
        }

        RefreshVisibleRows();
    }

    public IReadOnlySet<string> GetVisibleSeriesNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in _roots)
        {
            AddVisibleSeries(root, names);
        }

        return names;
    }

    public void ToggleExpanded(CounterTreeNode node)
    {
        node.IsExpanded = !node.IsExpanded;
    }

    public void UpdateStatistics(int firstBucket, int endBucketExclusive)
    {
        foreach (var root in _roots)
        {
            root.UpdateStatistics(firstBucket, endBucketExclusive);
        }
    }

    private CounterTreeNode FindOrCreateRoot(string caption)
    {
        var existing = _roots.FirstOrDefault(node => node.Caption == caption);
        if (existing is not null)
        {
            return existing;
        }

        var node = CreateNode(caption, "Computer", parent: null, expanded: true);
        _roots.Add(node);
        return node;
    }

    private CounterTreeNode FindOrCreateChild(CounterTreeNode parent, string caption, string kind)
    {
        var existing = parent.Children.FirstOrDefault(node => node.Caption == caption && node.Kind == kind);
        if (existing is not null)
        {
            return existing;
        }

        var node = CreateNode(caption, kind, parent, expanded: kind is "Object" or "Parent" or "Counter");
        parent.AddChild(node);
        return node;
    }

    private CounterTreeNode CreateNode(string caption, string kind, CounterTreeNode? parent, bool expanded)
    {
        var node = new CounterTreeNode(caption, kind, parent, expanded);
        node.VisibilityChanged += (_, _) => VisibilityChanged?.Invoke(this, EventArgs.Empty);
        node.ExpansionChanged += (_, _) => RefreshVisibleRows();
        return node;
    }

    private void RefreshVisibleRows()
    {
        Rows.Clear();
        foreach (var root in _roots)
        {
            AddVisibleRow(root);
        }
    }

    private void AddVisibleRow(CounterTreeNode node)
    {
        Rows.Add(new CounterTreeRow(node));
        if (!node.IsExpanded)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            AddVisibleRow(child);
        }
    }

    private static void AddVisibleSeries(CounterTreeNode node, HashSet<string> names)
    {
        if (node.Series is not null && node.IsChecked == true)
        {
            names.Add(node.Series.Name);
        }

        foreach (var child in node.Children)
        {
            AddVisibleSeries(child, names);
        }
    }
}

public sealed class CounterTreeRow
{
    public CounterTreeRow(CounterTreeNode node)
    {
        Node = node;
    }

    public CounterTreeNode Node { get; }
    public Thickness Indent => new(Node.Depth * 14, 0, 0, 0);
}

public sealed class CounterTreeNode : INotifyPropertyChanged
{
    private bool? _isChecked = true;
    private bool _isExpanded;

    internal CounterTreeNode(string caption, string kind, CounterTreeNode? parent, bool expanded)
    {
        Caption = caption;
        Kind = kind;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        _isExpanded = expanded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal event EventHandler? VisibilityChanged;
    internal event EventHandler? ExpansionChanged;

    public string Caption { get; }
    public string Kind { get; }
    public int Depth { get; }
    public CounterTreeNode? Parent { get; }
    public ObservableCollection<CounterTreeNode> Children { get; } = [];
    public PerformancePlotSeries? Series { get; private set; }
    public string? Unit => Series?.Unit;
    public SeriesStatistics? Statistics { get; private set; }
    public Visibility SeriesVisibility => Series is null ? Visibility.Collapsed : Visibility.Visible;
    public bool HasChildren => Children.Count > 0;
    public string ExpandGlyph => !HasChildren ? string.Empty : IsExpanded ? "-" : "+";

    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value ?? true, bubbleToParent: true, notifyVisibility: true);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value || !HasChildren)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpandGlyph));
            ExpansionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double? Minimum => Statistics?.Minimum;
    public double? Maximum => Statistics?.Maximum;
    public double? Average => Statistics?.Average;
    public double? Median => Statistics?.Median;

    internal void AddChild(CounterTreeNode child)
    {
        Children.Add(child);
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(ExpandGlyph));
    }

    internal void SetSeries(PerformancePlotSeries series)
    {
        Series = series;
        Statistics = null;
        OnPropertyChanged(nameof(Series));
        OnPropertyChanged(nameof(Unit));
        OnPropertyChanged(nameof(SeriesVisibility));
        OnPropertyChanged(nameof(Statistics));
        OnPropertyChanged(nameof(Minimum));
        OnPropertyChanged(nameof(Maximum));
        OnPropertyChanged(nameof(Average));
        OnPropertyChanged(nameof(Median));
    }

    internal void UpdateStatistics(int firstBucket, int endBucketExclusive)
    {
        if (Series is not null)
        {
            Statistics = SeriesStatistics.From(Series, firstBucket, endBucketExclusive);
            OnPropertyChanged(nameof(Statistics));
            OnPropertyChanged(nameof(Minimum));
            OnPropertyChanged(nameof(Maximum));
            OnPropertyChanged(nameof(Average));
            OnPropertyChanged(nameof(Median));
        }

        foreach (var child in Children)
        {
            child.UpdateStatistics(firstBucket, endBucketExclusive);
        }
    }

    private void SetChecked(bool isChecked, bool bubbleToParent, bool notifyVisibility)
    {
        if (_isChecked == isChecked)
        {
            return;
        }

        _isChecked = isChecked;
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(Minimum));
        OnPropertyChanged(nameof(Maximum));
        OnPropertyChanged(nameof(Average));
        OnPropertyChanged(nameof(Median));

        foreach (var child in Children)
        {
            child.SetChecked(isChecked, bubbleToParent: false, notifyVisibility: false);
        }

        if (notifyVisibility)
        {
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
        }

        if (bubbleToParent)
        {
            Parent?.UpdateCheckFromChildren();
        }
    }

    private void UpdateCheckFromChildren()
    {
        if (Children.Count == 0)
        {
            return;
        }

        var firstState = Children[0].IsChecked;
        var state = Children.All(child => child.IsChecked == firstState) ? firstState : null;
        if (_isChecked == state)
        {
            return;
        }

        _isChecked = state;
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(Minimum));
        OnPropertyChanged(nameof(Maximum));
        OnPropertyChanged(nameof(Average));
        OnPropertyChanged(nameof(Median));
        Parent?.UpdateCheckFromChildren();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record SeriesStatistics(double Minimum, double Maximum, double Average, double Median)
{
    public static SeriesStatistics? From(
        PerformancePlotSeries series,
        int firstBucket,
        int endBucketExclusive)
    {
        var values = new List<double>();
        foreach (var point in series.Points)
        {
            if (point.BucketIndex < firstBucket || point.BucketIndex >= endBucketExclusive)
            {
                continue;
            }

            var rawSampleEnd = point.RawSampleOffset + checked((int)point.SampleCount);
            for (var sampleIndex = point.RawSampleOffset; sampleIndex < rawSampleEnd; sampleIndex++)
            {
                values.Add(series.RawSamples[sampleIndex]);
            }
        }

        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var average = values.Aggregate(0d, (sum, value) => sum + value) / values.Count;
        var middle = values.Count / 2;
        var median = values.Count % 2 == 0
            ? values[middle - 1] + (values[middle] - values[middle - 1]) / 2
            : values[middle];
        return new SeriesStatistics(values[0], values[^1], average, median);
    }
}
