namespace DeviceTool.Bluetooth;

using Windows.Devices.Bluetooth.GenericAttributeProfile;

internal static class DisplayHelper
{
    public static string GetServiceName(GattDeviceService service) => BluetoothNames.GetServiceName(service.Uuid) ?? "(Custom)";

    public static string GetCharacteristicName(GattCharacteristic characteristic)
    {
        var name = BluetoothNames.GetCharacteristicName(characteristic.Uuid);
        if (name is not null)
        {
            return name;
        }

        var description = characteristic.UserDescription;
        return String.IsNullOrWhiteSpace(description) ? "(Custom)" : description;
    }
}
