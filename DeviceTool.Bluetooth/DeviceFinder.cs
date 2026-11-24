namespace DeviceTool.Bluetooth;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

internal static class DeviceFinder
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan NameTimeout = TimeSpan.FromSeconds(3);

    public static async ValueTask<BluetoothLEDevice?> FindDeviceAsync(ulong address, CancellationToken cancellationToken)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is not null)
        {
            return device;
        }

        var args = await DiscoverAsync(address, static _ => true, DiscoveryTimeout, cancellationToken);
        return args is not null ? await BluetoothLEDevice.FromBluetoothAddressAsync(address, args.BluetoothAddressType) : null;
    }

    public static async ValueTask<string?> FindLocalNameAsync(ulong address, CancellationToken cancellationToken)
    {
        var args = await DiscoverAsync(address, static x => !String.IsNullOrEmpty(x.Advertisement.LocalName), NameTimeout, cancellationToken);
        return args?.Advertisement.LocalName;
    }

    private static async ValueTask<BluetoothLEAdvertisementReceivedEventArgs?> DiscoverAsync(ulong address, Func<BluetoothLEAdvertisementReceivedEventArgs, bool> predicate, TimeSpan duration, CancellationToken cancellationToken)
    {
        var found = new TaskCompletionSource<BluetoothLEAdvertisementReceivedEventArgs?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += WatcherOnReceived;

        void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if ((args.BluetoothAddress == address) && predicate(args))
            {
                found.TrySetResult(args);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        await using var registration = timeout.Token.Register(() => found.TrySetResult(null));

        watcher.Start();
        var result = await found.Task;

        watcher.Received -= WatcherOnReceived;
        watcher.Stop();

        return result;
    }
}
