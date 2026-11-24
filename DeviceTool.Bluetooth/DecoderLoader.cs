namespace DeviceTool.Bluetooth;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

internal static class DecoderLoader
{
    private const string ResourceName = "DeviceTool.Bluetooth.Data.decoders.json";

    private const int MaxLength = UInt16.MaxValue;

    private const int MaxDigits = 10;

    private const decimal MaxScale = 1_000_000_000m;

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static bool TryLoad(string? file, out IReadOnlyList<DecoderDefinition> definitions, [NotNullWhen(false)] out string? error)
    {
        try
        {
            using var stream = (file is null) ? OpenResource() : File.OpenRead(file);
            using var document = JsonDocument.Parse(stream, Options);
            definitions = ReadDefinitions(document.RootElement);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or FormatException)
        {
            definitions = [];
            error = exception.Message;
            return false;
        }
    }

    private static Stream OpenResource() =>
        typeof(DecoderLoader).Assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException($"Resource not found. name=[{ResourceName}]");

    private static List<DecoderDefinition> ReadDefinitions(JsonElement root)
    {
        ValidateObject(root, String.Empty, "decoders");
        return ReadArray(root, String.Empty, "decoders", ReadDefinition);
    }

    private static DecoderDefinition ReadDefinition(JsonElement element, string path)
    {
        ValidateObject(element, path, "name", "companyId", "serviceUuid", "prefix", "minLength", "conditions", "fields");
        var name = ReadString(element, path, "name");
        var companyId = ReadOptionalNumber(element, path, "companyId", UInt16.MaxValue);
        var serviceUuid = ReadOptionalUuid(element, path, "serviceUuid");
        if (companyId.HasValue == serviceUuid.HasValue)
        {
            throw Invalid(path, "must have either companyId or serviceUuid");
        }

        var conditions = ReadOptionalArray(element, path, "conditions", ReadCondition);
        return new DecoderDefinition(
            name,
            (ushort?)companyId,
            serviceUuid,
            ReadBytes(element, path, "prefix"),
            ReadMinLength(element, path),
            (conditions is null) ? [] : RequireItems(conditions, Combine(path, "conditions")),
            ReadArray(element, path, "fields", ReadField));
    }

    private static DecoderCondition ReadCondition(JsonElement element, string path)
    {
        if ((element.ValueKind == JsonValueKind.Object) && element.TryGetProperty("namePrefix", out _))
        {
            ValidateObject(element, path, "namePrefix");
            return new NamePrefixCondition(ReadString(element, path, "namePrefix"));
        }

        ValidateObject(element, path, "serviceUuid", "offset", "mask", "values");
        var uuid = ReadOptionalUuid(element, path, "serviceUuid") ?? throw Invalid(path, "must have either namePrefix or serviceUuid");
        var values = ReadArray(element, path, "values", static (x, p) => (byte)ReadNumber(x, p, Byte.MaxValue));
        return new ServiceDataCondition(
            uuid,
            ReadInteger(element, path, "offset", 0, MaxLength),
            ReadByteMask(element, path) ?? Byte.MaxValue,
            [.. RequireItems(values, Combine(path, "values"))]);
    }

    private static DecoderField ReadField(JsonElement element, string path)
    {
        var type = (element.ValueKind == JsonValueKind.Object) ? ReadOptionalString(element, path, "type") : null;
        return (type ?? "integer") switch
        {
            "integer" => ReadIntegerField(element, path),
            "composite" => ReadCompositeField(element, path),
            "uuid" => ReadUuidField(element, path),
            "hex" => ReadHexField(element, path),
            "text" => ReadTextField(element, path),
            _ => throw Invalid(Combine(path, "type"), "must be integer, composite, uuid, hex or text")
        };
    }

    private static IntegerField ReadIntegerField(JsonElement element, string path)
    {
        ValidateObject(element, path, "name", "type", "minLength", "offset", "size", "endian", "signed", "mask", "shift", "scale", "digits", "unit", "invalid", "map");
        var size = ReadOptionalInteger(element, path, "size", 1, 4) ?? 1;
        var max = (1L << (8 * size)) - 1;
        return new IntegerField(
            ReadString(element, path, "name"),
            ReadMinLength(element, path),
            ReadInteger(element, path, "offset", 0, MaxLength),
            size,
            ReadBigEndian(element, path),
            ReadBoolean(element, path, "signed"),
            (uint)(ReadOptionalNumber(element, path, "mask", max) ?? max),
            ReadOptionalInteger(element, path, "shift", 0, (8 * size) - 1) ?? 0,
            ReadScale(element, path),
            ReadDigits(element, path),
            ReadOptionalString(element, path, "unit"),
            (uint?)ReadOptionalNumber(element, path, "invalid", max),
            ReadMap(element, path));
    }

