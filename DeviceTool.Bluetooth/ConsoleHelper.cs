namespace DeviceTool.Bluetooth;

internal static class ConsoleHelper
{
    public static void ConsoleWrite(ConsoleColor color, string value)
    {
        var backup = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(value);
        Console.ForegroundColor = backup;
    }

    public static void ConsoleWriteLine(ConsoleColor color, string value)
    {
        var backup = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(value);
        Console.ForegroundColor = backup;
    }

    public static void ConsoleWriteHex(ReadOnlySpan<byte> data, string indent)
    {
        for (var start = 0; start < data.Length; start += 16)
        {
            ConsoleWriteLine(ConsoleColor.DarkGreen, indent + ToHexString(data.Slice(start, Math.Min(16, data.Length - start))));
        }
    }

    public static void ConsoleWriteHex(ConsoleColor color, string label, ReadOnlySpan<byte> data)
    {
        ConsoleWrite(color, label);
        if (data.IsEmpty)
        {
            ConsoleWriteLine(ConsoleColor.DarkGreen, " (Empty)");
            return;
        }

        var length = Math.Min(16, data.Length);
        ConsoleWriteLine(ConsoleColor.DarkGreen, ToHexString(data[..length]));
        ConsoleWriteHex(data[length..], new string(' ', label.Length));
    }

    private static unsafe string ToHexString(ReadOnlySpan<byte> source)
    {
        var hex = stackalloc char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F' };
        var span = stackalloc char[(source.Length * 3) + 1];
        var offset = 0;

        span[offset++] = ' ';
        foreach (var b in source)
        {
            span[offset++] = ' ';
            span[offset++] = hex[b >> 4];
            span[offset++] = hex[b & 0xF];
        }

        return new string(span, 0, offset);
    }
}
