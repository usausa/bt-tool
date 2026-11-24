namespace DeviceTool.Bluetooth;

internal sealed record DecoderDefinition(string Name, ushort? CompanyId, Guid? ServiceUuid, byte[] Prefix, int MinLength, IReadOnlyList<DecoderCondition> Conditions, IReadOnlyList<DecoderField> Fields);

internal abstract record DecoderCondition;

internal sealed record NamePrefixCondition(string Prefix) : DecoderCondition;

internal sealed record ServiceDataCondition(Guid Uuid, int Offset, byte Mask, byte[] Values) : DecoderCondition;

internal abstract record DecoderField(string Name, int MinLength);

internal sealed record IntegerField(string Name, int MinLength, int Offset, int Size, bool BigEndian, bool Signed, uint Mask, int Shift, decimal Scale, int Digits, string? Unit, uint? Invalid, IReadOnlyDictionary<long, string>? Map) : DecoderField(Name, MinLength);

internal sealed record CompositeField(string Name, int MinLength, IReadOnlyList<CompositePart> Parts, CompositeSign? Sign, int Digits, string? Unit) : DecoderField(Name, MinLength);

internal sealed record CompositePart(int Offset, byte Mask, int Shift, decimal Scale);

internal sealed record CompositeSign(int Offset, byte Mask, bool Positive);

internal sealed record UuidField(string Name, int MinLength, int Offset) : DecoderField(Name, MinLength);

internal sealed record HexField(string Name, int MinLength, int Offset, int? Size) : DecoderField(Name, MinLength);

internal sealed record TextField(string Name, int MinLength, int Offset, int? Size) : DecoderField(Name, MinLength);