    private static CompositeField ReadCompositeField(JsonElement element, string path)
    {
        ValidateObject(element, path, "name", "type", "minLength", "parts", "sign", "digits", "unit");
        return new CompositeField(
            ReadString(element, path, "name"),
            ReadMinLength(element, path),
            RequireItems(ReadArray(element, path, "parts", ReadPart), Combine(path, "parts")),
            element.TryGetProperty("sign", out var sign) ? ReadSign(sign, Combine(path, "sign")) : null,
            ReadDigits(element, path),
            ReadOptionalString(element, path, "unit"));
    }

    private static CompositePart ReadPart(JsonElement element, string path)
    {
        ValidateObject(element, path, "offset", "mask", "shift", "scale");
        return new CompositePart(
            ReadInteger(element, path, "offset", 0, MaxLength),
            ReadByteMask(element, path) ?? Byte.MaxValue,
            ReadOptionalInteger(element, path, "shift", 0, 7) ?? 0,
            ReadScale(element, path));
    }

    private static CompositeSign ReadSign(JsonElement element, string path)
    {
        ValidateObject(element, path, "offset", "mask", "positive");
        return new CompositeSign(
            ReadInteger(element, path, "offset", 0, MaxLength),
            ReadByteMask(element, path) ?? throw Required(path, "mask"),
            ReadBoolean(element, path, "positive"));
    }

    private static UuidField ReadUuidField(JsonElement element, string path)
    {
        ValidateObject(element, path, "name", "type", "minLength", "offset");
        return new UuidField(
            ReadString(element, path, "name"),
            ReadMinLength(element, path),
            ReadInteger(element, path, "offset", 0, MaxLength));
    }

    private static HexField ReadHexField(JsonElement element, string path)
    {
        ValidateObject(element, path, "name", "type", "minLength", "offset", "size");
        return new HexField(
            ReadString(element, path, "name"),
            ReadMinLength(element, path),
            ReadInteger(element, path, "offset", 0, MaxLength),
            ReadOptionalInteger(element, path, "size", 1, MaxLength));
    }

    private static TextField ReadTextField(JsonElement element, string path)
    {
        ValidateObject(element, path, "name", "type", "minLength", "offset", "size");
        return new TextField(
            ReadString(element, path, "name"),
            ReadMinLength(element, path),
            ReadInteger(element, path, "offset", 0, MaxLength),
            ReadOptionalInteger(element, path, "size", 1, MaxLength));
    }

