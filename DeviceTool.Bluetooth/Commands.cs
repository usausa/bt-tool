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
using Windows.Devices.Enumeration;

using static DeviceTool.Bluetooth.AddressHelper;
using static DeviceTool.Bluetooth.ConsoleHelper;

internal static class CommandBuilderExtensions
{
    public static void AddCommands(this ICommandBuilder commands)
    {
        commands.AddCommand<ScanCommand>();
        commands.AddCommand<FindCommand>();
        commands.AddCommand<GattCommand>();
        commands.AddCommand<AdapterCommand>();
        commands.AddCommand<DevicesCommand>();
        commands.AddCommand<ReadCommand>();
        commands.AddCommand<WriteCommand>();
        commands.AddCommand<NotifyCommand>();
    }
}

[Command("scan", "Scan BLE advertisements")]
public sealed class ScanCommand : ICommandHandler
{
    private const int MaxConcurrentLookup = 8;

    private static readonly TimeSpan ScanResponseDelay = TimeSpan.FromSeconds(1);

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

    [Option<bool>("--extra", "-x", Description = "Show extra advertisement information")]
    public bool Extra { get; set; }

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

    [Option<string>("--service", "-u", Description = "Filter by service UUID (e.g. 180F)")]
    public string? Service { get; set; }

    [Option<bool>("--extended", Description = "Receive extended advertisements")]
    public bool Extended { get; set; }

    [Option<bool>("--decode", "-e", Description = "Decode advertisement data with decoder definitions")]
    public bool Decode { get; set; }

    [Option<string>("--decoders", Description = "Decoder definition file (default: built-in)")]
    public string? Decoders { get; set; }

    [Option<string>("--format", "-f", Description = "Output format", DefaultValue = "text", Completions = ["text", "json"])]
    public string Format { get; set; } = default!;

    [Option<bool>("--summary", Description = "Show summary at the end")]
    public bool Summary { get; set; }

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

        Guid? targetService = null;
        if (Service is not null)
        {
            if (!BluetoothNames.TryParseUuid(Service, out var value))
            {
                await Console.Error.WriteLineAsync($"Invalid service UUID. service=[{Service}]");
                context.ExitCode = 1;
                return;
            }

            targetService = value;
        }

        var json = String.Equals(Format, "json", StringComparison.OrdinalIgnoreCase);
        if (!json && !String.Equals(Format, "text", StringComparison.OrdinalIgnoreCase))
        {
            await Console.Error.WriteLineAsync($"Invalid format. format=[{Format}]");
            context.ExitCode = 1;
            return;
        }

        AdvertisementDecoder? decoder = null;
        if (Decode || (Decoders is not null) || json)
        {
            if (!DecoderLoader.TryLoad(Decoders, out var definitions, out var message))
            {
                await Console.Error.WriteLineAsync($"Invalid decoder file. file=[{Decoders ?? "(Built-in)"}] error=[{message}]");
                context.ExitCode = 1;
                return;
            }

            decoder = new AdvertisementDecoder(definitions);
        }

