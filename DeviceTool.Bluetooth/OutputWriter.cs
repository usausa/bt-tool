namespace DeviceTool.Bluetooth;

using System.Globalization;
using System.Text;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;

using static DeviceTool.Bluetooth.AddressHelper;
using static DeviceTool.Bluetooth.ConsoleHelper;

internal static class OutputWriter
{
    private const int BarLength = 20;

    private const int BarMinRssi = -100;

    private const int BarMaxRssi = -30;

    private const int StrongRssi = -60;

    private const int MediumRssi = -75;

    private const string BlockSymbols = "\u2588\u2591";

    private const string AsciiSymbols = "#-";

    private static readonly string BarSymbols = GetBarSymbols();

    private static string? liveBarSymbols;

    public static bool IsOutputRedirected => ConsoleIsOutputRedirected;

    public static void WriteError(string message) => ConsoleErrorWriteLine(message);

    public static void WriteJson(string line) => ConsoleWriteLine(line);

    public static void WriteStatus(string message) => ConsoleWriteLine(Palette.Muted, $"      ({message})");

    public static void WriteNotSupported(string operation) => ConsoleWriteLine(Palette.Error, $"      ({operation} not supported)");

    public static void WriteFailure(string operation, GattCommunicationStatus status, byte? protocolError)
    {
        var detail = ((status == GattCommunicationStatus.ProtocolError) && protocolError.HasValue) ? $"ProtocolError 0x{protocolError.Value:X2}" : status.ToString();
        ConsoleWriteLine(Palette.Error, $"      ({operation} failed: {detail})");
    }

    public static void WriteNoDevices() => ConsoleWriteLine(Palette.Muted, "(No devices)");

    public static void WriteDevice(ulong address, string? name)
    {
        ConsoleWrite(Palette.Plain, "[");
        ConsoleWrite(Palette.Address, ToAddressString(address));
        ConsoleWrite(Palette.Plain, "] ");
        ConsoleWriteLine(Palette.Name, name ?? "(Unknown)");
    }

    public static void WritePairedDevice(ulong address, string? name, bool connected)
    {
        var status = connected ? BluetoothConnectionStatus.Connected : BluetoothConnectionStatus.Disconnected;
        ConsoleWrite(Palette.Plain, "[");
        ConsoleWrite(Palette.Address, ToAddressString(address));
        ConsoleWrite(Palette.Plain, "] ");
        ConsoleWrite(connected ? Palette.Good : Palette.Muted, $"{status,-12}");
        ConsoleWrite(Palette.Plain, " ");
        ConsoleWriteLine(Palette.Name, name ?? "(Unknown)");
    }

    public static void WriteInfo(BluetoothLEDevice device)
    {
        ConsoleWrite(Palette.Label, "DeviceId:");
        ConsoleWriteLine(Palette.Plain, $" {device.BluetoothDeviceId.Id}");
        ConsoleWrite(Palette.Label, "AddressType:");
        ConsoleWriteLine(Palette.Plain, $" {device.BluetoothAddressType}");
        ConsoleWrite(Palette.Label, "ConnectionStatus:");
        ConsoleWriteLine(Palette.Plain, $" {device.ConnectionStatus}");
        ConsoleWrite(Palette.Label, "ProtectionLevel:");
        ConsoleWriteLine(Palette.Plain, $" {device.DeviceInformation.Pairing.ProtectionLevel}");
        ConsoleWrite(Palette.Label, "IsPaired:");
        ConsoleWriteLine(Palette.Plain, $" {device.DeviceInformation.Pairing.IsPaired}");
        ConsoleWrite(Palette.Label, "CanPair:");
        ConsoleWriteLine(Palette.Plain, $" {device.DeviceInformation.Pairing.CanPair}");
    }

