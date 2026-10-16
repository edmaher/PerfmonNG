using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PerfmonNG;

[SupportedOSPlatform("windows")]
public sealed class PdhQuery : IDisposable
{
    private SafePdhQueryHandle? _queryHandle;

    public PdhQuery(string? dataSource = null)
    {
        if (dataSource is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dataSource);
        }

        var status = PdhNativeMethods.PdhOpenQueryW(dataSource, UIntPtr.Zero, out var queryHandle);
        if (status != PdhNativeMethods.ErrorSuccess)
        {
            queryHandle?.Dispose();
            throw new PdhException(nameof(PdhNativeMethods.PdhOpenQueryW), status);
        }

        _queryHandle = queryHandle;
    }

    public static PdhQuery OpenLogFile(string logFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        return new PdhQuery(logFilePath);
    }

    public PdhCounter AddEnglishCounter(string counterPath)
    {
        return AddCounterCore(counterPath, useEnglishNames: true);
    }

    public PdhCounter AddCounter(string counterPath)
    {
        return AddCounterCore(counterPath, useEnglishNames: false);
    }

    private PdhCounter AddCounterCore(string counterPath, bool useEnglishNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(counterPath);
        var queryHandle = GetQueryHandle();
        IntPtr counterHandle;
        uint status;

        if (useEnglishNames)
        {
            status = PdhNativeMethods.PdhAddEnglishCounterW(
                queryHandle,
                counterPath,
                UIntPtr.Zero,
                out counterHandle);
        }
        else
        {
            status = PdhNativeMethods.PdhAddCounterW(
                queryHandle,
                counterPath,
                UIntPtr.Zero,
                out counterHandle);
        }

        if (status != PdhNativeMethods.ErrorSuccess)
        {
            var operation = useEnglishNames
                ? nameof(PdhNativeMethods.PdhAddEnglishCounterW)
                : nameof(PdhNativeMethods.PdhAddCounterW);
            throw new PdhException(operation, status);
        }

        var metadata = PdhNativeMethods.GetCounterMetadata(counterHandle, counterPath);
        return new PdhCounter(this, counterHandle, counterPath, metadata);
    }

    public void CollectData()
    {
        if (!TryCollectData())
        {
            throw new PdhException(
                nameof(PdhNativeMethods.PdhCollectQueryData),
                PdhNativeMethods.NoMoreData);
        }
    }

    public bool TryCollectData()
    {
        return TryCollectData(out _);
    }

    public bool TryCollectData(out DateTime timestamp)
    {
        var status = PdhNativeMethods.PdhCollectQueryDataWithTime(GetQueryHandle(), out var fileTime);
        if (status == PdhNativeMethods.NoMoreData)
        {
            timestamp = default;
            return false;
        }

        if (status != PdhNativeMethods.ErrorSuccess)
        {
            throw new PdhException(nameof(PdhNativeMethods.PdhCollectQueryDataWithTime), status);
        }

        timestamp = PdhNativeMethods.FromPdhFileTimeUtc(fileTime);
        return true;
    }

    public double GetDoubleValue(PdhCounter counter)
    {
        if (!TryGetDoubleValue(counter, out var value))
        {
            throw new PdhException("Counter data", PdhNativeMethods.InvalidData);
        }

        return value;
    }

    public bool TryGetDoubleValue(PdhCounter counter, out double value)
    {
        ArgumentNullException.ThrowIfNull(counter);
        GetQueryHandle();
        value = default;

        if (!ReferenceEquals(counter.Owner, this))
        {
            throw new ArgumentException("The counter belongs to a different query.", nameof(counter));
        }

        var status = PdhNativeMethods.PdhGetFormattedCounterValue(
            counter.Handle,
            PdhNativeMethods.FormatDouble,
            out _,
            out var formattedValue);

        if (status != PdhNativeMethods.ErrorSuccess)
        {
            if (status is PdhNativeMethods.InvalidData
                or PdhNativeMethods.NoInstance
                or PdhNativeMethods.NoData
                or PdhNativeMethods.EntryNotInLogFile
                or PdhNativeMethods.CalculationNegativeValue)
            {
                return false;
            }

            throw new PdhException(nameof(PdhNativeMethods.PdhGetFormattedCounterValue), status);
        }

        if (formattedValue.Status is not PdhNativeMethods.CounterStatusValidData
            and not PdhNativeMethods.CounterStatusNewData)
        {
            return false;
        }

        var counterValue = formattedValue.Value.DoubleValue;
        if (!double.IsFinite(counterValue))
        {
            return false;
        }

        value = counterValue;
        return true;
    }

    public void Dispose()
    {
        _queryHandle?.Dispose();
        _queryHandle = null;
    }

    private SafePdhQueryHandle GetQueryHandle()
    {
        return _queryHandle ?? throw new ObjectDisposedException(nameof(PdhQuery));
    }
}

public sealed class PdhCounter
{
    internal PdhCounter(PdhQuery owner, IntPtr handle, string path, PdhCounterMetadata metadata)
    {
        Owner = owner;
        Handle = handle;
        Path = path;
        Metadata = metadata;
    }

    internal PdhQuery Owner { get; }
    internal IntPtr Handle { get; }
    public string Path { get; }
    public PdhCounterMetadata Metadata { get; }
    public string Unit => Metadata.Unit;
}

