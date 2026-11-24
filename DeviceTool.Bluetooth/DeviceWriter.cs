namespace DeviceTool.Bluetooth;

using System.Text;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;

using static DeviceTool.Bluetooth.AddressHelper;
using static DeviceTool.Bluetooth.ConsoleHelper;

internal static class DeviceWriter
{
    public static void WriteDevice(ulong address, string? name)
    {
        ConsoleWrite(Console.ForegroundColor, "[");
        ConsoleWrite(ConsoleColor.DarkCyan, ToAddressString(address));
        ConsoleWrite(Console.ForegroundColor, "] ");
        ConsoleWriteLine(ConsoleColor.Magenta, name ?? "(Unknown)");
    }

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

    public static void WriteAdapter(BluetoothAdapter adapter, Radio? radio)
    {
        ConsoleWrite(ConsoleColor.Yellow, "Address:");
        ConsoleWriteLine(Console.ForegroundColor, $" {ToAddressString(adapter.BluetoothAddress)}");
        ConsoleWrite(ConsoleColor.Yellow, "IsLowEnergySupported:");
        ConsoleWriteLine(Console.ForegroundColor, $" {adapter.IsLowEnergySupported}");
        ConsoleWrite(ConsoleColor.Yellow, "IsCentralRoleSupported:");
        ConsoleWriteLine(Console.ForegroundColor, $" {adapter.IsCentralRoleSupported}");
        ConsoleWrite(ConsoleColor.Yellow, "IsPeripheralRoleSupported:");
        ConsoleWriteLine(Console.ForegroundColor, $" {adapter.IsPeripheralRoleSupported}");
        ConsoleWrite(ConsoleColor.Yellow, "IsExtendedAdvertisingSupported:");
        ConsoleWriteLine(Console.ForegroundColor, $" {adapter.IsExtendedAdvertisingSupported}");
        ConsoleWrite(ConsoleColor.Yellow, "MaxAdvertisementDataLength:");
        ConsoleWriteLine(Console.ForegroundColor, $" {adapter.MaxAdvertisementDataLength}");
        ConsoleWrite(ConsoleColor.Yellow, "RadioState:");
        ConsoleWriteLine(Console.ForegroundColor, $" {radio?.State.ToString() ?? "(Unknown)"}");
    }

    public static void WriteGatt(GattInfo gatt)
    {
        if (gatt.Status == GattCommunicationStatus.Success)
        {
            ConsoleWriteLine(ConsoleColor.Yellow, "GattServices:");
            foreach (var service in gatt.Services)
            {
                WriteService(service.Uuid, service.Name);
                if (service.Status == GattCommunicationStatus.Success)
                {
                    foreach (var characteristic in service.Characteristics)
                    {
                        WriteCharacteristic(characteristic.Uuid, characteristic.Name, characteristic.Properties);
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

    public static void WriteService(Guid uuid, string name)
    {
        ConsoleWrite(Console.ForegroundColor, $"  {uuid}    ");
        ConsoleWriteLine(ConsoleColor.Blue, name);
    }

    public static void WriteCharacteristic(Guid uuid, string name, GattCharacteristicProperties properties)
    {
        ConsoleWrite(Console.ForegroundColor, $"    {uuid}    ");
        ConsoleWrite(ConsoleColor.Blue, name);
        ConsoleWrite(Console.ForegroundColor, " [");
        ConsoleWrite(ConsoleColor.Green, properties.ToString());
        ConsoleWriteLine(Console.ForegroundColor, "]");
    }

    public static void WriteData(byte[] data)
    {
        if (data.Length == 0)
        {
            ConsoleWriteLine(ConsoleColor.DarkGreen, "      (Empty)");
            return;
        }

        ConsoleWriteHex(data, "    ");
        if (data.All(static x => x is >= 0x20 and <= 0x7E))
        {
            ConsoleWrite(ConsoleColor.Yellow, "      Text:");
            ConsoleWriteLine(Console.ForegroundColor, $" {Encoding.ASCII.GetString(data)}");
        }
    }

    public static void WriteStatus(ConsoleColor color, string message)
    {
        ConsoleWriteLine(color, $"      ({message})");
    }

    public static void WriteFailure(string operation, GattCommunicationStatus status, byte? protocolError)
    {
        var detail = ((status == GattCommunicationStatus.ProtocolError) && protocolError.HasValue) ? $"ProtocolError 0x{protocolError.Value:X2}" : status.ToString();
        ConsoleWriteLine(ConsoleColor.Red, $"      ({operation} failed: {detail})");
    }

    private static void WriteValue(GattValueInfo value)
    {
        if (value.Status != GattCommunicationStatus.Success)
        {
            ConsoleWriteLine(ConsoleColor.Red, $"      (Read failed: {value.Status})");
            return;
        }

        WriteData(value.Data);
    }
}
