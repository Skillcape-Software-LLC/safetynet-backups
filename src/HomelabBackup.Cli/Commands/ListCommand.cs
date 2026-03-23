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

        var command = new Command("list", "List remote backup archives")
        {
            Options = { sourceOption }
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption);

            var config = services.GetRequiredService<BackupConfig>();
            var sftp = services.GetRequiredService<ISftpService>();
            var manifestService = services.GetRequiredService<IManifestService>();

            var sourceNames = config.Sources.Select(s => s.Name).ToList();
            if (source is not null)
                sourceNames = sourceNames.Where(s => s.Equals(source, StringComparison.OrdinalIgnoreCase)).ToList();

            if (sourceNames.Count == 0)
            {
                Console.Error.WriteLine(source is not null
                    ? $"Source '{source}' not found."
                    : "No sources configured.");
                return;
            }

            await sftp.ConnectAsync(ct);

            var entries = new List<BackupEntry>();

            foreach (var name in sourceNames)
            {
                var remoteDir = $"{config.Destination.Path}/{name}";
                try
                {
                    var files = await sftp.ListDirectoryAsync(remoteDir, ct);
                    var manifests = files.Where(f => f.Name.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase));

                    foreach (var mf in manifests)
                    {
                        using var stream = new MemoryStream();
                        await sftp.DownloadToStreamAsync(mf.FullPath, stream, ct);
                        stream.Position = 0;
                        var manifest = await manifestService.ReadFromStreamAsync(stream, ct);

                        entries.Add(new BackupEntry(name, manifest.ArchiveFileName, manifest.CreatedUtc, manifest.CompressedBytes, manifest.Files.Count));
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Warning: could not list '{name}': {ex.Message}");
                }
            }

            await sftp.DisconnectAsync();

            if (entries.Count == 0)
            {
                Console.WriteLine("No archives found.");
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
