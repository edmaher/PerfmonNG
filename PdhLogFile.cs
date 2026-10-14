using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PerfmonNG;

public sealed record PdhCounterIdentifier(
    string? Instance,
    string Counter,
    string? Parent,
    string Object,
    string? Computer,
    uint? InstanceIndex = null);

public readonly record struct PdhTimeRange(
    DateTime FirstTimePoint,
    DateTime LastTimePoint,
    uint SampleCount);

[SupportedOSPlatform("windows")]
public static class PdhLogFile
{
    private const uint ErrorSuccess = 0;
    private const uint MoreData = 0x800007D2;
    private const uint DetailWizard = 400;

    public static DateTime GetFirstTimePoint(string logFilePath)
    {
        return GetTimeRange(logFilePath).FirstTimePoint;
    }

    public static DateTime GetLastTimePoint(string logFilePath)
    {
        return GetTimeRange(logFilePath).LastTimePoint;
    }

    public static PdhTimeRange GetTimeRange(string logFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);

        uint entryCount;
        var bufferSize = checked((uint)Marshal.SizeOf<PdhTimeInfo>());
        var status = NativeMethods.PdhGetDataSourceTimeRangeW(
            logFilePath,
            out entryCount,
            out var timeInfo,
            ref bufferSize);

        if (status != ErrorSuccess)
        {
            throw new PdhException(nameof(NativeMethods.PdhGetDataSourceTimeRangeW), status);
        }

        if (entryCount == 0)
        {
            throw new InvalidDataException("PDH returned no time range for the log file.");
        }

