using BleScan;

using Smart.CommandLine.Hosting;

var builder = CommandHost.CreateBuilder(args);
builder.ConfigureCommands(commands =>
{
    commands.ConfigureRootCommand(root =>
    {
        root.WithDescription("BLE scan tool").UseHandler<RootCommandHandler>();
    });
});

var host = builder.Build();
return await host.RunAsync();
