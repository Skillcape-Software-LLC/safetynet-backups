using System.CommandLine;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Infrastructure;
using HomelabBackup.Core.Models;
using HomelabBackup.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabBackup.Cli.Commands;

public static class ListCommand
{
    public static Command Create(IServiceProvider services)
    {
        var sourceOption = new Option<string?>("--source") { Description = "Filter by source name" };
        var destinationOption = new Option<string?>("--destination") { Description = "Destination name to list from (default: all destinations)" };

        var command = new Command("list", "List backup archives")
        {
            Options = { sourceOption, destinationOption }
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption);
            var destinationName = parseResult.GetValue(destinationOption);

            var config = services.GetRequiredService<BackupConfig>();
            var factory = services.GetRequiredService<TransferServiceFactory>();
            var manifestService = services.GetRequiredService<IManifestService>();

            if (config.Destinations.Count == 0)
            {
                Console.Error.WriteLine("No destinations configured.");
                return;
            }

            var destinations = destinationName is not null
                ? config.Destinations.Where(d => d.Name.Equals(destinationName, StringComparison.OrdinalIgnoreCase)).ToList()
                : config.Destinations;

            var entries = new List<BackupEntry>();

            foreach (var dest in destinations)
            {
                using var transfer = factory.Create(dest);
                await transfer.ConnectAsync(ct);

                var sourceNames = await transfer.ListSubdirectoriesAsync(dest.Path, ct);

                if (source is not null)
                    sourceNames = sourceNames.Where(s => s.Equals(source, StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var name in sourceNames)
                {
                    var remoteDir = $"{dest.Path}/{name}";
                    try
                    {
                        var files = await transfer.ListDirectoryAsync(remoteDir, ct);
                        var manifests = files.Where(f => f.Name.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase));

                        foreach (var mf in manifests)
                        {
                            using var stream = new MemoryStream();
                            await transfer.DownloadToStreamAsync(mf.FullPath, stream, ct);
                            stream.Position = 0;
                            var manifest = await manifestService.ReadFromStreamAsync(stream, ct);
                            entries.Add(new BackupEntry(name, manifest.ArchiveFileName, manifest.CreatedUtc,
                                manifest.CompressedBytes, manifest.Files.Count, DestinationId: dest.Id));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  Warning: could not list '{name}' on '{dest.Name}': {ex.Message}");
                    }
                }

                await transfer.DisconnectAsync();
            }

            if (entries.Count == 0)
            {
                Console.WriteLine(source is not null
                    ? $"No archives found for source '{source}'."
                    : "No archives found. Run a backup first.");
                return;
            }

            Console.WriteLine($"{"Source",-15} {"Archive",-40} {"Created (UTC)",-22} {"Size",12} {"Files",6}");
            Console.WriteLine(new string('-', 101));

            foreach (var entry in entries.OrderByDescending(e => e.CreatedUtc))
            {
                Console.WriteLine($"{entry.SourceName,-15} {entry.ArchiveFileName,-40} {entry.CreatedUtc:yyyy-MM-dd HH:mm:ss,-22} {FormatBytes(entry.CompressedBytes),12} {entry.FileCount,6}");
            }

            Console.WriteLine($"\nTotal: {entries.Count} archive(s)");
        });

        return command;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}
