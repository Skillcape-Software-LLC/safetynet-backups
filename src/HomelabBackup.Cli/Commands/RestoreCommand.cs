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

        var command = new Command("restore", "Restore a backup to a local directory")
        {
            Options = { sourceOption, fileOption, allOption, destOption }
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption);
            var file = parseResult.GetValue(fileOption);
            var all = parseResult.GetValue(allOption);
            var dest = parseResult.GetValue(destOption);

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
            var sftp = services.GetRequiredService<ISftpService>();

            if (all)
            {
                await sftp.ConnectAsync(ct);
                var sourceNames = await sftp.ListSubdirectoriesAsync(config.Destination.Path, ct);
                await sftp.DisconnectAsync();

                if (sourceNames.Count == 0)
                {
                    Console.WriteLine("No archives found on remote.");
                    return;
                }

                Console.WriteLine($"Found {sourceNames.Count} source(s) on remote: {string.Join(", ", sourceNames)}\n");

                foreach (var name in sourceNames)
                {
                    // Resolve destination: --dest /base → /base/sourceName, no --dest → original path from manifest
                    string? resolvedDest = dest is not null ? Path.Combine(dest, name) : null;
                    Console.WriteLine($"Restoring '{name}'{(resolvedDest is not null ? $" → {resolvedDest}" : " → original path")}...");

                    var progress = MakeProgress();
                    var result = await engine.RestoreLatestAsync(name, config.Destination, resolvedDest, progress, ct);
                    Console.WriteLine();
                    PrintResult(result);
                }
            }
            else if (source is not null)
            {
                string? resolvedDest = dest;
                Console.WriteLine($"Restoring latest backup for '{source}'{(resolvedDest is not null ? $" → {resolvedDest}" : " → original path")}...");
                var progress = MakeProgress();
                var result = await engine.RestoreLatestAsync(source, config.Destination, resolvedDest, progress, ct);
                Console.WriteLine();
                PrintResult(result);
            }
            else
            {
                if (dest is null)
                {
                    Console.Error.WriteLine("--dest is required when using --file. Use --source to auto-detect destination from manifest.");
                    return;
                }
                Console.WriteLine($"Restoring {file} → {dest}...");
                var progress = MakeProgress();
                var result = await engine.RestoreFileAsync(file!, config.Destination, dest, progress, ct);
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
