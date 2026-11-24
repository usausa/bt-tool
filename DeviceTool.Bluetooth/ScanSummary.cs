namespace DeviceTool.Bluetooth;

using System.Globalization;

using static DeviceTool.Bluetooth.AddressHelper;
using static DeviceTool.Bluetooth.ConsoleHelper;

internal sealed record ScanSummaryItem(ulong Address, string? Name, int Count, double AverageRssi, short MinRssi, short MaxRssi, DateTimeOffset LastSeen);

internal sealed class ScanSummary
{
    private readonly Dictionary<ulong, Entry> entries = [];

    public void Add(ulong address, short rssi, DateTimeOffset timestamp)
    {
        if (!entries.TryGetValue(address, out var entry))
        {
            entry = new Entry { MinRssi = rssi, MaxRssi = rssi, LastSeen = timestamp };
            entries.Add(address, entry);
        }

        entry.Count++;
        entry.TotalRssi += rssi;
        entry.MinRssi = Math.Min(entry.MinRssi, rssi);
        entry.MaxRssi = Math.Max(entry.MaxRssi, rssi);
        if (timestamp > entry.LastSeen)
        {
            entry.LastSeen = timestamp;
        }
    }

    public List<ScanSummaryItem> GetItems(IReadOnlyDictionary<ulong, string?> names, Func<string?, bool> filter) =>
        entries
            .Select(x => new ScanSummaryItem(x.Key, names.GetValueOrDefault(x.Key), x.Value.Count, Math.Round((double)x.Value.TotalRssi / x.Value.Count, 1, MidpointRounding.AwayFromZero), x.Value.MinRssi, x.Value.MaxRssi, x.Value.LastSeen))
            .Where(x => filter(x.Name))
            .OrderByDescending(static x => x.AverageRssi)
            .ThenBy(static x => x.Address)
            .ToList();

    public static void Write(IReadOnlyList<ScanSummaryItem> items)
    {
        if (items.Count == 0)
        {
            ConsoleWriteLine(ConsoleColor.DarkGray, "(No devices)");
            return;
        }

        ConsoleWriteLine(ConsoleColor.Yellow, "Address            Count  AvgRSSI  MinRSSI  MaxRSSI  LastSeen      Name");
        foreach (var item in items)
        {
            ConsoleWrite(ConsoleColor.DarkCyan, ToAddressString(item.Address));
            ConsoleWrite(Console.ForegroundColor, String.Create(CultureInfo.InvariantCulture, $"  {item.Count,5}  {item.AverageRssi,7:F1}  {item.MinRssi,7}  {item.MaxRssi,7}  "));
            ConsoleWrite(ConsoleColor.Cyan, $"{item.LastSeen:HH:mm:ss.fff}");
            ConsoleWrite(Console.ForegroundColor, "  ");
            ConsoleWriteLine(ConsoleColor.Magenta, item.Name ?? "(Unknown)");
        }
    }

    private sealed class Entry
    {
        public int Count { get; set; }

        public long TotalRssi { get; set; }

        public short MinRssi { get; set; }

        public short MaxRssi { get; set; }

        public DateTimeOffset LastSeen { get; set; }
    }
}