        var claims = new HashSet<ulong>();
        var names = new Dictionary<ulong, string?>();
        var summary = Summary ? new ScanSummary() : null;
        var failed = 0;
        var skipped = 0;
        var stop = new TaskCompletionSource<BluetoothError>(TaskCreationOptions.RunContinuationsAsynchronously);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = Active ? BluetoothLEScanningMode.Active : BluetoothLEScanningMode.Passive,
            AllowExtendedAdvertisements = Extended
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
                    (targetCompany.HasValue && args.Advertisement.ManufacturerData.All(x => x.CompanyId != targetCompany.Value)) ||
                    (targetService.HasValue && !HasService(args.Advertisement, targetService.Value)))
                {
                    return;
                }

                var localName = args.Advertisement.LocalName;
                bool wait;
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

                    summary?.Add(address, args.RawSignalStrengthInDBm, args.Timestamp);

                    if (Once)
                    {
                        if (!claims.Add(address))
                        {
                            return;
                        }

                        claimed = true;
                    }

                    wait = Once && Active && String.IsNullOrEmpty(names.GetValueOrDefault(address));
                }

                if (wait)
                {
                    await Task.Delay(ScanResponseDelay);
                }

                bool lookup;
                lock (Sync)
                {
                    if (stopped)
                    {
                        return;
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
                    var deviceName = NormalizeName(device?.Name, address);
                    lock (Sync)
                    {
                        if (String.IsNullOrEmpty(names.GetValueOrDefault(address)))
                        {
                            names[address] = deviceName;
                        }
                    }
                }

                string? name;
                lock (Sync)
                {
                    name = names.GetValueOrDefault(address);
                }

                if (!MatchesName(name, Name))
                {
                    return;
                }

                var gatt = (Gatt && (device is not null)) ? await GattReader.ReadAsync(device, BluetoothCacheMode.Cached, false) : null;
                var decoded = decoder?.Decode(CreatePayload(args.Advertisement, name)) ?? [];
                var line = json ? ScanJsonWriter.FormatAdvertisement(args, name, decoded, Info, device, Gatt, gatt) : null;

                lock (Sync)
                {
                    if (stopped)
                    {
                        return;
                    }

                    if (line is not null)
                    {
                        Console.Out.WriteLine(line);
                    }
                    else
                    {
                        WriteAdvertisement(args, name, device, gatt, decoded);
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
                        WriteStatus(json, ConsoleColor.Red, $"[{ToAddressString(address)}] (Failed get information)");
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

        List<ScanSummaryItem>? items;
        lock (Sync)
        {
            stopped = true;
            items = summary?.GetItems(names, x => MatchesName(x, Name));
        }

        watcher.Received -= WatcherOnReceived;
        watcher.Stopped -= WatcherOnStopped;
        watcher.Stop();

        if ((failed > 0) || (skipped > 0))
        {
            WriteStatus(json, ConsoleColor.DarkGray, $"(Failed: {failed}, Skipped: {skipped})");
        }

        if (items is not null)
        {
            if (json)
            {
                await Console.Out.WriteLineAsync(ScanJsonWriter.FormatSummary(items));
            }
            else
            {
                ScanSummary.Write(items);
            }
        }

        if (error != BluetoothError.Success)
        {
            await Console.Error.WriteLineAsync($"Scan aborted. error=[{error}]");
            context.ExitCode = 1;
        }
    }

    private static bool MatchesName(string? name, string? filter) =>
        (filter is null) || (name?.Contains(filter, StringComparison.OrdinalIgnoreCase) is true);

    private static bool HasService(BluetoothLEAdvertisement advertisement, Guid uuid) =>
        advertisement.ServiceUuids.Contains(uuid) || GetServiceData(advertisement).Exists(x => x.Uuid == uuid);

    private static List<ServiceDataEntry> GetServiceData(BluetoothLEAdvertisement advertisement)
    {
        var list = new List<ServiceDataEntry>();
        foreach (var section in advertisement.DataSections)
        {
            var entry = ServiceDataEntry.Parse(section.DataType, section.Data.ToArray());
            if (entry is not null)
            {
                list.Add(entry);
            }
        }

        return list;
    }

    private static AdvertisementPayload CreatePayload(BluetoothLEAdvertisement advertisement, string? name) =>
        new(name, [.. advertisement.ManufacturerData.Select(static x => new ManufacturerDataEntry(x.CompanyId, x.Data.ToArray()))], GetServiceData(advertisement));

    private static void WriteStatus(bool json, ConsoleColor color, string message)
    {
        if (json)
        {
            Console.Error.WriteLine(message);
        }
        else
        {
            ConsoleWriteLine(color, message);
        }
    }

    private void WriteAdvertisement(BluetoothLEAdvertisementReceivedEventArgs args, string? name, BluetoothLEDevice? device, GattInfo? gatt, IEnumerable<DecodedResult> decoded)
    {
        ConsoleWrite(ConsoleColor.Cyan, $"{args.Timestamp:HH:mm:ss.fff}");
        ConsoleWrite(Console.ForegroundColor, " [");
        ConsoleWrite(ConsoleColor.DarkCyan, ToAddressString(args.BluetoothAddress));
        ConsoleWrite(Console.ForegroundColor, "] ");
        ConsoleWrite(ConsoleColor.Yellow, "RSSI:");
        ConsoleWrite(Console.ForegroundColor, $"{args.RawSignalStrengthInDBm}");
        ConsoleWrite(Console.ForegroundColor, " ");
        ConsoleWriteLine(ConsoleColor.Magenta, name ?? "(Unknown)");

        if (Extra)
        {
            WriteExtra(args);
        }

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
                WriteNamedValue("CompanyId:", $"0x{md.CompanyId:X4}", BluetoothNames.GetCompanyName(md.CompanyId));
                ConsoleWriteLine(ConsoleColor.Yellow, "Data:");
                ConsoleWriteHex(md.Data.ToArray(), String.Empty);
            }
        }

        if (Section)
        {
            foreach (var ds in args.Advertisement.DataSections)
            {
                WriteNamedValue("DataType:", $"0x{ds.DataType:X2}", BluetoothNames.GetAdTypeName(ds.DataType));
                ConsoleWriteLine(ConsoleColor.Yellow, "Data:");
                ConsoleWriteHex(ds.Data.ToArray(), String.Empty);
            }
        }

        foreach (var result in decoded)
        {
            WriteDecoded(result);
        }
    }

    private static void WriteExtra(BluetoothLEAdvertisementReceivedEventArgs args)
    {
        WriteNamedValue("AdvertisementType:", args.AdvertisementType.ToString(), null);
        WriteNamedValue("AddressType:", args.BluetoothAddressType.ToString(), null);
        if (args.TransmitPowerLevelInDBm is { } txPower)
        {
            WriteNamedValue("TxPower:", $"{txPower} dBm", null);
        }

        var uuids = args.Advertisement.ServiceUuids;
        if (uuids.Count == 0)
        {
            return;
        }

        ConsoleWriteLine(ConsoleColor.Yellow, "ServiceUuids:");
        foreach (var uuid in uuids)
        {
            var name = BluetoothNames.GetServiceName(uuid);
            if (name is null)
            {
                ConsoleWriteLine(Console.ForegroundColor, $"  {uuid}");
            }
            else
            {
                DeviceWriter.WriteService(uuid, name);
            }
        }
    }

    private static void WriteDecoded(DecodedResult result)
    {
        ConsoleWrite(ConsoleColor.Yellow, "Decoded:");
        ConsoleWrite(ConsoleColor.Blue, $" {result.Name}");
        foreach (var field in result.Fields)
        {
            ConsoleWrite(Console.ForegroundColor, (field.Unit is null) ? $" {field.Name}={field.Value}" : $" {field.Name}={field.Value} {field.Unit}");
        }

        Console.WriteLine();
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

    private static void WriteNamedValue(string label, string value, string? name)
    {
        ConsoleWrite(ConsoleColor.Yellow, label);
        ConsoleWrite(Console.ForegroundColor, $" {value}");
        if (name is not null)
        {
            ConsoleWrite(ConsoleColor.Blue, $" {name}");
        }

        Console.WriteLine();
    }
}

