namespace DeviceTool.Bluetooth;

using System.Buffers.Binary;
using System.Globalization;

internal sealed record ManufacturerDataEntry(ushort CompanyId, byte[] Data);

internal sealed record ServiceDataEntry(Guid Uuid, byte[] Data)
{
    public static ServiceDataEntry? Parse(byte dataType, byte[] data) => dataType switch
    {
        0x16 when data.Length >= 2 => new ServiceDataEntry(BluetoothNames.ToUuid(BinaryPrimitives.ReadUInt16LittleEndian(data)), data[2..]),
        0x20 when data.Length >= 4 => new ServiceDataEntry(BluetoothNames.ToUuid(BinaryPrimitives.ReadUInt32LittleEndian(data)), data[4..]),
        0x21 when data.Length >= AdvertisementDecoder.UuidLength => new ServiceDataEntry(ToUuid(data.AsSpan(0, AdvertisementDecoder.UuidLength)), data[AdvertisementDecoder.UuidLength..]),
        _ => null
    };

    private static Guid ToUuid(ReadOnlySpan<byte> littleEndian)
    {
        Span<byte> bigEndian = stackalloc byte[AdvertisementDecoder.UuidLength];
        littleEndian.CopyTo(bigEndian);
        bigEndian.Reverse();
        return new Guid(bigEndian, bigEndian: true);
    }
}

internal sealed record AdvertisementPayload(string? Name, IReadOnlyList<ManufacturerDataEntry> ManufacturerData, IReadOnlyList<ServiceDataEntry> ServiceData);

internal sealed record DecodedField(string Name, string Value, bool IsNumber, string? Unit);

internal sealed record DecodedResult(string Name, IReadOnlyList<DecodedField> Fields);

internal sealed class AdvertisementDecoder
{
    public const int UuidLength = 16;

    private readonly IReadOnlyList<DecoderDefinition> definitions;

    public AdvertisementDecoder(IReadOnlyList<DecoderDefinition> definitions)
    {
        this.definitions = definitions;
    }

    public List<DecodedResult> Decode(AdvertisementPayload payload)
    {
        var results = new List<DecodedResult>();
        foreach (var definition in definitions)
        {
            foreach (var data in SelectTargets(definition, payload))
            {
                if (IsMatch(definition, data, payload))
                {
                    results.Add(new DecodedResult(definition.Name, DecodeFields(definition.Fields, data)));
                }
            }
        }

        return results;
    }

    private static IEnumerable<byte[]> SelectTargets(DecoderDefinition definition, AdvertisementPayload payload) =>
        definition.CompanyId.HasValue
            ? payload.ManufacturerData.Where(x => x.CompanyId == definition.CompanyId.Value).Select(static x => x.Data)
            : payload.ServiceData.Where(x => x.Uuid == definition.ServiceUuid).Select(static x => x.Data);

    private static bool IsMatch(DecoderDefinition definition, byte[] data, AdvertisementPayload payload) =>
        (data.Length >= definition.MinLength) &&
        data.AsSpan().StartsWith(definition.Prefix) &&
        ((definition.Conditions.Count == 0) || definition.Conditions.Any(x => IsSatisfied(x, payload)));

    private static bool IsSatisfied(DecoderCondition condition, AdvertisementPayload payload) => condition switch
    {
        NamePrefixCondition x => payload.Name?.StartsWith(x.Prefix, StringComparison.Ordinal) is true,
        ServiceDataCondition x => payload.ServiceData.Any(entry => (entry.Uuid == x.Uuid) && (x.Offset < entry.Data.Length) && x.Values.Contains((byte)(entry.Data[x.Offset] & x.Mask))),
        _ => false
    };

