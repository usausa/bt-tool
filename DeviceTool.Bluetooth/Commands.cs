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

    [Option<string>("--company", "-c", Description = "Filter by company ID (e.g. 0x1234)")]
    public string? Company { get; set; }

    [Option<string>("--service", "-u", Description = "Filter by service UUID (e.g. 180F)")]
    public string? Service { get; set; }

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
        if (Duration is < 0 or > StopSignal.MaxDuration)
        {
            OutputWriter.WriteError($"Duration must be between 0 and {StopSignal.MaxDuration}. duration=[{Duration}]");
            context.ExitCode = 1;
            return;
        }

        ulong? targetAddress = null;
        if (Address is not null)
        {
            if (!TryParseAddress(Address, out var value))
            {
                OutputWriter.WriteError($"Invalid address. address=[{Address}]");
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
                OutputWriter.WriteError($"Invalid company ID. company=[{Company}]");
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
                OutputWriter.WriteError($"Invalid service UUID. service=[{Service}]");
                context.ExitCode = 1;
                return;
            }

            targetService = value;
        }

        var json = String.Equals(Format, "json", StringComparison.OrdinalIgnoreCase);
        if (!json && !String.Equals(Format, "text", StringComparison.OrdinalIgnoreCase))
        {
            OutputWriter.WriteError($"Invalid format. format=[{Format}]");
            context.ExitCode = 1;
            return;
        }

        AdvertisementDecoder? decoder = null;
        if (Decode || (Decoders is not null) || json)
        {
            if (!DecoderLoader.TryLoad(Decoders, out var definitions, out var message))
            {
                OutputWriter.WriteError($"Invalid decoder file. file=[{Decoders ?? "(Built-in)"}] error=[{message}]");
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
        using var stop = new StopSignal(Duration, context.CancellationToken);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = Active ? BluetoothLEScanningMode.Active : BluetoothLEScanningMode.Passive
        };

        watcher.Received += WatcherOnReceived;
        watcher.Stopped += stop.WatcherStoppedHandler;

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
                        OutputWriter.WriteJson(line);
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
                        OutputWriter.WriteScanFailure(address, json);
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

        watcher.Start();

        var error = await stop.Task;

        List<ScanSummaryItem>? items;
        lock (Sync)
        {
            stopped = true;
            items = summary?.GetItems(names, x => MatchesName(x, Name));
        }

        watcher.Received -= WatcherOnReceived;
        watcher.Stopped -= stop.WatcherStoppedHandler;
        watcher.Stop();

        if ((failed > 0) || (skipped > 0))
        {
            OutputWriter.WriteScanCounts(failed, skipped, json);
        }

        if (items is not null)
        {
            if (json)
            {
                OutputWriter.WriteJson(ScanJsonWriter.FormatSummary(items));
            }
            else
            {
                OutputWriter.WriteSummary(items);
            }
        }

        if (error != BluetoothError.Success)
        {
            OutputWriter.WriteError($"Scan aborted. error=[{error}]");
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

    private void WriteAdvertisement(BluetoothLEAdvertisementReceivedEventArgs args, string? name, BluetoothLEDevice? device, GattInfo? gatt, IEnumerable<DecodedResult> decoded)
    {
        OutputWriter.WriteAdvertisement(args.Timestamp, args.BluetoothAddress, args.RawSignalStrengthInDBm, name);

        if (Extra)
        {
            WriteExtra(args);
        }

        if (Info && (device is not null))
        {
            OutputWriter.WriteInfo(device);
        }

        if (gatt is not null)
        {
            OutputWriter.WriteGatt(gatt);
        }

        if (Manufacturer)
        {
            foreach (var md in args.Advertisement.ManufacturerData)
            {
                OutputWriter.WriteNamedValue("CompanyId:", $"0x{md.CompanyId:X4}", BluetoothNames.GetCompanyName(md.CompanyId));
                OutputWriter.WriteLabel("Data:");
                OutputWriter.WriteHex(md.Data.ToArray());
            }
        }

        if (Section)
        {
            foreach (var ds in args.Advertisement.DataSections)
            {
                OutputWriter.WriteNamedValue("DataType:", $"0x{ds.DataType:X2}", BluetoothNames.GetAdTypeName(ds.DataType));
                OutputWriter.WriteLabel("Data:");
                OutputWriter.WriteHex(ds.Data.ToArray());
            }
        }

        foreach (var result in decoded)
        {
            OutputWriter.WriteDecoded(result);
        }
    }

    private static void WriteExtra(BluetoothLEAdvertisementReceivedEventArgs args)
    {
        OutputWriter.WriteNamedValue("AdvertisementType:", args.AdvertisementType.ToString(), null);
        OutputWriter.WriteNamedValue("AddressType:", args.BluetoothAddressType.ToString(), null);
        if (args.TransmitPowerLevelInDBm is { } txPower)
        {
            OutputWriter.WriteNamedValue("TxPower:", $"{txPower} dBm", null);
        }

        var uuids = args.Advertisement.ServiceUuids;
        if (uuids.Count == 0)
        {
            return;
        }

        OutputWriter.WriteLabel("ServiceUuids:");
        foreach (var uuid in uuids)
        {
            OutputWriter.WriteService(uuid, BluetoothNames.GetServiceName(uuid));
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

[Command("find", "Track the signal strength of a device")]
public sealed class FindCommand : ICommandHandler
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan LineInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(3);

    [Option<string>("--address", "-a", Description = "Device address (e.g. AA:BB:CC:DD:EE:FF)", Required = true)]
    public string Address { get; set; } = default!;

    [Option<int>("--duration", "-d", Description = "Duration in seconds (0: until Enter or Ctrl+C)", DefaultValue = 0)]
    public int Duration { get; set; }

    public async ValueTask ExecuteAsync(CommandContext context)
    {
        if (Duration is < 0 or > StopSignal.MaxDuration)
        {
            OutputWriter.WriteError($"Duration must be between 0 and {StopSignal.MaxDuration}. duration=[{Duration}]");
            context.ExitCode = 1;
            return;
        }

        if (!TryParseAddress(Address, out var address))
        {
            OutputWriter.WriteError($"Invalid address. address=[{Address}]");
            context.ExitCode = 1;
            return;
        }

        var tracker = new SignalTracker();
        using var stop = new StopSignal(Duration, context.CancellationToken);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += WatcherOnReceived;
        watcher.Stopped += stop.WatcherStoppedHandler;

        void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (args.BluetoothAddress == address)
            {
                tracker.Add(args.RawSignalStrengthInDBm, args.Advertisement.LocalName);
            }
        }

        watcher.Start();

        var name = await GetNameAsync(address);
        OutputWriter.WriteDevice(address, tracker.GetSnapshot().Name ?? name);

        await ShowAsync(tracker, stop.Task);

        watcher.Received -= WatcherOnReceived;
        watcher.Stopped -= stop.WatcherStoppedHandler;
        watcher.Stop();

        OutputWriter.WriteSignalTotal(tracker.GetSnapshot());

        var error = await stop.Task;
        if (error != BluetoothError.Success)
        {
            OutputWriter.WriteError($"Scan aborted. error=[{error}]");
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
        var live = !OutputWriter.IsOutputRedirected;
        using var timer = new PeriodicTimer(live ? RefreshInterval : LineInterval);
        var width = 0;
        while (await Task.WhenAny(timer.WaitForNextTickAsync().AsTask(), stop) != stop)
        {
            var snapshot = tracker.GetSnapshot();
            var silent = (snapshot.Count == 0) || (snapshot.Silence >= SignalTimeout);
            if (live)
            {
                width = OutputWriter.WriteLiveSignal(snapshot, silent, width);
            }
            else
            {
                OutputWriter.WriteSignal(snapshot, silent);
            }
        }

        if (live)
        {
            OutputWriter.EndLiveSignal();
        }
    }
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
            OutputWriter.WriteError($"Invalid address. address=[{Address}]");
            context.ExitCode = 1;
            return;
        }

        using var device = await DeviceFinder.FindDeviceAsync(address, context.CancellationToken);
        if (device is null)
        {
            if (!context.CancellationToken.IsCancellationRequested)
            {
                OutputWriter.WriteError($"Device not found. address=[{Address}]");
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

        OutputWriter.WriteDevice(address, name);
        OutputWriter.WriteInfo(device);

        var gatt = await GattReader.ReadAsync(device, Uncached ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached, Read);
        OutputWriter.WriteGatt(gatt);
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
            OutputWriter.WriteError("Bluetooth adapter not found.");
            context.ExitCode = 1;
            return;
        }

        var radio = await adapter.GetRadioAsync();
        OutputWriter.WriteAdapter(adapter, radio);
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
            OutputWriter.WriteNoDevices();
            return;
        }

        foreach (var (address, name, connected) in devices.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(static x => x.Address))
        {
            OutputWriter.WritePairedDevice(address, name, connected);
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
            OutputWriter.WriteError($"Invalid address. address=[{Address}]");
            context.ExitCode = 1;
            return;
        }

        Guid? serviceUuid = null;
        if (Service is not null)
        {
            if (!BluetoothNames.TryParseUuid(Service, out var value))
            {
                OutputWriter.WriteError($"Invalid service UUID. service=[{Service}]");
                context.ExitCode = 1;
                return;
            }

            serviceUuid = value;
        }

        if (!BluetoothNames.TryParseUuid(Characteristic, out var characteristicUuid))
        {
            OutputWriter.WriteError($"Invalid characteristic UUID. characteristic=[{Characteristic}]");
            context.ExitCode = 1;
            return;
        }

        var error = Validate();
        if (error is not null)
        {
            OutputWriter.WriteError(error);
            context.ExitCode = 1;
            return;
        }

        using var device = await DeviceFinder.FindDeviceAsync(address, context.CancellationToken);
        if (device is null)
        {
            if (!context.CancellationToken.IsCancellationRequested)
            {
                OutputWriter.WriteError($"Device not found. address=[{Address}]");
                context.ExitCode = 1;
            }

            return;
        }

        using var search = await GattReader.FindCharacteristicsAsync(device, serviceUuid, characteristicUuid);
        if (search.ServicesStatus != GattCommunicationStatus.Success)
        {
            OutputWriter.WriteError($"Get services failed. status=[{search.ServicesStatus}]");
            context.ExitCode = 1;
            return;
        }

        if ((Service is not null) && (search.ServiceCount == 0))
        {
            OutputWriter.WriteError($"Service not found. service=[{Service}]");
            context.ExitCode = 1;
            return;
        }

        if ((search.Matches.Count == 0) && (search.CharacteristicsStatus != GattCommunicationStatus.Success))
        {
            OutputWriter.WriteError($"Get characteristics failed. status=[{search.CharacteristicsStatus}]");
            context.ExitCode = 1;
            return;
        }

        if (search.Matches.Count == 0)
        {
            OutputWriter.WriteError($"Characteristic not found. characteristic=[{Characteristic}]");
            context.ExitCode = 1;
            return;
        }

        if (search.Matches.Count > 1)
        {
            OutputWriter.WriteError($"Multiple characteristics found. characteristic=[{Characteristic}]");
            context.ExitCode = 1;
            return;
        }

        var (service, characteristic) = search.Matches[0];
        OutputWriter.WriteDevice(address, NormalizeName(device.Name, address));
        OutputWriter.WriteService(service.Uuid, DisplayHelper.GetServiceName(service));
        OutputWriter.WriteCharacteristic(characteristic.Uuid, DisplayHelper.GetCharacteristicName(characteristic), characteristic.CharacteristicProperties);

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
            OutputWriter.WriteNotSupported("Read");
            context.ExitCode = 1;
            return;
        }

        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
        {
            OutputWriter.WriteFailure("Read", result.Status, result.ProtocolError);
            context.ExitCode = 1;
            return;
        }

        OutputWriter.WriteData(result.Value?.ToArray() ?? []);
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
            OutputWriter.WriteNotSupported(NoResponse ? "Write without response" : "Write");
            context.ExitCode = 1;
            return;
        }

        OutputWriter.WriteData(data);
        var result = await characteristic.WriteValueWithResultAsync(data.AsBuffer(), NoResponse ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse);
        if (result.Status != GattCommunicationStatus.Success)
        {
            OutputWriter.WriteFailure("Write", result.Status, result.ProtocolError);
            context.ExitCode = 1;
            return;
        }

        OutputWriter.WriteStatus("Write succeeded");
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

    protected override string? Validate() => (Duration is < 0 or > StopSignal.MaxDuration) ? $"Duration must be between 0 and {StopSignal.MaxDuration}. duration=[{Duration}]" : null;

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
            OutputWriter.WriteNotSupported("Notify");
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

                OutputWriter.WriteNotification(args.Timestamp, args.CharacteristicValue.ToArray());
            }
        }

        var subscribe = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(mode);
        if (subscribe.Status != GattCommunicationStatus.Success)
        {
            characteristic.ValueChanged -= CharacteristicOnValueChanged;
            OutputWriter.WriteFailure("Subscribe", subscribe.Status, subscribe.ProtocolError);
            context.ExitCode = 1;
            return;
        }

        lock (Sync)
        {
            OutputWriter.WriteStatus($"Subscribed: {mode}");
        }

        try
        {
            using var stop = new StopSignal(Duration, context.CancellationToken);
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
                OutputWriter.WriteFailure("Unsubscribe", unsubscribe.Status, unsubscribe.ProtocolError);
                context.ExitCode = 1;
            }

            characteristic.ValueChanged -= CharacteristicOnValueChanged;
        }
    }
}