[Command("find", "Track the signal strength of a device")]
public sealed class FindCommand : ICommandHandler
{
    private const int BarLength = 20;

    private const int BarMinRssi = -100;

    private const int BarMaxRssi = -30;

    private const int StrongRssi = -60;

    private const int MediumRssi = -75;

    private const string BlockSymbols = "\u2588\u2591";

    private const string AsciiSymbols = "#-";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan LineInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(3);

    [Option<string>("--address", "-a", Description = "Device address (e.g. AA:BB:CC:DD:EE:FF)", Required = true)]
    public string Address { get; set; } = default!;

    [Option<int>("--duration", "-d", Description = "Duration in seconds (0: until Enter or Ctrl+C)", DefaultValue = 0)]
    public int Duration { get; set; }

    public async ValueTask ExecuteAsync(CommandContext context)
    {
        if (Duration < 0)
        {
            await Console.Error.WriteLineAsync($"Duration must be 0 or greater. duration=[{Duration}]");
            context.ExitCode = 1;
            return;
        }

        if (!TryParseAddress(Address, out var address))
        {
            await Console.Error.WriteLineAsync($"Invalid address. address=[{Address}]");
            context.ExitCode = 1;
            return;
        }

        var tracker = new SignalTracker();
        var stop = new TaskCompletionSource<BluetoothError>(TaskCreationOptions.RunContinuationsAsynchronously);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += WatcherOnReceived;
        watcher.Stopped += WatcherOnStopped;

        void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (args.BluetoothAddress == address)
            {
                tracker.Add(args.RawSignalStrengthInDBm, args.Advertisement.LocalName);
            }
        }

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

