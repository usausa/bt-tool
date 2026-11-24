// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable MemberCanBeProtected.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace DeviceTool.Bluetooth;

using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;

using Smart.CommandLine.Hosting;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

using static DeviceTool.Bluetooth.AddressHelper;
using static DeviceTool.Bluetooth.ConsoleHelper;

internal static class CommandBuilderExtensions
{
    public static void AddCommands(this ICommandBuilder commands)
    {
        commands.AddCommand<ScanCommand>();
        commands.AddCommand<GattCommand>();
    }
}

[Command("scan", "Scan BLE advertisements")]
public sealed class ScanCommand : ICommandHandler
{
    private const int MaxConcurrentLookup = 8;

    private static readonly Lock Sync = new();

    private static readonly SemaphoreSlim LookupLimiter = new(MaxConcurrentLookup, MaxConcurrentLookup);

    private bool stopped;

    [Option<bool>("--active", "-a", Description = "Active scanning")]
    public bool Active { get; set; }

    [Option<bool>("--once", "-o", Description = "Show each device only once")]
    public bool Once { get; set; }

    [Option<bool>("--info", "-i", Description = "Show device information")]
    public bool Info { get; set; }

    [Option<bool>("--gatt", "-g", Description = "Show GATT services")]
    public bool Gatt { get; set; }

    [Option<bool>("--manufacturer", "-m", Description = "Show manufacturer data")]
    public bool Manufacturer { get; set; }

    [Option<bool>("--section", "-s", Description = "Show data sections")]
    public bool Section { get; set; }

    [Option<int>("--duration", "-d", Description = "Scan duration in seconds (0: until Enter or Ctrl+C)", DefaultValue = 0)]
    public int Duration { get; set; }

    [Option<int?>("--rssi", "-r", Description = "Minimum RSSI in dBm (e.g. -70)")]
    public int? Rssi { get; set; }

    [Option<string>("--name", "-n", Description = "Filter by name (partial match)")]
    public string? Name { get; set; }

    [Option<string>("--address", Description = "Filter by address (e.g. AA:BB:CC:DD:EE:FF)")]
    public string? Address { get; set; }

    [Option<string>("--company", "-c", Description = "Filter by company ID (e.g. 0x0969)")]
    public string? Company { get; set; }

    public async ValueTask ExecuteAsync(CommandContext context)
    {
        if (Duration < 0)
        {
            await Console.Error.WriteLineAsync($"Duration must be 0 or greater. duration=[{Duration}]");
            context.ExitCode = 1;
            return;
        }

        ulong? targetAddress = null;
        if (Address is not null)
        {
            if (!TryParseAddress(Address, out var value))
            {
                await Console.Error.WriteLineAsync($"Invalid address. address=[{Address}]");
                context.ExitCode = 1;
                return;
            }

            targetAddress = value;
        }

        ushort? targetCompany = null;
        if (Company is not null)
        {
            if (!TryParseCompany(Company, out var value))
            {
                await Console.Error.WriteLineAsync($"Invalid company ID. company=[{Company}]");
                context.ExitCode = 1;
                return;
            }

            targetCompany = value;
        }

        var claims = new HashSet<ulong>();
        var names = new Dictionary<ulong, string?>();
        var failed = 0;
        var skipped = 0;
        var stop = new TaskCompletionSource<BluetoothError>(TaskCreationOptions.RunContinuationsAsynchronously);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = Active ? BluetoothLEScanningMode.Active : BluetoothLEScanningMode.Passive
        };

