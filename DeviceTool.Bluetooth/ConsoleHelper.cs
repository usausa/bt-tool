namespace DeviceTool.Bluetooth;

using System.Text;

internal static class ConsoleHelper
{
    public static bool ConsoleIsOutputRedirected => Console.IsOutputRedirected;

    public static Encoding ConsoleOutputEncoding => Console.OutputEncoding;

    public static ConsoleColor ConsoleForegroundColor => Console.ForegroundColor;

    public static int ConsoleWindowWidth => Console.WindowWidth;

    public static int ConsoleCursorLeft => Console.CursorLeft;

    public static void ConsoleWrite(string value) => Console.Write(value);

    public static void ConsoleWrite(ConsoleColor color, string value)
    {
        var backup = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(value);
        Console.ForegroundColor = backup;
    }

    public static void ConsoleWriteLine() => Console.WriteLine();

    public static void ConsoleWriteLine(string value) => Console.WriteLine(value);

    public static void ConsoleWriteLine(ConsoleColor color, string value)
    {
        var backup = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(value);
        Console.ForegroundColor = backup;
    }

    public static void ConsoleWriteCarriageReturn() => Console.Write('\r');

    public static void ConsoleWriteSpaces(int count) => Console.Write(new string(' ', count));

    public static void ConsoleErrorWriteLine(string value) => Console.Error.WriteLine(value);

    public static void ConsoleWriteHex(ConsoleColor color, ReadOnlySpan<byte> data, string indent)
    {
        for (var start = 0; start < data.Length; start += 16)
        {
            ConsoleWriteLine(color, indent + ToHexString(data.Slice(start, Math.Min(16, data.Length - start))));
        }
    }

    public static void ConsoleWriteHex(ConsoleColor labelColor, string label, ConsoleColor dataColor, ReadOnlySpan<byte> data)
    {
        ConsoleWrite(labelColor, label);
        if (data.IsEmpty)
        {
            ConsoleWriteLine(dataColor, " (Empty)");
            return;
        }

        var length = Math.Min(16, data.Length);
        ConsoleWriteLine(dataColor, ToHexString(data[..length]));
        ConsoleWriteHex(dataColor, data[length..], new string(' ', label.Length));
    }

    public static Task<bool> ConsoleWaitForLineAsync(CancellationToken cancellationToken) =>
        Task.Run(static () => Console.ReadLine() is not null, cancellationToken);

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
