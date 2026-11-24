namespace DeviceTool.Bluetooth;

using System.Text;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

using static DeviceTool.Bluetooth.ConsoleHelper;

internal static class DeviceWriter
{
    public static void WriteInfo(BluetoothLEDevice device)
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

    public static void WriteGatt(GattInfo gatt)
    {
        if (gatt.Status == GattCommunicationStatus.Success)
        {
            ConsoleWriteLine(ConsoleColor.Yellow, "GattServices:");
            foreach (var service in gatt.Services)
            {
                ConsoleWrite(Console.ForegroundColor, $"  {service.Uuid}    ");
                ConsoleWriteLine(ConsoleColor.Blue, service.Name);
                if (service.Status == GattCommunicationStatus.Success)
                {
                    foreach (var characteristic in service.Characteristics)
                    {
                        ConsoleWrite(Console.ForegroundColor, $"    {characteristic.Uuid}    ");
                        ConsoleWrite(ConsoleColor.Blue, characteristic.Name);
                        ConsoleWrite(Console.ForegroundColor, " [");
                        ConsoleWrite(ConsoleColor.Green, characteristic.Properties.ToString());
                        ConsoleWriteLine(Console.ForegroundColor, "]");
                        if (characteristic.Value is not null)
                        {
                            WriteValue(characteristic.Value);
                        }
                    }
                }
                else
                {
                    ConsoleWriteLine(ConsoleColor.Red, $"    (Get characteristics failed: {service.Status})");
                }
            }
        }
        else
        {
            ConsoleWrite(ConsoleColor.Yellow, "GattServices:");
            ConsoleWriteLine(ConsoleColor.Red, $" {gatt.Status}");
        }
    }

    private static void WriteValue(GattValueInfo value)
    {
        if (value.Status != GattCommunicationStatus.Success)
        {
            ConsoleWriteLine(ConsoleColor.Red, $"      (Read failed: {value.Status})");
            return;
        }

        if (value.Data.Length == 0)
        {
            ConsoleWriteLine(ConsoleColor.DarkGreen, "      (Empty)");
            return;
        }

        ConsoleWriteHex(value.Data, "    ");
        if (value.Data.All(static x => x is >= 0x20 and <= 0x7E))
        {
            ConsoleWrite(ConsoleColor.Yellow, "      Text:");
            ConsoleWriteLine(Console.ForegroundColor, $" {Encoding.ASCII.GetString(value.Data)}");
        }
    }
}