        var name = await GetNameAsync(address);
        DeviceWriter.WriteDevice(address, tracker.GetSnapshot().Name ?? name);

        await ShowAsync(tracker, stop.Task);

        watcher.Received -= WatcherOnReceived;
        watcher.Stopped -= WatcherOnStopped;
        watcher.Stop();

        WriteTotal(tracker.GetSnapshot());

        var error = await stop.Task;
        if (error != BluetoothError.Success)
        {
            await Console.Error.WriteLineAsync($"Scan aborted. error=[{error}]");
            context.ExitCode = 1;
        }
    }

    private static async ValueTask<string?> GetNameAsync(ulong address)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        return NormalizeName(device?.Name, address);
    }

    private static async ValueTask ShowAsync(SignalTracker tracker, Task stop)
    {
        var live = !Console.IsOutputRedirected;
        var symbols = GetSymbols();
        using var timer = new PeriodicTimer(live ? RefreshInterval : LineInterval);
        var width = 0;
        while (await Task.WhenAny(timer.WaitForNextTickAsync().AsTask(), stop) != stop)
        {
            var snapshot = tracker.GetSnapshot();
            if (live)
            {
                width = WriteLive(snapshot, symbols, width);
            }
            else
            {
                WriteSignal(snapshot, symbols);
                Console.WriteLine();
            }
        }

        if (live && (Console.CursorLeft > 0))
        {
            Console.WriteLine();
        }
    }

    private static string GetSymbols()
    {
        var encoding = Console.OutputEncoding;
        return (encoding.GetString(encoding.GetBytes(BlockSymbols)) == BlockSymbols) ? BlockSymbols : AsciiSymbols;
    }

    private static int WriteLive(SignalSnapshot snapshot, string symbols, int width)
    {
        Console.Write('\r');
        WriteSignal(snapshot, symbols);
        var end = Console.CursorLeft;
        if (end < width)
        {
            Console.Write(new string(' ', width - end));
        }

        return end;
    }

    private static void WriteSignal(SignalSnapshot snapshot, string symbols)
    {
        ConsoleWrite(ConsoleColor.Cyan, $"{DateTime.Now:HH:mm:ss}");
        if ((snapshot.Count == 0) || (snapshot.Silence >= SignalTimeout))
        {
            ConsoleWrite(ConsoleColor.DarkGray, " (no signal)");
            return;
        }

        ConsoleWrite(Console.ForegroundColor, String.Create(CultureInfo.InvariantCulture, $" RSSI {snapshot.Rssi} dBm  avg {snapshot.RecentAverage}  min {snapshot.MinRssi}  max {snapshot.MaxRssi}  "));
        ConsoleWrite(GetColor(snapshot.RecentAverage), GetBar(snapshot.RecentAverage, symbols));
    }

    private static void WriteTotal(SignalSnapshot snapshot)
    {
        ConsoleWriteLine(Console.ForegroundColor, (snapshot.Count == 0) ? "Packets: 0" : String.Create(CultureInfo.InvariantCulture, $"Packets: {snapshot.Count}  RSSI avg {snapshot.Average}  min {snapshot.MinRssi}  max {snapshot.MaxRssi}"));
    }

    private static string GetBar(int rssi, string symbols)
    {
        var length = Math.Clamp((int)Math.Round((double)(rssi - BarMinRssi) * BarLength / (BarMaxRssi - BarMinRssi), MidpointRounding.AwayFromZero), 0, BarLength);
        return new string(symbols[0], length) + new string(symbols[1], BarLength - length);
    }

    private static ConsoleColor GetColor(int rssi) => rssi switch
    {
        >= StrongRssi => ConsoleColor.Green,
        >= MediumRssi => ConsoleColor.Yellow,
        _ => ConsoleColor.Red
    };
}

[Command("gatt", "Show GATT services of a device")]
public sealed class GattCommand : ICommandHandler
{
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