public sealed record PdhCounterMetadata(
    uint Type,
    int Scale,
    int DefaultScale,
    string Unit,
    string? ExplainText)
{
    internal static PdhCounterMetadata FromNative(
        uint type,
        int scale,
        int defaultScale,
        string counterPath,
        string? explainText)
    {
        const uint displayMask = 0xF0000000;
        var unit = (type & displayMask) switch
        {
            0x10000000 => "/s",
            0x20000000 => "%",
            0x30000000 => "s",
            0x40000000 => string.Empty,
            _ => InferUnit(counterPath, explainText)
        };

        return new PdhCounterMetadata(type, scale, defaultScale, unit, explainText);
    }

    private static string InferUnit(string counterPath, string? explainText)
    {
        var description = $"{counterPath} {explainText}".ToLowerInvariant();
        if (description.Contains("byte", StringComparison.Ordinal))
        {
            return "bytes";
        }

        if (description.Contains("packet", StringComparison.Ordinal)
            || description.Contains("request", StringComparison.Ordinal)
            || description.Contains("operation", StringComparison.Ordinal)
            || description.Contains("process", StringComparison.Ordinal))
        {
            return "count";
        }

        if (description.Contains("second", StringComparison.Ordinal))
        {
            return "s";
        }

        return "value";
    }
}

public sealed class PdhException : Exception
{
    public PdhException(string operation, uint status)
        : base($"{operation} failed with PDH status 0x{status:X8}.")
    {
        Status = status;
    }

    public uint Status { get; }
}

internal sealed class SafePdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePdhQueryHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle()
    {
        return PdhNativeMethods.PdhCloseQuery(handle) == PdhNativeMethods.ErrorSuccess;
    }
}

internal static class PdhNativeMethods
{
    internal const uint ErrorSuccess = 0;
    private const uint MoreData = 0x800007D2;
    internal const uint NoMoreData = 0xC0000BCC;
    internal const uint NoData = 0x800007D5;
    internal const uint NoInstance = 0x800007D1;
    internal const uint InvalidData = 0xC0000BC6;
    internal const uint EntryNotInLogFile = 0xC0000BCD;
    internal const uint CalculationNegativeValue = 0x800007D8;
    internal const uint FormatDouble = 0x00000200;
    internal const uint CounterStatusValidData = 0;
    internal const uint CounterStatusNewData = 1;

    internal static DateTime FromPdhFileTimeUtc(long fileTime)
    {
        return DateTime.FromFileTimeUtc(fileTime);
    }

    internal static PdhCounterMetadata GetCounterMetadata(IntPtr counter, string counterPath)
    {
        uint bufferSize = 0;
        var status = PdhGetCounterInfoW(counter, true, ref bufferSize, IntPtr.Zero);
        if (status != MoreData && status != ErrorSuccess || bufferSize == 0)
        {
            return PdhCounterMetadata.FromNative(0, 0, 0, counterPath, null);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
            try
            {
                var actualSize = bufferSize;
                status = PdhGetCounterInfoW(counter, true, ref actualSize, buffer);
                if (status == MoreData)
                {
                    bufferSize = actualSize;
                    continue;
                }

                if (status != ErrorSuccess)
                {
                    return PdhCounterMetadata.FromNative(0, 0, 0, counterPath, null);
                }

                var type = unchecked((uint)Marshal.ReadInt32(buffer, sizeof(uint)));
                var scale = Marshal.ReadInt32(buffer, sizeof(uint) * 4);
                var defaultScale = Marshal.ReadInt32(buffer, sizeof(uint) * 5);
                var explainTextOffset = sizeof(uint) * 6 + IntPtr.Size * 9;
                var explainTextPointer = Marshal.ReadIntPtr(buffer, explainTextOffset);
                var explainText = explainTextPointer == IntPtr.Zero
                    ? null
                    : Marshal.PtrToStringUni(explainTextPointer);

                return PdhCounterMetadata.FromNative(type, scale, defaultScale, counterPath, explainText);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return PdhCounterMetadata.FromNative(0, 0, 0, counterPath, null);
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint PdhOpenQueryW(
        string? dataSource,
        UIntPtr userData,
        out SafePdhQueryHandle query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint PdhAddEnglishCounterW(
        SafePdhQueryHandle query,
        string counterPath,
        UIntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint PdhAddCounterW(
        SafePdhQueryHandle query,
        string counterPath,
        UIntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    internal static extern uint PdhCollectQueryData(SafePdhQueryHandle query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    internal static extern uint PdhCollectQueryDataWithTime(
        SafePdhQueryHandle query,
        out long timestamp);

    [DllImport("pdh.dll", ExactSpelling = true)]
    internal static extern uint PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        out uint counterType,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll", ExactSpelling = true)]
    internal static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetCounterInfoW(
        IntPtr counter,
        [MarshalAs(UnmanagedType.U1)] bool retrieveExplainText,
        ref uint bufferSize,
        IntPtr buffer);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PdhFormattedCounterValue
{
    public uint Status;
    public PdhCounterValueUnion Value;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PdhCounterValueUnion
{
    [FieldOffset(0)]
    public int LongValue;

    [FieldOffset(0)]
    public double DoubleValue;

    [FieldOffset(0)]
    public long LargeValue;

    [FieldOffset(0)]
    public IntPtr StringValue;
}
