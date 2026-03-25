using System.CommandLine;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Engines;
using HomelabBackup.Core.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabBackup.Cli.Commands;

public static class RestoreCommand
{
    public static Command Create(IServiceProvider services)
    {
        var sourceOption = new Option<string?>("--source") { Description = "Restore the latest backup for a source" };
        var fileOption = new Option<string?>("--file") { Description = "Restore a specific archive by filename" };
        var allOption = new Option<bool>("--all") { Description = "Restore all sources to their original paths" };
        var destOption = new Option<string?>("--dest") { Description = "Local destination path (defaults to original path from manifest)" };
        var destinationOption = new Option<string?>("--destination") { Description = "Name of the destination to restore from (default: first configured destination)" };

        var command = new Command("restore", "Restore a backup to a local directory")
        {
            Options = { sourceOption, fileOption, allOption, destOption, destinationOption }
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption);
            var file = parseResult.GetValue(fileOption);
            var all = parseResult.GetValue(allOption);
            var dest = parseResult.GetValue(destOption);
            var destinationName = parseResult.GetValue(destinationOption);

            var specifiedCount = (source is not null ? 1 : 0) + (file is not null ? 1 : 0) + (all ? 1 : 0);
            if (specifiedCount == 0)
            {
                Console.Error.WriteLine("Specify one of: --source <name>, --file <archive>, or --all");
                return;
            }
            if (specifiedCount > 1)
            {
                Console.Error.WriteLine("Only one of --source, --file, or --all can be specified.");
                return;
            }

            var config = services.GetRequiredService<BackupConfig>();
            var engine = services.GetRequiredService<IRestoreEngine>();
            var factory = services.GetRequiredService<TransferServiceFactory>();

            if (config.Destinations.Count == 0)
            {
                Console.Error.WriteLine("No destinations configured.");
                return;
            }

            // Resolve the destination to restore from
            DestinationConfig? destination;
            if (destinationName is not null)
            {
                destination = config.Destinations.FirstOrDefault(
                    d => d.Name.Equals(destinationName, StringComparison.OrdinalIgnoreCase));
                if (destination is null)
                {
                    Console.Error.WriteLine($"Destination '{destinationName}' not found.");
                    return;
                }
            }
            else
            {
                // Try to infer from source config
                if (source is not null)
                {
                    var srcConfig = config.Sources.FirstOrDefault(s => s.Name.Equals(source, StringComparison.OrdinalIgnoreCase));
                    destination = srcConfig?.DestinationId.HasValue == true
                        ? config.Destinations.FirstOrDefault(d => d.Id == srcConfig.DestinationId)
                        : config.Destinations.FirstOrDefault();
                }
                else
                {
                    destination = config.Destinations.FirstOrDefault();
                }
            }

            if (destination is null)
            {
                Console.Error.WriteLine("Could not determine destination. Use --destination to specify one.");
                return;
            }

            using var transfer = factory.Create(destination);

            if (all)
            {
                await transfer.ConnectAsync(ct);
                var sourceNames = await transfer.ListSubdirectoriesAsync(destination.Path, ct);
                await transfer.DisconnectAsync();

                if (sourceNames.Count == 0)
                {
                    Console.WriteLine("No archives found.");
                    return;
                }

                Console.WriteLine($"Found {sourceNames.Count} source(s): {string.Join(", ", sourceNames)}\n");

                foreach (var name in sourceNames)
                {
                    string? resolvedDest = dest is not null ? Path.Combine(dest, name) : null;
                    Console.WriteLine($"Restoring '{name}'{(resolvedDest is not null ? $" → {resolvedDest}" : " → original path")}...");

                    var progress = MakeProgress();
                    var result = await engine.RestoreLatestAsync(name, destination, transfer, resolvedDest, progress, ct);
                    Console.WriteLine();
                    PrintResult(result);
                }
            }
            else if (source is not null)
            {
                Console.WriteLine($"Restoring latest backup for '{source}'{(dest is not null ? $" → {dest}" : " → original path")}...");
                var progress = MakeProgress();
                var result = await engine.RestoreLatestAsync(source, destination, transfer, dest, progress, ct);
                Console.WriteLine();
                PrintResult(result);
            }
            else
            {
                if (dest is null)
                {
                    Console.Error.WriteLine("--dest is required when using --file.");
                    return;
                }
                Console.WriteLine($"Restoring {file} → {dest}...");
                var progress = MakeProgress();
                var result = await engine.RestoreFileAsync(file!, destination, transfer, dest, progress, ct);
                Console.WriteLine();
                PrintResult(result);
            }
        });

        return command;
    }

    private static IProgress<long> MakeProgress()
    {
        return new Progress<long>(bytes =>
        {
            var mb = bytes / (1024.0 * 1024);
            Console.Write($"\r  Downloading: {mb:F1} MB downloaded...");
        });
    }

    private static void PrintResult(Core.Models.BackupResult result)
    {
        if (result.Success)
            Console.WriteLine($"  Restore complete in {result.Duration:mm\\:ss}");
        else
            Console.Error.WriteLine($"  Restore FAILED: {result.ErrorMessage}");
    }
}