        using var device = await DeviceFinder.FindDeviceAsync(address, context.CancellationToken);
        if (device is null)
        {
            if (!context.CancellationToken.IsCancellationRequested)
            {
                await Console.Error.WriteLineAsync($"Device not found. address=[{Address}]");
                context.ExitCode = 1;
            }

            return;
        }

        var name = NormalizeName(device.Name, address);
        if (name is null)
        {
            name = await DeviceFinder.FindLocalNameAsync(address, context.CancellationToken);
            if (context.CancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        DeviceWriter.WriteDevice(address, name);
        DeviceWriter.WriteInfo(device);

        var gatt = await GattReader.ReadAsync(device, Uncached ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached, Read);
        DeviceWriter.WriteGatt(gatt);
        if (gatt.Status != GattCommunicationStatus.Success)
        {
            context.ExitCode = 1;
        }
    }
}

[Command("adapter", "Show Bluetooth adapter information")]
public sealed class AdapterCommand : ICommandHandler
{
    public async ValueTask ExecuteAsync(CommandContext context)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
        {
            await Console.Error.WriteLineAsync("Bluetooth adapter not found.");
            context.ExitCode = 1;
            return;
        }

        var radio = await adapter.GetRadioAsync();
        DeviceWriter.WriteAdapter(adapter, radio);
    }
}

[Command("devices", "List paired BLE devices")]
public sealed class DevicesCommand : ICommandHandler
{
    private const string AddressProperty = "System.Devices.Aep.DeviceAddress";

    private const string ConnectedProperty = "System.Devices.Aep.IsConnected";

    private const string PresentProperty = "System.Devices.Aep.IsPresent";

    [Option<bool>("--all", "-a", Description = "Include disconnected devices")]
    public bool All { get; set; }

    public async ValueTask ExecuteAsync(CommandContext context)
    {
        var infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), [AddressProperty, ConnectedProperty, PresentProperty]);
        var devices = new List<(ulong Address, string? Name, bool Connected)>();
        foreach (var info in infos)
        {
            if ((info.Properties.GetValueOrDefault(PresentProperty) is not true) ||
                (info.Properties.GetValueOrDefault(AddressProperty) is not string text) ||
                !TryParseAddress(text, out var address))
            {
                continue;
            }

            var connected = info.Properties.GetValueOrDefault(ConnectedProperty) is true;
            if (!All && !connected)
            {
                continue;
            }

            devices.Add((address, NormalizeName(info.Name, address), connected));
        }

        if (devices.Count == 0)
        {
            ConsoleWriteLine(ConsoleColor.DarkGray, "(No devices)");
            return;
        }

        foreach (var (address, name, connected) in devices.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(static x => x.Address))
        {
            var status = connected ? BluetoothConnectionStatus.Connected : BluetoothConnectionStatus.Disconnected;
            ConsoleWrite(Console.ForegroundColor, "[");
            ConsoleWrite(ConsoleColor.DarkCyan, ToAddressString(address));
            ConsoleWrite(Console.ForegroundColor, "] ");
            ConsoleWrite(connected ? ConsoleColor.Green : ConsoleColor.DarkGray, $"{status,-12}");
            ConsoleWrite(Console.ForegroundColor, " ");
            ConsoleWriteLine(ConsoleColor.Magenta, name ?? "(Unknown)");
        }
    }
}

public abstract class CharacteristicCommandBase : ICommandHandler
{
    [Option<string>("--address", "-a", Description = "Device address (e.g. AA:BB:CC:DD:EE:FF)", Required = true)]
    public string Address { get; set; } = default!;

    [Option<string>("--service", "-s", Description = "Service UUID (e.g. 180F)")]
    public string? Service { get; set; }

    [Option<string>("--characteristic", "-c", Description = "Characteristic UUID (e.g. 2A19)", Required = true)]
    public string Characteristic { get; set; } = default!;