    public static void WriteAdapter(BluetoothAdapter adapter, Radio? radio)
    {
        ConsoleWrite(Palette.Label, "Address:");
        ConsoleWriteLine(Palette.Plain, $" {ToAddressString(adapter.BluetoothAddress)}");
        ConsoleWrite(Palette.Label, "IsLowEnergySupported:");
        ConsoleWriteLine(Palette.Plain, $" {adapter.IsLowEnergySupported}");
        ConsoleWrite(Palette.Label, "IsCentralRoleSupported:");
        ConsoleWriteLine(Palette.Plain, $" {adapter.IsCentralRoleSupported}");
        ConsoleWrite(Palette.Label, "IsPeripheralRoleSupported:");
        ConsoleWriteLine(Palette.Plain, $" {adapter.IsPeripheralRoleSupported}");
        ConsoleWrite(Palette.Label, "IsExtendedAdvertisingSupported:");
        ConsoleWriteLine(Palette.Plain, $" {adapter.IsExtendedAdvertisingSupported}");
        ConsoleWrite(Palette.Label, "MaxAdvertisementDataLength:");
        ConsoleWriteLine(Palette.Plain, $" {adapter.MaxAdvertisementDataLength}");
        ConsoleWrite(Palette.Label, "RadioState:");
        ConsoleWriteLine(Palette.Plain, $" {radio?.State.ToString() ?? "(Unknown)"}");
    }

    public static void WriteGatt(GattInfo gatt)
    {
        if (gatt.Status == GattCommunicationStatus.Success)
        {
            ConsoleWriteLine(Palette.Label, "GattServices:");
            foreach (var service in gatt.Services)
            {
                WriteService(service.Uuid, service.Name);
                if (service.Status == GattCommunicationStatus.Success)
                {
                    foreach (var characteristic in service.Characteristics)
                    {
                        WriteCharacteristic(characteristic.Uuid, characteristic.Name, characteristic.Properties);
                        if (characteristic.Value is not null)
                        {
                            WriteValue(characteristic.Value);
                        }
                    }
                }
                else
                {
                    ConsoleWriteLine(Palette.Error, $"    (Get characteristics failed: {service.Status})");
                }
            }
        }
        else
        {
            ConsoleWrite(Palette.Label, "GattServices:");
            ConsoleWriteLine(Palette.Error, $" {gatt.Status}");
        }
    }

    public static void WriteService(Guid uuid, string? name)
    {
        if (name is null)
        {
            ConsoleWriteLine(Palette.Plain, $"  {uuid}");
            return;
        }

        ConsoleWrite(Palette.Plain, $"  {uuid}    ");
        ConsoleWriteLine(Palette.ReferenceName, name);
    }

    public static void WriteCharacteristic(Guid uuid, string name, GattCharacteristicProperties properties)
    {
        ConsoleWrite(Palette.Plain, $"    {uuid}    ");
        ConsoleWrite(Palette.ReferenceName, name);
        ConsoleWrite(Palette.Plain, " [");
        ConsoleWrite(Palette.Property, properties.ToString());
        ConsoleWriteLine(Palette.Plain, "]");
    }

    public static void WriteData(byte[] data)
    {
        if (data.Length == 0)
        {
            ConsoleWriteLine(Palette.Data, "      (Empty)");
            return;
        }

        ConsoleWriteHex(Palette.Data, data, "    ");
        if (data.All(static x => x is >= 0x20 and <= 0x7E))
        {
            ConsoleWrite(Palette.Label, "      Text:");
            ConsoleWriteLine(Palette.Plain, $" {Encoding.ASCII.GetString(data)}");
        }
    }

    public static void WriteNotification(DateTimeOffset timestamp, ReadOnlySpan<byte> data) =>
        ConsoleWriteHex(Palette.Timestamp, $"{timestamp:HH:mm:ss.fff}", Palette.Data, data);

