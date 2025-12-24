// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable MemberCanBeProtected.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace BleScan;

using System;
using System.Runtime.InteropServices.WindowsRuntime;

using Smart.CommandLine.Hosting;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

public sealed class RootCommandHandler : ICommandHandler
{
    [Option("--active", "-a", Description = "Active scanning")]
    public bool Active { get; set; }

    [Option("--once", "-o", Description = "Scan once")]
    public bool Once { get; set; }

    [Option("--info", "-i", Description = "Show device information")]
    public bool Info { get; set; }

    [Option("--gatt", "-g", Description = "Get gatt services")]
    public bool Gatt { get; set; }

    [Option("--manufacturer", "-m", Description = "Show manufacturer data")]
    public bool Manufacturer { get; set; }

    [Option("--section", "-s", Description = "Show data section")]
    public bool Section { get; set; }

    public ValueTask ExecuteAsync(CommandContext context)
    {
        var set = new HashSet<ulong>();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = Active ? BluetoothLEScanningMode.Active : BluetoothLEScanningMode.Passive
        };

        watcher.Received += WatcherOnReceived;

#pragma warning disable CA1031
        // ReSharper disable once AsyncVoidMethod
        async void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (Once)
            {
                lock (set)
                {
                    if (!set.Add(args.BluetoothAddress))
                    {
                        return;
                    }
                }
            }

            try
            {
                var device = await BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress);
                var services = (Gatt && (device is not null)) ? await device.GetGattServicesAsync() : null;
                var name = device?.Name ?? "(Unknown)";

                lock (watcher)
                {
                    ConsoleWrite(ConsoleColor.Cyan, $"{args.Timestamp:HH:mm:ss.fff}");
                    ConsoleWrite(Console.ForegroundColor, " [");
                    ConsoleWrite(ConsoleColor.DarkCyan, ToAddressString(args.BluetoothAddress));
                    ConsoleWrite(Console.ForegroundColor, "] ");
                    ConsoleWrite(ConsoleColor.Yellow, "RSSI:");
                    ConsoleWrite(Console.ForegroundColor, $"{args.RawSignalStrengthInDBm}");
                    ConsoleWrite(Console.ForegroundColor, " ");
                    ConsoleWriteLine(ConsoleColor.Magenta, name);

                    if (Info && (device is not null))
                    {
                        ConsoleWrite(ConsoleColor.Yellow, "DeviceId:");
                        ConsoleWriteLine(Console.ForegroundColor, $" {device.BluetoothDeviceId.Id}");
                        ConsoleWrite(ConsoleColor.Yellow, "AddressType:");
                        ConsoleWriteLine(Console.ForegroundColor, $" {device.BluetoothAddressType}");
                        ConsoleWrite(ConsoleColor.Yellow, "ConnectionStatus:");
                        ConsoleWriteLine(Console.ForegroundColor, $" {device.ConnectionStatus}");
                        ConsoleWrite(ConsoleColor.Yellow, "ProtectionLevel:");
                        ConsoleWriteLine(Console.ForegroundColor, $" {device.DeviceInformation.Pairing.ProtectionLevel}");
                        ConsoleWrite(ConsoleColor.Yellow, "IsPaired:");
                        ConsoleWriteLine(Console.ForegroundColor, $" {device.DeviceInformation.Pairing.IsPaired}");
                        ConsoleWrite(ConsoleColor.Yellow, "CanPair:");
                        ConsoleWriteLine(Console.ForegroundColor, $" {device.DeviceInformation.Pairing.CanPair}");
                    }

                    if (Gatt && (services is not null))
                    {
                        if (services.Status == GattCommunicationStatus.Success)
                        {
                            ConsoleWriteLine(ConsoleColor.Yellow, "GattServices:");
                            foreach (var service in services.Services)
                            {
                                ConsoleWrite(Console.ForegroundColor, $"  {service.Uuid}    ");
                                ConsoleWriteLine(ConsoleColor.Blue, DisplayHelper.GetServiceName(service));
                                try
                                {
                                    foreach (var characteristic in service.GetAllCharacteristics())
                                    {
                                        ConsoleWrite(Console.ForegroundColor, $"    {characteristic.Uuid}    ");
                                        ConsoleWrite(ConsoleColor.Blue, DisplayHelper.GetCharacteristicName(characteristic));
                                        ConsoleWrite(Console.ForegroundColor, " [");
                                        ConsoleWrite(ConsoleColor.Green, characteristic.CharacteristicProperties.ToString());
                                        ConsoleWriteLine(Console.ForegroundColor, "]");
                                    }
                                }
                                catch
                                {
                                    ConsoleWriteLine(ConsoleColor.Red, "    (Get characteristics failed)");
                                }
                            }
                        }
                        else
                        {
                            ConsoleWrite(ConsoleColor.Yellow, "GattServices:");
                            ConsoleWriteLine(ConsoleColor.Red, $" {services.Status}");
                        }
                    }

                    if (Manufacturer)
                    {
                        foreach (var md in args.Advertisement.ManufacturerData)
                        {
                            ConsoleWrite(ConsoleColor.Yellow, "CompanyId:");
                            ConsoleWriteLine(Console.ForegroundColor, $" 0x{md.CompanyId:X4}");
                            ConsoleWriteLine(ConsoleColor.Yellow, "Data:");
                            var array = md.Data.ToArray().AsSpan();
                            for (var start = 0; start < array.Length; start += 16)
                            {
                                ConsoleWriteLine(ConsoleColor.DarkGreen, ToHexString(array.Slice(start, Math.Min(16, array.Length - start))));
                            }
                        }
                    }

                    if (Section)
                    {
                        foreach (var ds in args.Advertisement.DataSections)
                        {
                            ConsoleWrite(ConsoleColor.Yellow, "DataType:");
                            ConsoleWriteLine(Console.ForegroundColor, $" 0x{ds.DataType:X2}");
                            ConsoleWriteLine(ConsoleColor.Yellow, "Data:");
                            var array = ds.Data.ToArray().AsSpan();
                            for (var start = 0; start < array.Length; start += 16)
                            {
                                ConsoleWriteLine(ConsoleColor.DarkGreen, ToHexString(array.Slice(start, Math.Min(16, array.Length - start))));
                            }
                        }
                    }
                }
            }
            catch
            {
                ConsoleWriteLine(ConsoleColor.Red, "(Failed get information)");
            }
        }
