namespace DeviceTool.Bluetooth;

using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

internal static class BluetoothNames
{
    private static readonly Lazy<FrozenDictionary<Guid, string>> ServiceNames = new(static () => LoadUuidNames("service_uuids.json"));

    private static readonly Lazy<FrozenDictionary<Guid, string>> CharacteristicNames = new(static () => LoadUuidNames("characteristic_uuids.json"));

    private static readonly Lazy<FrozenDictionary<ushort, string>> CompanyNames = new(LoadCompanyNames);

    public static string? GetServiceName(Guid uuid) => ServiceNames.Value.GetValueOrDefault(uuid);

    public static string? GetCharacteristicName(Guid uuid) => CharacteristicNames.Value.GetValueOrDefault(uuid);

    public static string? GetCompanyName(ushort companyId) => CompanyNames.Value.GetValueOrDefault(companyId);

    public static string? GetAdTypeName(byte dataType) => dataType switch
    {
        0x01 => "Flags",
        0x02 => "Incomplete List of 16-bit Service or Service Class UUIDs",
        0x03 => "Complete List of 16-bit Service or Service Class UUIDs",
        0x04 => "Incomplete List of 32-bit Service or Service Class UUIDs",
        0x05 => "Complete List of 32-bit Service or Service Class UUIDs",
        0x06 => "Incomplete List of 128-bit Service or Service Class UUIDs",
        0x07 => "Complete List of 128-bit Service or Service Class UUIDs",
        0x08 => "Shortened Local Name",
        0x09 => "Complete Local Name",
        0x0A => "Tx Power Level",
        0x0D => "Class of Device",
        0x0E => "Simple Pairing Hash C-192",
        0x0F => "Simple Pairing Randomizer R-192",
        0x10 => "Security Manager TK Value",
        0x11 => "Security Manager Out of Band Flags",
        0x12 => "Peripheral Connection Interval Range",
        0x14 => "List of 16-bit Service Solicitation UUIDs",
        0x15 => "List of 128-bit Service Solicitation UUIDs",
        0x16 => "Service Data - 16-bit UUID",
        0x17 => "Public Target Address",
        0x18 => "Random Target Address",
        0x19 => "Appearance",
        0x1A => "Advertising Interval",
        0x1B => "LE Bluetooth Device Address",
        0x1C => "LE Role",
        0x1D => "Simple Pairing Hash C-256",
        0x1E => "Simple Pairing Randomizer R-256",
        0x1F => "List of 32-bit Service Solicitation UUIDs",
        0x20 => "Service Data - 32-bit UUID",
        0x21 => "Service Data - 128-bit UUID",
        0x22 => "LE Secure Connections Confirmation Value",
        0x23 => "LE Secure Connections Random Value",
        0x24 => "URI",
        0x25 => "Indoor Positioning",
        0x26 => "Transport Discovery Data",
        0x27 => "LE Supported Features",
        0x28 => "Channel Map Update Indication",
        0x29 => "PB-ADV",
        0x2A => "Mesh Message",
        0x2B => "Mesh Beacon",
        0x2C => "BIGInfo",
        0x2D => "Broadcast_Code",
        0x2E => "Resolvable Set Identifier",
        0x2F => "Advertising Interval - long",
        0x30 => "Broadcast_Name",
        0x31 => "Encrypted Advertising Data",
        0x32 => "Periodic Advertising Response Timing Information",
        0x33 => "IP Link",
        0x34 => "Electronic Shelf Label",
        0x3D => "3D Information Data",
        0xFF => "Manufacturer Specific Data",
        _ => null
    };

    private static FrozenDictionary<Guid, string> LoadUuidNames(string fileName)
    {
        using var document = LoadDocument(fileName);
        var names = new Dictionary<Guid, string>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            if (!String.IsNullOrEmpty(name) && TryParseUuid(entry.GetProperty("uuid").GetString(), out var uuid))
            {
                names.TryAdd(uuid, name);
            }
        }

        return names.ToFrozenDictionary();
    }

    private static FrozenDictionary<ushort, string> LoadCompanyNames()
    {
        using var document = LoadDocument("company_ids.json");
        var names = new Dictionary<ushort, string>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            if (!String.IsNullOrEmpty(name) && entry.GetProperty("code").TryGetUInt16(out var code))
            {
                names.TryAdd(code, name);
            }
        }

        return names.ToFrozenDictionary();
    }

    private static JsonDocument LoadDocument(string fileName)
    {
        using var stream = typeof(BluetoothNames).Assembly.GetManifestResourceStream($"DeviceTool.Bluetooth.Data.{fileName}") ??
            throw new InvalidOperationException($"Resource not found. name=[{fileName}]");
        return JsonDocument.Parse(stream);
    }

    private static bool TryParseUuid(string? value, out Guid uuid)
    {
        if ((value?.Length is 4 or 8) && UInt32.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var shortUuid))
        {
            uuid = new Guid(shortUuid, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB);
            return true;
        }

        return Guid.TryParse(value, out uuid);
    }
}
