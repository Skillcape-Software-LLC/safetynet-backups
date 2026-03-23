using System.CommandLine;
using HomelabBackup.Cli.Commands;
using HomelabBackup.Core;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "data/safetynet.db";
var repository = new SqliteConfigRepository(dbPath);
repository.EnsureSchema();

// YAML migration: if a backup.yml exists and the DB is empty, import it
var legacyConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "config/backup.yml";
if (File.Exists(legacyConfigPath) && repository.IsEmpty())
{
    try
    {
        var legacyConfig = ConfigLoader.Load(legacyConfigPath);
        repository.Save(legacyConfig);
        Console.WriteLine($"[SafetyNet] Migrated config from {legacyConfigPath} to {dbPath}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SafetyNet] YAML migration failed: {ex.Message}");
    }
}

BackupConfig config;
try
{
    config = repository.Load();
    ConfigLoader.Validate(config);
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
    .AddSingleton<IConfigRepository>(repository)
    .AddHomelabBackupCore(config)
    .BuildServiceProvider();

var rootCommand = new RootCommand("HomelabBackup — backup local directories to a remote host via SFTP");
rootCommand.Subcommands.Add(BackupCommand.Create(services));
rootCommand.Subcommands.Add(RestoreCommand.Create(services));
rootCommand.Subcommands.Add(ListCommand.Create(services));
rootCommand.Subcommands.Add(RetentionCommand.Create(services));

return await rootCommand.Parse(args).InvokeAsync();
