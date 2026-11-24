namespace DeviceTool.Bluetooth;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Foundation;

using static DeviceTool.Bluetooth.ConsoleHelper;

internal sealed class StopSignal : IDisposable
{
    public const int MaxDuration = 4294967;

    private readonly TaskCompletionSource<BluetoothError> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource timeout;

    private readonly CancellationTokenRegistration registration;

    public Task<BluetoothError> Task => completion.Task;

    public TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs> WatcherStoppedHandler { get; }

    public StopSignal(int duration, CancellationToken cancellationToken)
    {
        timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (duration > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(duration));
        }

        registration = timeout.Token.Register(() => completion.TrySetResult(BluetoothError.Success));
        WatcherStoppedHandler = (_, args) =>
        {
            if (args.Error != BluetoothError.Success)
            {
                completion.TrySetResult(args.Error);
            }
        };
        _ = WaitForEnterAsync(cancellationToken);
    }

    public void Dispose()
    {
        registration.Dispose();
        timeout.Dispose();
    }

    private async Task WaitForEnterAsync(CancellationToken cancellationToken)
    {
        if (await ConsoleWaitForLineAsync(cancellationToken))
        {
            completion.TrySetResult(BluetoothError.Success);
        }
    }
}
