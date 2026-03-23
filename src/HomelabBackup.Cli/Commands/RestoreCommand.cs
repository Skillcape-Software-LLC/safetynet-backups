using System.CommandLine;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Engines;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabBackup.Cli.Commands;

public static class RestoreCommand
{
    public static Command Create(IServiceProvider services)
    {
        var sourceOption = new Option<string?>("--source") { Description = "Restore the latest backup for a source" };
        var fileOption = new Option<string?>("--file") { Description = "Restore a specific archive by filename" };
        var destOption = new Option<string>("--dest") { Description = "Local destination path", Required = true };

        var command = new Command("restore", "Restore a backup to a local directory")
        {
            Options = { sourceOption, fileOption, destOption }
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption);
            var file = parseResult.GetValue(fileOption);
            var dest = parseResult.GetValue(destOption)!;

            if (source is null && file is null)
            {
                Console.Error.WriteLine("Either --source or --file must be specified.");
                return;
            }
            if (source is not null && file is not null)
            {
                Console.Error.WriteLine("Only one of --source or --file can be specified.");
                return;
            }

            var config = services.GetRequiredService<BackupConfig>();
            var engine = services.GetRequiredService<IRestoreEngine>();

            if (source is not null)
            {
                Console.WriteLine($"Restoring latest backup for '{source}' to {dest}...");
                var result = await engine.RestoreLatestAsync(source, config.Destination, dest, ct);
                PrintResult(result);
            }
            else
            {
                Console.WriteLine($"Restoring {file} to {dest}...");
                var result = await engine.RestoreFileAsync(file!, config.Destination, dest, ct);
                PrintResult(result);
            }
        });

        return command;
    }

    private static void PrintResult(Core.Models.BackupResult result)
    {
        if (result.Success)
            Console.WriteLine($"  Restore complete in {result.Duration:mm\\:ss}");
        else
            Console.Error.WriteLine($"  Restore FAILED: {result.ErrorMessage}");
    }
}
