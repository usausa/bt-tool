using DeviceTool.Bluetooth;

using Smart.CommandLine.Hosting;

if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
{
    await Console.Error.WriteLineAsync("This tool requires Windows 10 version 2004 or later.");
    return 1;
}

var builder = CommandHost.CreateBuilder(args);
builder.ConfigureCommands(commands =>
{
    commands.ConfigureRootCommand(root =>
    {
        root.WithDescription("Bluetooth LE tool");
    });

    commands.AddCommands();
});

var host = builder.Build();
return await host.RunAsync();