#pragma warning restore CA1031

        watcher.Start();

        Console.ReadLine();

        return ValueTask.CompletedTask;
    }

    private static void ConsoleWrite(ConsoleColor color, string value)
    {
        var backup = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(value);
        Console.ForegroundColor = backup;
    }

    private static void ConsoleWriteLine(ConsoleColor color, string value)
    {
        var backup = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(value);
        Console.ForegroundColor = backup;
    }

    private static unsafe string ToAddressString(ulong address)
    {
        var hex = stackalloc char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F' };
        var span = stackalloc char[18];
        var offset = 0;

        span[offset++] = hex[(address >> 44) & 0xF];
        span[offset++] = hex[(address >> 40) & 0xF];
        span[offset++] = ':';
        span[offset++] = hex[(address >> 36) & 0xF];
        span[offset++] = hex[(address >> 32) & 0xF];
        span[offset++] = ':';
        span[offset++] = hex[(address >> 28) & 0xF];
        span[offset++] = hex[(address >> 24) & 0xF];
        span[offset++] = ':';
        span[offset++] = hex[(address >> 20) & 0xF];
        span[offset++] = hex[(address >> 16) & 0xF];
        span[offset++] = ':';
        span[offset++] = hex[(address >> 12) & 0xF];
        span[offset++] = hex[(address >> 8) & 0xF];
        span[offset++] = ':';
        span[offset++] = hex[(address >> 4) & 0xF];
        span[offset] = hex[address & 0xF];

        return new string(span);
    }

    private static unsafe string ToHexString(ReadOnlySpan<byte> source)
    {
        var hex = stackalloc char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F' };
        var span = stackalloc char[(source.Length * 3) + 2];
        var offset = 0;

        span[offset++] = ' ';
        foreach (var b in source)
        {
            span[offset++] = ' ';
            span[offset++] = hex[b >> 4];
            span[offset++] = hex[b & 0xF];
        }

        return new string(span);
    }
}