        watcher.Received += WatcherOnReceived;
        watcher.Stopped += WatcherOnStopped;

#pragma warning disable CA1031
        // ReSharper disable once AsyncVoidMethod
        async void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var address = args.BluetoothAddress;
            var claimed = false;
            var acquired = false;
            var printed = false;
            BluetoothLEDevice? device = null;
            try
            {
                if ((Rssi.HasValue && (args.RawSignalStrengthInDBm < Rssi.Value)) ||
                    (targetAddress.HasValue && (address != targetAddress.Value)) ||
                    (targetCompany.HasValue && args.Advertisement.ManufacturerData.All(x => x.CompanyId != targetCompany.Value)))
                {
                    return;
                }

                var localName = args.Advertisement.LocalName;
                bool lookup;
                lock (Sync)
                {
                    if (stopped)
                    {
                        return;
                    }

                    if (!String.IsNullOrEmpty(localName))
                    {
                        names[address] = localName;
                    }

                    if (Once)
                    {
                        if (!claims.Add(address))
                        {
                            return;
                        }

                        claimed = true;
                    }

                    lookup = Info || Gatt || !names.ContainsKey(address);
                }

                if (lookup)
                {
                    if (!await LookupLimiter.WaitAsync(0))
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }

                    acquired = true;
                    device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                    var deviceName = device?.Name;
                    lock (Sync)
                    {
                        if (String.IsNullOrEmpty(names.GetValueOrDefault(address)))
                        {
                            names[address] = String.IsNullOrEmpty(deviceName) ? null : deviceName;
                        }
                    }
                }

                string? name;
                lock (Sync)
                {
                    name = names.GetValueOrDefault(address);
                }

                if ((Name is not null) && ((name is null) || !name.Contains(Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                var gatt = (Gatt && (device is not null)) ? await GattReader.ReadAsync(device, BluetoothCacheMode.Cached, false) : null;

                lock (Sync)
                {
                    if (stopped)
                    {
                        return;
                    }

                    ConsoleWrite(ConsoleColor.Cyan, $"{args.Timestamp:HH:mm:ss.fff}");
                    ConsoleWrite(Console.ForegroundColor, " [");
                    ConsoleWrite(ConsoleColor.DarkCyan, ToAddressString(address));
                    ConsoleWrite(Console.ForegroundColor, "] ");
                    ConsoleWrite(ConsoleColor.Yellow, "RSSI:");
                    ConsoleWrite(Console.ForegroundColor, $"{args.RawSignalStrengthInDBm}");
                    ConsoleWrite(Console.ForegroundColor, " ");
                    ConsoleWriteLine(ConsoleColor.Magenta, name ?? "(Unknown)");

                    if (Info && (device is not null))
                    {
                        DeviceWriter.WriteInfo(device);
                    }

                    if (gatt is not null)
                    {
                        DeviceWriter.WriteGatt(gatt);
                    }

                    if (Manufacturer)
                    {
                        foreach (var md in args.Advertisement.ManufacturerData)
                        {
                            ConsoleWrite(ConsoleColor.Yellow, "CompanyId:");
                            ConsoleWriteLine(Console.ForegroundColor, $" 0x{md.CompanyId:X4}");
                            ConsoleWriteLine(ConsoleColor.Yellow, "Data:");
                            ConsoleWriteHex(md.Data.ToArray(), String.Empty);
                        }
                    }

                    if (Section)
                    {
                        foreach (var ds in args.Advertisement.DataSections)
                        {
                            ConsoleWrite(ConsoleColor.Yellow, "DataType:");
                            ConsoleWriteLine(Console.ForegroundColor, $" 0x{ds.DataType:X2}");
                            ConsoleWriteLine(ConsoleColor.Yellow, "Data:");
                            ConsoleWriteHex(ds.Data.ToArray(), String.Empty);
                        }
                    }

                    printed = true;
                }
            }
            catch
            {
                Interlocked.Increment(ref failed);
                lock (Sync)
                {
                    if (!stopped)
                    {
                        ConsoleWriteLine(ConsoleColor.Red, $"[{ToAddressString(address)}] (Failed get information)");
                    }
                }
            }
            finally
            {
                device?.Dispose();

                if (acquired)
                {
                    LookupLimiter.Release();
                }

                if (claimed && !printed)
                {
                    lock (Sync)
                    {
                        claims.Remove(address);
                    }
                }
            }
        }
#pragma warning restore CA1031

        void WatcherOnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            if (args.Error != BluetoothError.Success)
            {
                stop.TrySetResult(args.Error);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        if (Duration > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(Duration));
        }

        await using var registration = timeout.Token.Register(() => stop.TrySetResult(BluetoothError.Success));

        watcher.Start();

        _ = Task.Run(() =>
        {
            if (Console.ReadLine() is not null)
            {
                stop.TrySetResult(BluetoothError.Success);
            }
        }, context.CancellationToken);

        var error = await stop.Task;

        lock (Sync)
        {
            stopped = true;
        }

        watcher.Received -= WatcherOnReceived;
        watcher.Stopped -= WatcherOnStopped;
        watcher.Stop();

        if ((failed > 0) || (skipped > 0))
        {
            ConsoleWriteLine(ConsoleColor.DarkGray, $"(Failed: {failed}, Skipped: {skipped})");
        }

        if (error != BluetoothError.Success)
        {
            await Console.Error.WriteLineAsync($"Scan aborted. error=[{error}]");
            context.ExitCode = 1;
        }
    }