    private static void ValidateObject(JsonElement element, string path, params ReadOnlySpan<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid((path.Length > 0) ? path : "root", "must be an object");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name))
            {
                throw Invalid(Combine(path, property.Name), "is not supported");
            }
        }
    }

    private static List<T> ReadArray<T>(JsonElement element, string path, string name, Func<JsonElement, string, T> read) =>
        ReadOptionalArray(element, path, name, read) ?? throw Required(path, name);

    private static List<T>? ReadOptionalArray<T>(JsonElement element, string path, string name, Func<JsonElement, string, T> read)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        var arrayPath = Combine(path, name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(arrayPath, "must be an array");
        }

        return [.. value.EnumerateArray().Select((x, i) => read(x, $"{arrayPath}[{i}]"))];
    }

    private static List<T> RequireItems<T>(List<T> items, string path) =>
        (items.Count > 0) ? items : throw Invalid(path, "must not be empty");

    private static string ReadString(JsonElement element, string path, string name) =>
        ReadOptionalString(element, path, name) ?? throw Required(path, name);

    private static string? ReadOptionalString(JsonElement element, string path, string name) =>
        element.TryGetProperty(name, out var value) ? ReadText(value, Combine(path, name)) : null;

    private static string ReadText(JsonElement value, string path)
    {
        var text = (value.ValueKind == JsonValueKind.String) ? value.GetString() : null;
        return String.IsNullOrEmpty(text) ? throw Invalid(path, "must be a non-empty string") : text;
    }

    private static int ReadInteger(JsonElement element, string path, string name, int min, int max) =>
        ReadOptionalInteger(element, path, name, min, max) ?? throw Required(path, name);

    private static int? ReadOptionalInteger(JsonElement element, string path, string name, int min, int max)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if ((value.ValueKind == JsonValueKind.Number) && value.TryGetInt32(out var number) && (number >= min) && (number <= max))
        {
            return number;
        }

        throw Invalid(Combine(path, name), $"must be an integer between {min} and {max}");
    }

    private static int ReadMinLength(JsonElement element, string path) =>
        ReadOptionalInteger(element, path, "minLength", 0, MaxLength) ?? 0;

    private static int ReadDigits(JsonElement element, string path) =>
        ReadOptionalInteger(element, path, "digits", 0, MaxDigits) ?? 0;

    private static byte? ReadByteMask(JsonElement element, string path) =>
        (byte?)ReadOptionalNumber(element, path, "mask", Byte.MaxValue);

    private static long? ReadOptionalNumber(JsonElement element, string path, string name, long max) =>
        element.TryGetProperty(name, out var value) ? ReadNumber(value, Combine(path, name), max) : null;

    private static long ReadNumber(JsonElement value, string path, long max)
    {
        if (TryGetNumber(value, out var number) && (number >= 0) && (number <= max))
        {
            return number;
        }

        throw Invalid(path, $"must be a number between 0x0 and 0x{max:X}");
    }

    private static bool TryGetNumber(JsonElement value, out long number)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out number);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return TryParseNumber(value.GetString(), out number);
        }

        number = 0;
        return false;
    }

    private static bool TryParseNumber(string? text, out long number)
    {
        number = 0;
        if (String.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return (text.Length is > 2 and <= 10) && Int64.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number);
        }

        return Int64.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
    }

    private static bool ReadBoolean(JsonElement element, string path, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(Combine(path, name), "must be true or false")
        };
    }

    private static bool ReadBigEndian(JsonElement element, string path) =>
        (ReadOptionalString(element, path, "endian") ?? "little") switch
        {
            "little" => false,
            "big" => true,
            _ => throw Invalid(Combine(path, "endian"), "must be little or big")
        };

    private static decimal ReadScale(JsonElement element, string path)
    {
        if (!element.TryGetProperty("scale", out var value))
        {
            return 1m;
        }

        if ((value.ValueKind == JsonValueKind.Number) && value.TryGetDecimal(out var scale) && (Math.Abs(scale) <= MaxScale))
        {
            return scale;
        }

        throw Invalid(Combine(path, "scale"), $"must be a number between -{MaxScale} and {MaxScale}");
    }

    private static byte[] ReadBytes(JsonElement element, string path, string name)
    {
        var text = ReadOptionalString(element, path, name);
        if (text is null)
        {
            return [];
        }

        if (((text.Length % 2) != 0) || !text.All(Char.IsAsciiHexDigit))
        {
            throw Invalid(Combine(path, name), "must be a hex string");
        }

        return Convert.FromHexString(text);
    }

    private static Guid? ReadOptionalUuid(JsonElement element, string path, string name)
    {
        var text = ReadOptionalString(element, path, name);
        if (text is null)
        {
            return null;
        }

        return BluetoothNames.TryParseUuid(text, out var uuid) ? uuid : throw Invalid(Combine(path, name), "must be a 16, 32 or 128-bit UUID");
    }

    private static Dictionary<long, string>? ReadMap(JsonElement element, string path)
    {
        if (!element.TryGetProperty("map", out var value))
        {
            return null;
        }

        var mapPath = Combine(path, "map");
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(mapPath, "must be an object");
        }

        var map = new Dictionary<long, string>();
        foreach (var property in value.EnumerateObject())
        {
            var entryPath = Combine(mapPath, property.Name);
            if (!TryParseNumber(property.Name, out var key))
            {
                throw Invalid(entryPath, "must have a number key");
            }

            if (!map.TryAdd(key, ReadText(property.Value, entryPath)))
            {
                throw Invalid(entryPath, "is duplicated");
            }
        }

        return (map.Count > 0) ? map : throw Invalid(mapPath, "must not be empty");
    }

    private static string Combine(string path, string name) => (path.Length > 0) ? $"{path}.{name}" : name;

    private static FormatException Required(string path, string name) => Invalid(Combine(path, name), "is required");

    private static FormatException Invalid(string path, string message) => new($"{path} {message}");
}
