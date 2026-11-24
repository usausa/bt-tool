namespace DeviceTool.Bluetooth;

using System.Diagnostics;

internal readonly record struct SignalSnapshot(string? Name, int Count, short Rssi, int RecentAverage, int Average, short MinRssi, short MaxRssi, TimeSpan Silence);

internal sealed class SignalTracker
{
    private const int RecentCount = 10;

    private readonly Lock sync = new();

    private readonly Queue<short> recent = new();

    private string? name;

    private int count;

    private long total;

    private long recentTotal;

    private short rssi;

    private short minRssi;

    private short maxRssi;

    private long timestamp = Stopwatch.GetTimestamp();

    public void Add(short value, string? localName)
    {
        lock (sync)
        {
            if (!String.IsNullOrEmpty(localName))
            {
                name = localName;
            }

            minRssi = (count == 0) ? value : Math.Min(minRssi, value);
            maxRssi = (count == 0) ? value : Math.Max(maxRssi, value);
            count++;
            total += value;
            rssi = value;

            recent.Enqueue(value);
            recentTotal += value;
            if (recent.Count > RecentCount)
            {
                recentTotal -= recent.Dequeue();
            }

            timestamp = Stopwatch.GetTimestamp();
        }
    }

    public SignalSnapshot GetSnapshot()
    {
        lock (sync)
        {
            return new SignalSnapshot(name, count, rssi, Average(recentTotal, recent.Count), Average(total, count), minRssi, maxRssi, Stopwatch.GetElapsedTime(timestamp));
        }
    }

    private static int Average(long sum, int number) => (number == 0) ? 0 : (int)Math.Round((double)sum / number, MidpointRounding.AwayFromZero);
}