        return new PdhTimeRange(
            PdhNativeMethods.FromPdhFileTimeUtc(timeInfo.StartTime),
            PdhNativeMethods.FromPdhFileTimeUtc(timeInfo.EndTime),
            timeInfo.SampleCount);
    }

    public static IReadOnlyList<string> EnumerateMachines(string logFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);

        return ReadMultiString(
            (IntPtr buffer, ref uint length, bool _) =>
                NativeMethods.PdhEnumMachinesW(logFilePath, buffer, ref length),
            nameof(NativeMethods.PdhEnumMachinesW));
    }

    public static IReadOnlyList<string> EnumerateObjects(string logFilePath, string? computerName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);

        return ReadMultiString(
            (IntPtr buffer, ref uint length, bool refresh) =>
                NativeMethods.PdhEnumObjectsW(
                    logFilePath,
                    computerName,
                    buffer,
                    ref length,
                    DetailWizard,
                    refresh ? 1 : 0),
            nameof(NativeMethods.PdhEnumObjectsW));
    }

    public static string BuildCounterPath(PdhCounterIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        var elements = new CounterPathElements
        {
            Computer = identifier.Computer,
            Object = identifier.Object,
            Instance = identifier.Instance,
            Parent = identifier.Parent,
            InstanceIndex = identifier.InstanceIndex ?? 0,
            Counter = identifier.Counter
        };

        uint characterCount = 0;
        var status = NativeMethods.PdhMakeCounterPathW(ref elements, null, ref characterCount, 0);
        if (status != MoreData && status != ErrorSuccess)
        {
            throw new PdhException(nameof(NativeMethods.PdhMakeCounterPathW), status);
        }

        if (characterCount == 0)
        {
            throw new PdhException(nameof(NativeMethods.PdhMakeCounterPathW), status);
        }

        var path = new StringBuilder(checked((int)characterCount));
        status = NativeMethods.PdhMakeCounterPathW(ref elements, path, ref characterCount, 0);
        if (status != ErrorSuccess)
        {
            throw new PdhException(nameof(NativeMethods.PdhMakeCounterPathW), status);
        }

        return path.ToString();
    }

    public static IReadOnlyList<PdhCounterIdentifier> EnumerateCounters(
        string logFilePath,
        string? computerName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);

        var computers = computerName is null
            ? EnumerateMachines(logFilePath)
            : new[] { computerName };
        var results = new List<PdhCounterIdentifier>();

        foreach (var computer in computers)
        {
            foreach (var objectName in EnumerateObjects(logFilePath, computer))
            {
                var objectItems = ReadObjectItems(logFilePath, computer, objectName);
                foreach (var counterName in objectItems.Counters)
                {
                    if (!objectItems.HasInstances)
                    {
                        results.Add(new PdhCounterIdentifier(
                            Instance: null,
                            Counter: counterName,
                            Parent: null,
                            Object: objectName,
                            Computer: computer));
                        continue;
                    }

                    foreach (var instanceName in objectItems.Instances)
                    {
                        var (parent, instance, instanceIndex) = ParseInstance(instanceName);
                        results.Add(new PdhCounterIdentifier(
                            Instance: instance,
                            Counter: counterName,
                            Parent: parent,
                            Object: objectName,
                            Computer: computer,
                            InstanceIndex: instanceIndex));
                    }
                }
            }
        }

        return results;
    }

    private static string[] ReadMultiString(MultiStringQuery query, string operation)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint length = 0;
            var status = query(IntPtr.Zero, ref length, refresh: true);
            if (status == ErrorSuccess && length == 0)
            {
                return [];
            }

            if (status != MoreData && status != ErrorSuccess)
            {
                throw new PdhException(operation, status);
            }

            if (length == 0)
            {
                return [];
            }

            using var buffer = new NativeBuffer(length);
            var bufferLength = length;
            status = query(buffer.Pointer, ref bufferLength, refresh: false);
            if (status == MoreData)
            {
                continue;
            }

            if (status != ErrorSuccess)
            {
                throw new PdhException(operation, status);
            }

            return ParseMultiString(buffer.Pointer, length);
        }

        throw new PdhException(operation, MoreData);
    }

    private static ObjectItems ReadObjectItems(string logFilePath, string? computerName, string objectName)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint counterLength = 0;
            uint instanceLength = 0;
            var status = NativeMethods.PdhEnumObjectItemsW(
                logFilePath,
                computerName,
                objectName,
                IntPtr.Zero,
                ref counterLength,
                IntPtr.Zero,
                ref instanceLength,
                DetailWizard,
                0);

            if (status == ErrorSuccess && counterLength == 0 && instanceLength == 0)
            {
                return new ObjectItems([], [], HasInstances: false);
            }

            if (status != MoreData && status != ErrorSuccess)
            {
                throw new PdhException(nameof(NativeMethods.PdhEnumObjectItemsW), status);
            }

            using var counterBuffer = new NativeBuffer(counterLength);
            using var instanceBuffer = new NativeBuffer(instanceLength);
            var counterBufferLength = counterLength;
            var instanceBufferLength = instanceLength;
            status = NativeMethods.PdhEnumObjectItemsW(
                logFilePath,
                computerName,
                objectName,
                counterBuffer.Pointer,
                ref counterBufferLength,
                instanceBuffer.Pointer,
                ref instanceBufferLength,
                DetailWizard,
                0);

            if (status == MoreData)
            {
                continue;
            }

            if (status != ErrorSuccess)
            {
                throw new PdhException(nameof(NativeMethods.PdhEnumObjectItemsW), status);
            }

            return new ObjectItems(
                ParseMultiString(counterBuffer.Pointer, counterLength),
                ParseMultiString(instanceBuffer.Pointer, instanceLength),
                HasInstances: instanceLength > 0);
        }

        throw new PdhException(nameof(NativeMethods.PdhEnumObjectItemsW), MoreData);
    }

    private static string[] ParseMultiString(IntPtr buffer, uint length)
    {
        if (buffer == IntPtr.Zero || length == 0)
        {
            return [];
        }

        var values = new List<string>();
        uint position = 0;
        while (position < length)
        {
            var value = Marshal.PtrToStringUni(buffer + checked((int)(position * sizeof(char))));
            if (string.IsNullOrEmpty(value))
            {
                break;
            }

            values.Add(value);
            position += checked((uint)value.Length + 1);
        }

        return values.ToArray();
    }

    private static (string? Parent, string Instance, uint? InstanceIndex) ParseInstance(string value)
    {
        string? parent = null;
        var instance = value;
        var separator = value.IndexOf('/');
        if (separator >= 0)
        {
            parent = value[..separator];
            instance = value[(separator + 1)..];
        }

        uint? instanceIndex = null;
        var indexSeparator = instance.LastIndexOf('#');
        if (indexSeparator >= 0
            && uint.TryParse(instance.AsSpan(indexSeparator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedIndex))
        {
            instance = instance[..indexSeparator];
            instanceIndex = parsedIndex;
        }

        return (parent, instance, instanceIndex);
    }

    private delegate uint MultiStringQuery(IntPtr buffer, ref uint length, bool refresh);

    private sealed record ObjectItems(string[] Counters, string[] Instances, bool HasInstances);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PdhTimeInfo
    {
        public long StartTime;
        public long EndTime;
        public uint SampleCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CounterPathElements
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Computer;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Object;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Instance;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Parent;

        public uint InstanceIndex;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Counter;
    }

    private sealed class NativeBuffer : IDisposable
    {
        public NativeBuffer(uint characterCount)
        {
            if (characterCount > 0)
            {
                Pointer = Marshal.AllocHGlobal(checked((int)characterCount * sizeof(char)));
            }
        }

        public IntPtr Pointer { get; private set; }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint PdhGetDataSourceTimeRangeW(
            string dataSource,
            out uint entryCount,
            out PdhTimeInfo timeInfo,
            ref uint bufferSize);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint PdhEnumMachinesW(
            string dataSource,
            IntPtr machineList,
            ref uint characterCount);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint PdhEnumObjectsW(
            string dataSource,
            string? machineName,
            IntPtr objectList,
            ref uint characterCount,
            uint detailLevel,
            int refresh);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint PdhEnumObjectItemsW(
            string dataSource,
            string? machineName,
            string objectName,
            IntPtr counterList,
            ref uint counterCharacterCount,
            IntPtr instanceList,
            ref uint instanceCharacterCount,
            uint detailLevel,
            uint flags);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint PdhMakeCounterPathW(
            ref CounterPathElements pathElements,
            [Out] StringBuilder? fullPath,
            ref uint characterCount,
            uint flags);
    }
}