    public static void WriteAdvertisement(DateTimeOffset timestamp, ulong address, short rssi, string? name)
    {
        ConsoleWrite(Palette.Timestamp, $"{timestamp:HH:mm:ss.fff}");
        ConsoleWrite(Palette.Plain, " [");
        ConsoleWrite(Palette.Address, ToAddressString(address));
        ConsoleWrite(Palette.Plain, "] ");
        ConsoleWrite(Palette.Label, "RSSI:");
        ConsoleWrite(Palette.Plain, $"{rssi}");
        ConsoleWrite(Palette.Plain, " ");
        ConsoleWriteLine(Palette.Name, name ?? "(Unknown)");
    }

    public static void WriteLabel(string label) => ConsoleWriteLine(Palette.Label, label);

    public static void WriteNamedValue(string label, string value, string? referenceName)
    {
        ConsoleWrite(Palette.Label, label);
        ConsoleWrite(Palette.Plain, $" {value}");
        if (referenceName is not null)
        {
            ConsoleWrite(Palette.ReferenceName, $" {referenceName}");
        }

        ConsoleWriteLine();
    }

    public static void WriteHex(ReadOnlySpan<byte> data) => ConsoleWriteHex(Palette.Data, data, String.Empty);

    public static void WriteDecoded(DecodedResult result)
    {
        ConsoleWrite(Palette.Label, "Decoded:");
        ConsoleWrite(Palette.ReferenceName, $" {result.Name}");
        foreach (var field in result.Fields)
        {
            ConsoleWrite(Palette.Plain, (field.Unit is null) ? $" {field.Name}={field.Value}" : $" {field.Name}={field.Value} {field.Unit}");
        }

        ConsoleWriteLine();
    }

    public static void WriteScanFailure(ulong address, bool json) => WriteScanStatus(json, Palette.Error, $"[{ToAddressString(address)}] (Failed get information)");

    public static void WriteScanCounts(int failed, int skipped, bool json) => WriteScanStatus(json, Palette.Muted, $"(Failed: {failed}, Skipped: {skipped})");

    public static void WriteSummary(IReadOnlyList<ScanSummaryItem> items)
    {
        if (items.Count == 0)
        {
            WriteNoDevices();
            return;
        }

        ConsoleWriteLine(Palette.Label, "Address            Count  AvgRSSI  MinRSSI  MaxRSSI  LastSeen      Name");
        foreach (var item in items)
        {
            ConsoleWrite(Palette.Address, ToAddressString(item.Address));
            ConsoleWrite(Palette.Plain, String.Create(CultureInfo.InvariantCulture, $"  {item.Count,5}  {item.AverageRssi,7:F1}  {item.MinRssi,7}  {item.MaxRssi,7}  "));
            ConsoleWrite(Palette.Timestamp, $"{item.LastSeen:HH:mm:ss.fff}");
            ConsoleWrite(Palette.Plain, "  ");
            ConsoleWriteLine(Palette.Name, item.Name ?? "(Unknown)");
        }
    }

    public static void WriteSignal(SignalSnapshot snapshot, bool silent)
    {
        foreach (var (color, text) in GetSignalSegments(snapshot, silent, BarSymbols))
        {
            ConsoleWrite(color, text);
        }

        ConsoleWriteLine();
    }

    public static int WriteLiveSignal(SignalSnapshot snapshot, bool silent, int width)
    {
        liveBarSymbols ??= GetLiveBarSymbols();
        var limit = ConsoleWindowWidth - 1;
        ConsoleWriteCarriageReturn();
        foreach (var (color, text) in GetSignalSegments(snapshot, silent, liveBarSymbols))
        {
            var available = limit - ConsoleCursorLeft;
            if (available <= 0)
            {
                break;
            }

            ConsoleWrite(color, (text.Length > available) ? text[..available] : text);
        }

        var end = ConsoleCursorLeft;
        var clear = Math.Min(width, limit);
        if (end < clear)
        {
            ConsoleWriteSpaces(clear - end);
        }

        return end;
    }

    public static void EndLiveSignal()
    {
        if (ConsoleCursorLeft > 0)
        {
            ConsoleWriteLine();
        }
    }