    private static bool TryParseCompany(string value, out ushort company)
    {
        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if ((hex.Length is >= 1 and <= 4) && UInt16.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out company))
        {
            return true;
        }

        company = 0;
        return false;
    }
}

[Command("gatt", "Show GATT services of a device")]
public sealed class GattCommand : ICommandHandler
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(10);

    [Option<string>("--address", "-a", Description = "Device address (e.g. AA:BB:CC:DD:EE:FF)", Required = true)]
    public string Address { get; set; } = default!;

    [Option<bool>("--read", "-r", Description = "Read characteristic values")]
    public bool Read { get; set; }

    [Option<bool>("--uncached", "-u", Description = "Do not use GATT cache")]
    public bool Uncached { get; set; }

    public async ValueTask ExecuteAsync(CommandContext context)
    {
        if (!TryParseAddress(Address, out var address))
        {
            await Console.Error.WriteLineAsync($"Invalid address. address=[{Address}]");
            context.ExitCode = 1;
            return;
        }

        using var device = await FindDeviceAsync(address, context.CancellationToken);
        if (device is null)
        {
            if (!context.CancellationToken.IsCancellationRequested)
            {
                await Console.Error.WriteLineAsync($"Device not found. address=[{Address}]");
                context.ExitCode = 1;
            }

            return;
        }

        var name = device.Name;
        ConsoleWrite(Console.ForegroundColor, "[");
        ConsoleWrite(ConsoleColor.DarkCyan, ToAddressString(address));
        ConsoleWrite(Console.ForegroundColor, "] ");
        ConsoleWriteLine(ConsoleColor.Magenta, String.IsNullOrEmpty(name) ? "(Unknown)" : name);
        DeviceWriter.WriteInfo(device);

        var gatt = await GattReader.ReadAsync(device, Uncached ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached, Read);
        DeviceWriter.WriteGatt(gatt);
        if (gatt.Status != GattCommunicationStatus.Success)
        {
            context.ExitCode = 1;
        }
    }

    private static async ValueTask<BluetoothLEDevice?> FindDeviceAsync(ulong address, CancellationToken cancellationToken)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is not null)
        {
            return device;
        }

        var addressType = await DiscoverAsync(address, cancellationToken);
        return addressType.HasValue ? await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType.Value) : null;
    }

    private static async ValueTask<BluetoothAddressType?> DiscoverAsync(ulong address, CancellationToken cancellationToken)
    {
        var found = new TaskCompletionSource<BluetoothAddressType?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += WatcherOnReceived;

        void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (args.BluetoothAddress == address)
            {
                found.TrySetResult(args.BluetoothAddressType);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscoveryTimeout);
        await using var registration = timeout.Token.Register(() => found.TrySetResult(null));

        watcher.Start();
        var addressType = await found.Task;

        watcher.Received -= WatcherOnReceived;
        watcher.Stop();

        return addressType;
    }
}
