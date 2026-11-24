namespace DeviceTool.Bluetooth;

using System.Runtime.InteropServices.WindowsRuntime;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

internal sealed record GattInfo(GattCommunicationStatus Status, IReadOnlyList<GattServiceInfo> Services);

internal sealed record GattServiceInfo(Guid Uuid, string Name, GattCommunicationStatus Status, IReadOnlyList<GattCharacteristicInfo> Characteristics);

internal sealed record GattCharacteristicInfo(Guid Uuid, string Name, GattCharacteristicProperties Properties, GattValueInfo? Value);

internal sealed record GattValueInfo(GattCommunicationStatus Status, byte[] Data);

internal static class GattReader
{
    public static async ValueTask<GattInfo> ReadAsync(BluetoothLEDevice device, BluetoothCacheMode cacheMode, bool readValues)
    {
        var result = await device.GetGattServicesAsync(cacheMode);
        var services = result.Services;
        try
        {
            if (result.Status != GattCommunicationStatus.Success)
            {
                return new GattInfo(result.Status, []);
            }

            var list = new List<GattServiceInfo>(services.Count);
            foreach (var service in services)
            {
                list.Add(await ReadServiceAsync(service, cacheMode, readValues));
            }

            return new GattInfo(result.Status, list);
        }
        finally
        {
            if (services is not null)
            {
                foreach (var service in services)
                {
                    service.Dispose();
                }
            }
        }
    }

    private static async ValueTask<GattServiceInfo> ReadServiceAsync(GattDeviceService service, BluetoothCacheMode cacheMode, bool readValues)
    {
        var name = DisplayHelper.GetServiceName(service);
        var result = await service.GetCharacteristicsAsync(cacheMode);
        if (result.Status != GattCommunicationStatus.Success)
        {
            return new GattServiceInfo(service.Uuid, name, result.Status, []);
        }

        var list = new List<GattCharacteristicInfo>(result.Characteristics.Count);
        foreach (var characteristic in result.Characteristics)
        {
            var properties = characteristic.CharacteristicProperties;
            var value = (readValues && properties.HasFlag(GattCharacteristicProperties.Read)) ? await ReadValueAsync(characteristic, cacheMode) : null;
            list.Add(new GattCharacteristicInfo(characteristic.Uuid, DisplayHelper.GetCharacteristicName(characteristic), properties, value));
        }

        return new GattServiceInfo(service.Uuid, name, result.Status, list);
    }

    private static async ValueTask<GattValueInfo> ReadValueAsync(GattCharacteristic characteristic, BluetoothCacheMode cacheMode)
    {
        var result = await characteristic.ReadValueAsync(cacheMode);
        if (result.Status != GattCommunicationStatus.Success)
        {
            return new GattValueInfo(result.Status, []);
        }

        return new GattValueInfo(result.Status, result.Value?.ToArray() ?? []);
    }
}
