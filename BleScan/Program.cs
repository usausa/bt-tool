// ReSharper disable UseObjectOrCollectionInitializer
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

var rootCommand = new RootCommand("BLE scan tool");
rootCommand.Handler = CommandHandler.Create(() =>
{
    var watcher = new BluetoothLEAdvertisementWatcher
    {
        ScanningMode = BluetoothLEScanningMode.Passive
    };

    watcher.Received += WatcherOnReceived;

    // TODO
    // Watcherのオプション
    // 1度だけ
    // Deviceが必要なこと
    // GATTの詳細表示

    async void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // TODO ロック
        // TODO 基本情報で出せること
        Debug.WriteLine($"Address: {args.BluetoothAddress:X12}");

        // Device
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress);

        // Gatt
        var gatt = device is not null ? await device.GetGattServicesAsync() : null;
        //if (gatt?.Services.Count > 0)
        //{
        //    for (var i = 0; i < gatt!.Services.Count; i++)
        //    {
        //        Console.WriteLine($"  {gatt.Services[i].Uuid}");
        //    }
        //}

        Console.WriteLine($"{args.Timestamp:HH:mm:ss.fff} {args.BluetoothAddress:X12} {args.RawSignalStrengthInDBm} {device?.Name} {gatt?.Status} {gatt?.Services.Count}");
    }

    watcher.Start();

    Console.ReadLine();
});

return await rootCommand.InvokeAsync(args).ConfigureAwait(false);
