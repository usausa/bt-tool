namespace DeviceTool.Bluetooth;

using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

using static DeviceTool.Bluetooth.AddressHelper;

internal static class ScanJsonWriter
{
    public static string FormatAdvertisement(BluetoothLEAdvertisementReceivedEventArgs args, string? name, IEnumerable<DecodedResult> decoded, bool info, BluetoothLEDevice? device, bool gatt, GattInfo? gattInfo) =>
        Format(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "advertisement");
            writer.WriteString("time", args.Timestamp);
            writer.WriteString("address", ToAddressString(args.BluetoothAddress));
            writer.WriteString("addressType", args.BluetoothAddressType.ToString());
            writer.WriteNumber("rssi", args.RawSignalStrengthInDBm);
            writer.WriteString("name", name);
            writer.WriteString("advertisementType", args.AdvertisementType.ToString());
            if (args.TransmitPowerLevelInDBm is { } txPower)
            {
                writer.WriteNumber("txPower", txPower);
            }
            else
            {
                writer.WriteNull("txPower");
            }

            WriteAdvertisement(writer, args.Advertisement);
            WriteDecoded(writer, decoded);
            if (info)
            {
                WriteDevice(writer, device);
            }

            if (gatt)
            {
                WriteGatt(writer, gattInfo);
            }

            writer.WriteEndObject();
        });

    public static string FormatSummary(IEnumerable<ScanSummaryItem> items) =>
        Format(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "summary");
            writer.WriteStartArray("devices");
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("address", ToAddressString(item.Address));
                writer.WriteString("name", item.Name);
                writer.WriteNumber("count", item.Count);
                writer.WriteNumber("averageRssi", item.AverageRssi);
                writer.WriteNumber("minRssi", item.MinRssi);
                writer.WriteNumber("maxRssi", item.MaxRssi);
                writer.WriteString("lastSeen", item.LastSeen);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string Format(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        write(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteAdvertisement(Utf8JsonWriter writer, BluetoothLEAdvertisement advertisement)
    {
        writer.WriteStartArray("serviceUuids");
        foreach (var uuid in advertisement.ServiceUuids)
        {
            writer.WriteStartObject();
            writer.WriteString("uuid", uuid);
            writer.WriteString("name", BluetoothNames.GetServiceName(uuid));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("manufacturerData");
        foreach (var data in advertisement.ManufacturerData)
        {
            writer.WriteStartObject();
            writer.WriteNumber("companyId", data.CompanyId);
            writer.WriteString("company", BluetoothNames.GetCompanyName(data.CompanyId));
            writer.WriteString("data", Convert.ToHexString(data.Data.ToArray()));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("dataSections");
        foreach (var section in advertisement.DataSections)
        {
            writer.WriteStartObject();
            writer.WriteNumber("type", section.DataType);
            writer.WriteString("typeName", BluetoothNames.GetAdTypeName(section.DataType));
            writer.WriteString("data", Convert.ToHexString(section.Data.ToArray()));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteDecoded(Utf8JsonWriter writer, IEnumerable<DecodedResult> decoded)
    {
        writer.WriteStartArray("decoded");
        foreach (var result in decoded)
        {
            writer.WriteStartObject();
            writer.WriteString("name", result.Name);
            writer.WriteStartArray("fields");
            foreach (var field in result.Fields)
            {
                writer.WriteStartObject();
                writer.WriteString("name", field.Name);
                writer.WritePropertyName("value");
                if (field.IsNumber)
                {
                    writer.WriteRawValue(field.Value);
                }
                else
                {
                    writer.WriteStringValue(field.Value);
                }

                if (field.Unit is not null)
                {
                    writer.WriteString("unit", field.Unit);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteDevice(Utf8JsonWriter writer, BluetoothLEDevice? device)
    {
        if (device is null)
        {
            writer.WriteNull("device");
            return;
        }

        var pairing = device.DeviceInformation.Pairing;
        writer.WriteStartObject("device");
        writer.WriteString("deviceId", device.BluetoothDeviceId.Id);
        writer.WriteString("addressType", device.BluetoothAddressType.ToString());
        writer.WriteString("connectionStatus", device.ConnectionStatus.ToString());
        writer.WriteString("protectionLevel", pairing.ProtectionLevel.ToString());
        writer.WriteBoolean("isPaired", pairing.IsPaired);
        writer.WriteBoolean("canPair", pairing.CanPair);
        writer.WriteEndObject();
    }

    private static void WriteGatt(Utf8JsonWriter writer, GattInfo? gatt)
    {
        if (gatt is null)
        {
            writer.WriteNull("gatt");
            return;
        }

        writer.WriteStartObject("gatt");
        writer.WriteString("status", gatt.Status.ToString());
        writer.WriteStartArray("services");
        foreach (var service in gatt.Services)
        {
            writer.WriteStartObject();
            writer.WriteString("uuid", service.Uuid);
            writer.WriteString("name", service.Name);
            writer.WriteString("status", service.Status.ToString());
            writer.WriteStartArray("characteristics");
            foreach (var characteristic in service.Characteristics)
            {
                writer.WriteStartObject();
                writer.WriteString("uuid", characteristic.Uuid);
                writer.WriteString("name", characteristic.Name);
                writer.WriteString("properties", characteristic.Properties.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