    public async ValueTask ExecuteAsync(CommandContext context)
    {
        if (!TryParseAddress(Address, out var address))
        {
            await Console.Error.WriteLineAsync($"Invalid address. address=[{Address}]");
            context.ExitCode = 1;
            return;
        }

        Guid? serviceUuid = null;
        if (Service is not null)
        {
            if (!BluetoothNames.TryParseUuid(Service, out var value))
            {
                await Console.Error.WriteLineAsync($"Invalid service UUID. service=[{Service}]");
                context.ExitCode = 1;
                return;
            }

            serviceUuid = value;
        }

        if (!BluetoothNames.TryParseUuid(Characteristic, out var characteristicUuid))
        {
            await Console.Error.WriteLineAsync($"Invalid characteristic UUID. characteristic=[{Characteristic}]");
            context.ExitCode = 1;
            return;
        }

        var error = Validate();
        if (error is not null)
        {
            await Console.Error.WriteLineAsync(error);
            context.ExitCode = 1;
            return;
        }

        using var device = await DeviceFinder.FindDeviceAsync(address, context.CancellationToken);
        if (device is null)
        {
            if (!context.CancellationToken.IsCancellationRequested)
            {
                await Console.Error.WriteLineAsync($"Device not found. address=[{Address}]");
                context.ExitCode = 1;
            }

            return;
        }

        using var search = await GattReader.FindCharacteristicsAsync(device, serviceUuid, characteristicUuid);
        if (search.ServicesStatus != GattCommunicationStatus.Success)
        {
            await Console.Error.WriteLineAsync($"Get services failed. status=[{search.ServicesStatus}]");
            context.ExitCode = 1;
            return;
        }

        if ((Service is not null) && (search.ServiceCount == 0))
        {
            await Console.Error.WriteLineAsync($"Service not found. service=[{Service}]");
            context.ExitCode = 1;
            return;
        }

        if ((search.Matches.Count == 0) && (search.CharacteristicsStatus != GattCommunicationStatus.Success))
        {
            await Console.Error.WriteLineAsync($"Get characteristics failed. status=[{search.CharacteristicsStatus}]");
            context.ExitCode = 1;
            return;
        }

        if (search.Matches.Count == 0)
        {
            await Console.Error.WriteLineAsync($"Characteristic not found. characteristic=[{Characteristic}]");
            context.ExitCode = 1;
            return;
        }

        if (search.Matches.Count > 1)
        {
            await Console.Error.WriteLineAsync($"Multiple characteristics found. characteristic=[{Characteristic}]");
            context.ExitCode = 1;
            return;
        }

        var (service, characteristic) = search.Matches[0];
        DeviceWriter.WriteDevice(address, NormalizeName(device.Name, address));
        DeviceWriter.WriteService(service.Uuid, DisplayHelper.GetServiceName(service));
        DeviceWriter.WriteCharacteristic(characteristic.Uuid, DisplayHelper.GetCharacteristicName(characteristic), characteristic.CharacteristicProperties);

        await ExecuteAsync(context, device, characteristic);
    }

    protected virtual string? Validate() => null;

    protected abstract ValueTask ExecuteAsync(CommandContext context, BluetoothLEDevice device, GattCharacteristic characteristic);
}

[Command("read", "Read a characteristic value")]
public sealed class ReadCommand : CharacteristicCommandBase
{
    protected override async ValueTask ExecuteAsync(CommandContext context, BluetoothLEDevice device, GattCharacteristic characteristic)
    {
        if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
        {
            DeviceWriter.WriteStatus(ConsoleColor.Red, "Read not supported");
            context.ExitCode = 1;
            return;
        }

        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
        {
            DeviceWriter.WriteFailure("Read", result.Status, result.ProtocolError);
            context.ExitCode = 1;
            return;
        }

        DeviceWriter.WriteData(result.Value?.ToArray() ?? []);
    }
}

[Command("write", "Write a characteristic value")]
public sealed class WriteCommand : CharacteristicCommandBase
{
    [Option<string>("--value", "-v", Description = "Value in hex (e.g. 0102FF)", Required = true)]
    public string Value { get; set; } = default!;

    [Option<bool>("--no-response", "-n", Description = "Write without response")]
    public bool NoResponse { get; set; }

    protected override string? Validate() => TryParseValue(Value, out _) ? null : $"Invalid value. value=[{Value}]";