    public static void WriteSignalTotal(SignalSnapshot snapshot) =>
        ConsoleWriteLine(Palette.Plain, (snapshot.Count == 0) ? "Packets: 0" : String.Create(CultureInfo.InvariantCulture, $"Packets: {snapshot.Count}  RSSI avg {snapshot.Average}  min {snapshot.MinRssi}  max {snapshot.MaxRssi}"));

    private static void WriteValue(GattValueInfo value)
    {
        if (value.Status != GattCommunicationStatus.Success)
        {
            ConsoleWriteLine(Palette.Error, $"      (Read failed: {value.Status})");
            return;
        }

        WriteData(value.Data);
    }

    private static void WriteScanStatus(bool json, ConsoleColor color, string message)
    {
        if (json)
        {
            ConsoleErrorWriteLine(message);
        }
        else
        {
            ConsoleWriteLine(color, message);
        }
    }

    private static List<(ConsoleColor Color, string Text)> GetSignalSegments(SignalSnapshot snapshot, bool silent, string symbols)
    {
        var time = (Palette.Timestamp, $"{DateTime.Now:HH:mm:ss}");
        if (silent)
        {
            return [time, (Palette.Muted, " (no signal)")];
        }

        return
        [
            time,
            (Palette.Plain, String.Create(CultureInfo.InvariantCulture, $" RSSI {snapshot.Rssi} dBm  avg {snapshot.RecentAverage}  min {snapshot.MinRssi}  max {snapshot.MaxRssi}  ")),
            (GetSignalColor(snapshot.RecentAverage), GetBar(snapshot.RecentAverage, symbols))
        ];
    }

    private static string GetBar(int rssi, string symbols)
    {
        var length = Math.Clamp((int)Math.Round((double)(rssi - BarMinRssi) * BarLength / (BarMaxRssi - BarMinRssi), MidpointRounding.AwayFromZero), 0, BarLength);
        return new string(symbols[0], length) + new string(symbols[1], BarLength - length);
    }

    private static ConsoleColor GetSignalColor(int rssi) => rssi switch
    {
        >= StrongRssi => Palette.Good,
        >= MediumRssi => Palette.Medium,
        _ => Palette.Weak
    };

    private static string GetBarSymbols()
    {
        var encoding = ConsoleOutputEncoding;
        return (encoding.GetString(encoding.GetBytes(BlockSymbols)) == BlockSymbols) ? BlockSymbols : AsciiSymbols;
    }

    private static string GetLiveBarSymbols()
    {
        if (BarSymbols != BlockSymbols)
        {
            return BarSymbols;
        }

        ConsoleWriteCarriageReturn();
        ConsoleWrite(BlockSymbols);
        var moved = ConsoleCursorLeft;
        ConsoleWriteCarriageReturn();
        ConsoleWriteSpaces(moved);
        ConsoleWriteCarriageReturn();
        return (moved == BlockSymbols.Length) ? BlockSymbols : AsciiSymbols;
    }

    private static class Palette
    {
        public const ConsoleColor Timestamp = ConsoleColor.Cyan;

        public const ConsoleColor Address = ConsoleColor.DarkCyan;

        public const ConsoleColor Name = ConsoleColor.Magenta;

        public const ConsoleColor Label = ConsoleColor.Yellow;

        public const ConsoleColor ReferenceName = ConsoleColor.Blue;

        public const ConsoleColor Property = ConsoleColor.Green;

        public const ConsoleColor Data = ConsoleColor.DarkGreen;

        public const ConsoleColor Error = ConsoleColor.Red;

        public const ConsoleColor Muted = ConsoleColor.DarkGray;

        public const ConsoleColor Good = ConsoleColor.Green;

        public const ConsoleColor Medium = ConsoleColor.Yellow;

        public const ConsoleColor Weak = ConsoleColor.Red;

        public static ConsoleColor Plain => ConsoleForegroundColor;
    }
}
