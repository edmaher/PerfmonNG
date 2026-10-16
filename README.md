# PerfmonNG

A .NET 9 WPF desktop application with a single main window and a time-series plot area.

## Build

```powershell
dotnet build .
```

## Run

```powershell
dotnet run .
```

The File menu provides Open and Exit. Open loads a PDH performance log, identifies its time range, enumerates its counters, and plots each counter instance as a colored line. Timestamps are shown in UTC to match the log and PerfMon. Choose 10-minute, 1-minute, 30-second, or 10-second intervals and aggregate each point using Max, Median, or Average. Statistic changes reuse cached samples; interval changes reload the log. Use the horizontal scrollbar to pan across the log. Hover anywhere over the chart to inspect the time bucket and each visible counter's mean, maximum, and sample count.

The counter tree is grouped by computer, object, parent, counter, and instance. Tri-state group checkboxes control line visibility. Its Min/Max/Average/Median columns summarize raw samples within the visible chart range and remain independent of the selected line statistic. PDH counter type and explanation metadata provide unit labels in both the summary and mouseover tables. Counter-instance combinations not present in a log are skipped and listed in the status tooltip. The panel divider can be dragged to resize the counter table.

The default Y axis is 0–100. Each counter series receives its own power-of-ten divisor when needed. Non-finite and below-axis values are skipped. Axis bounds can be supplied to `PerformanceChartController.OpenFileAsync` when loading a file.