    protected override async ValueTask ExecuteAsync(CommandContext context, BluetoothLEDevice device, GattCharacteristic characteristic)
    {
        _ = TryParseValue(Value, out var data);

        var required = NoResponse ? GattCharacteristicProperties.WriteWithoutResponse : GattCharacteristicProperties.Write;
        if (!characteristic.CharacteristicProperties.HasFlag(required))
        {
            DeviceWriter.WriteStatus(ConsoleColor.Red, NoResponse ? "Write without response not supported" : "Write not supported");
            context.ExitCode = 1;
            return;
        }

        DeviceWriter.WriteData(data);
        var result = await characteristic.WriteValueWithResultAsync(data.AsBuffer(), NoResponse ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse);
        if (result.Status != GattCommunicationStatus.Success)
        {
            DeviceWriter.WriteFailure("Write", result.Status, result.ProtocolError);
            context.ExitCode = 1;
            return;
        }

        DeviceWriter.WriteStatus(ConsoleColor.DarkGray, "Write succeeded");
    }

    private static bool TryParseValue(string value, out byte[] data)
    {
        var hex = value.Replace(" ", String.Empty, StringComparison.Ordinal).Replace(":", String.Empty, StringComparison.Ordinal).Replace("-", String.Empty, StringComparison.Ordinal);
        if ((hex.Length > 0) && ((hex.Length % 2) == 0) && hex.All(Char.IsAsciiHexDigit))
        {
            data = Convert.FromHexString(hex);
            return true;
        }

        data = [];
        return false;
    }
}

[Command("notify", "Subscribe to characteristic notifications")]
public sealed class NotifyCommand : CharacteristicCommandBase
{
    private static readonly Lock Sync = new();

    private bool stopped;

    [Option<int>("--duration", "-d", Description = "Duration in seconds (0: until Enter or Ctrl+C)", DefaultValue = 0)]
    public int Duration { get; set; }

    protected override string? Validate() => Duration < 0 ? $"Duration must be 0 or greater. duration=[{Duration}]" : null;

    protected override async ValueTask ExecuteAsync(CommandContext context, BluetoothLEDevice device, GattCharacteristic characteristic)
    {
        var properties = characteristic.CharacteristicProperties;
        GattClientCharacteristicConfigurationDescriptorValue mode;
        if (properties.HasFlag(GattCharacteristicProperties.Notify))
        {
            mode = GattClientCharacteristicConfigurationDescriptorValue.Notify;
        }
        else if (properties.HasFlag(GattCharacteristicProperties.Indicate))
        {
            mode = GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        }
        else
        {
            DeviceWriter.WriteStatus(ConsoleColor.Red, "Notify not supported");
            context.ExitCode = 1;
            return;
        }

        using var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
        session.MaintainConnection = true;

        characteristic.ValueChanged += CharacteristicOnValueChanged;

        void CharacteristicOnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            lock (Sync)
            {
                if (stopped)
                {
                    return;
                }

                ConsoleWriteHex(ConsoleColor.Cyan, $"{args.Timestamp:HH:mm:ss.fff}", args.CharacteristicValue.ToArray());
            }
        }

        var subscribe = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(mode);
        if (subscribe.Status != GattCommunicationStatus.Success)
        {
            characteristic.ValueChanged -= CharacteristicOnValueChanged;
            DeviceWriter.WriteFailure("Subscribe", subscribe.Status, subscribe.ProtocolError);
            context.ExitCode = 1;
            return;
        }

        lock (Sync)
        {
            DeviceWriter.WriteStatus(ConsoleColor.DarkGray, $"Subscribed: {mode}");
        }

        try
        {
            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            if (Duration > 0)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(Duration));
            }

            await using var registration = timeout.Token.Register(() => stop.TrySetResult());

            _ = Task.Run(() =>
            {
                if (Console.ReadLine() is not null)
                {
                    stop.TrySetResult();
                }
            }, context.CancellationToken);

            await stop.Task;
        }
        finally
        {
            lock (Sync)
            {
                stopped = true;
            }

            var unsubscribe = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
            if (unsubscribe.Status != GattCommunicationStatus.Success)
            {
                DeviceWriter.WriteFailure("Unsubscribe", unsubscribe.Status, unsubscribe.ProtocolError);
                context.ExitCode = 1;
            }

            characteristic.ValueChanged -= CharacteristicOnValueChanged;
        }
    }
}
