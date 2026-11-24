namespace DeviceTool.Bluetooth;

using System.Globalization;

internal static class AddressHelper
{
    public static bool TryParseAddress(string value, out ulong address)
    {
        var hex = value.Replace(":", String.Empty, StringComparison.Ordinal).Replace("-", String.Empty, StringComparison.Ordinal);
        if ((hex.Length == 12) && UInt64.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address))
        {
            return true;
        }

        address = 0;
        return false;
    }

    public static unsafe string ToAddressString(ulong address)
    {
        var hex = stackalloc char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F' };
        var span = stackalloc char[17];
        var offset = 0;

        span[offset++] = hex[(address >> 44) & 0xF];
        span[offset++] = hex[(address >> 40) & 0xF];
        span[offset++] = ':';
        span[offset++] = hex[(address >> 36) & 0xF];
        span[offset++] = hex[(address >> 32) & 0xF];
        span[offset++] = ':';
        span[offset++] = hex[(address >> 28) & 0xF];
        span[offset++] = hex[(address >> 24) & 0xF];
        span[offset++] = ':';
        span[offset++] = hex[(address >> 20) & 0xF];
        span[offset++] = hex[(address >> 16) & 0xF];
        span[offset++] = ':';
        span[offset++] = hex[(address >> 12) & 0xF];
        span[offset++] = hex[(address >> 8) & 0xF];
        span[offset++] = ':';
        span[offset++] = hex[(address >> 4) & 0xF];
        span[offset++] = hex[address & 0xF];

        return new string(span, 0, offset);
    }
}