    private static List<DecodedField> DecodeFields(IReadOnlyList<DecoderField> fields, ReadOnlySpan<byte> data)
    {
        var list = new List<DecodedField>(fields.Count);
        foreach (var field in fields)
        {
            if (data.Length < field.MinLength)
            {
                continue;
            }

            var decoded = field switch
            {
                IntegerField x => DecodeInteger(x, data),
                CompositeField x => DecodeComposite(x, data),
                UuidField x => DecodeUuid(x, data),
                HexField x => DecodeHex(x, data),
                TextField x => DecodeText(x, data),
                _ => null
            };
            if (decoded is not null)
            {
                list.Add(decoded);
            }
        }

        return list;
    }

    private static DecodedField? DecodeInteger(IntegerField field, ReadOnlySpan<byte> data)
    {
        if (data.Length < (field.Offset + field.Size))
        {
            return null;
        }

        var bytes = data.Slice(field.Offset, field.Size);
        var raw = 0u;
        for (var i = 0; i < bytes.Length; i++)
        {
            raw = (raw << 8) | bytes[field.BigEndian ? i : bytes.Length - 1 - i];
        }

        if (raw == field.Invalid)
        {
            return null;
        }

        var masked = raw & field.Mask;
        long value = masked;
        var bits = 8 * field.Size;
        if (field.Signed && (value >= (1L << (bits - 1))))
        {
            value -= 1L << bits;
        }

        value >>= field.Shift;

        if (field.Map is not null)
        {
            if (!field.Map.TryGetValue(value, out var text))
            {
                text = $"0x{(masked >> field.Shift).ToString($"X{field.Size * 2}", CultureInfo.InvariantCulture)}";
            }

            return new DecodedField(field.Name, text, false, field.Unit);
        }

        return new DecodedField(field.Name, FormatNumber(value * field.Scale, field.Digits), true, field.Unit);
    }

    private static DecodedField? DecodeComposite(CompositeField field, ReadOnlySpan<byte> data)
    {
        var value = 0m;
        foreach (var part in field.Parts)
        {
            if (part.Offset >= data.Length)
            {
                return null;
            }

            value += ((data[part.Offset] & part.Mask) >> part.Shift) * part.Scale;
        }

        var sign = field.Sign;
        if (sign is not null)
        {
            if (sign.Offset >= data.Length)
            {
                return null;
            }

            var set = (data[sign.Offset] & sign.Mask) != 0;
            if (set != sign.Positive)
            {
                value = -value;
            }
        }

        return new DecodedField(field.Name, FormatNumber(value, field.Digits), true, field.Unit);
    }

    private static DecodedField? DecodeUuid(UuidField field, ReadOnlySpan<byte> data)
    {
        if (data.Length < (field.Offset + UuidLength))
        {
            return null;
        }

        var hex = Convert.ToHexString(data.Slice(field.Offset, UuidLength));
        return new DecodedField(field.Name, $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}", false, null);
    }

    private static DecodedField? DecodeHex(HexField field, ReadOnlySpan<byte> data) =>
        TrySlice(data, field.Offset, field.Size, out var bytes) ? new DecodedField(field.Name, Convert.ToHexString(bytes), false, null) : null;

    private static DecodedField? DecodeText(TextField field, ReadOnlySpan<byte> data)
    {
        if (!TrySlice(data, field.Offset, field.Size, out var bytes))
        {
            return null;
        }

        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = (bytes[i] is >= 0x20 and <= 0x7E) ? (char)bytes[i] : '?';
        }

        return new DecodedField(field.Name, new string(chars), false, null);
    }

    private static bool TrySlice(ReadOnlySpan<byte> data, int offset, int? size, out ReadOnlySpan<byte> slice)
    {
        var length = size ?? (data.Length - offset);
        if ((length <= 0) || (data.Length < (offset + length)))
        {
            slice = [];
            return false;
        }

        slice = data.Slice(offset, length);
        return true;
    }

    private static string FormatNumber(decimal value, int digits) =>
        value.ToString($"F{digits}", CultureInfo.InvariantCulture);
}
