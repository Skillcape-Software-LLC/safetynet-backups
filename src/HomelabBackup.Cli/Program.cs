using System.CommandLine;
using HomelabBackup.Cli.Commands;
using HomelabBackup.Core;
using HomelabBackup.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var configPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "config/backup.yml";

BackupConfig config;
try
{
    config = ConfigLoader.Load(configPath);
}
catch (ConfigurationException ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 1;
}

var services = new ServiceCollection()
    .AddLogging(builder => builder
        .AddConsole()
        .SetMinimumLevel(LogLevel.Information))
    .AddHomelabBackupCore(config)
    .BuildServiceProvider();

var rootCommand = new RootCommand("HomelabBackup — backup local directories to a remote host via SFTP");
rootCommand.Subcommands.Add(BackupCommand.Create(services));
rootCommand.Subcommands.Add(RestoreCommand.Create(services));
rootCommand.Subcommands.Add(ListCommand.Create(services));
rootCommand.Subcommands.Add(RetentionCommand.Create(services));

return await rootCommand.Parse(args).InvokeAsync();
