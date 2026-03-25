using System.CommandLine;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Engines;
using HomelabBackup.Core.Infrastructure;
using HomelabBackup.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabBackup.Cli.Commands;

public static class BackupCommand
{
    public static Command Create(IServiceProvider services)
    {
        var sourceOption = new Option<string?>("--source") { Description = "Back up a specific source (default: all)" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Show what would be backed up without transferring" };

        var command = new Command("backup", "Back up configured sources to their configured destinations")
        {
            Options = { sourceOption, dryRunOption }
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption);
            var dryRun = parseResult.GetValue(dryRunOption);

            var config = services.GetRequiredService<BackupConfig>();
            var engine = services.GetRequiredService<IBackupEngine>();
            var factory = services.GetRequiredService<TransferServiceFactory>();

            if (config.Sources.Count == 0)
            {
                Console.Error.WriteLine("No sources configured. Add sources via the Config page.");
                return;
            }

            if (config.Destinations.Count == 0)
            {
                Console.Error.WriteLine("No destinations configured. Add a destination via the Destinations page.");
                return;
            }

            var sources = config.Sources.AsEnumerable();
            if (source is not null)
            {
                sources = sources.Where(s => s.Name.Equals(source, StringComparison.OrdinalIgnoreCase));
                if (!sources.Any())
                {
                    Console.Error.WriteLine($"Source '{source}' not found in configuration.");
                    return;
                }
            }

            var progress = new Progress<BackupProgressEvent>(evt =>
            {
                var phase = evt.Phase switch
                {
                    BackupPhase.Scanning => "Scanning",
                    BackupPhase.Compressing => $"Compressing ({evt.FilesProcessed}/{evt.FilesTotal})",
                    BackupPhase.Transferring => "Transferring",
                    BackupPhase.Verifying => "Verifying",
                    BackupPhase.Complete => "Complete",
                    BackupPhase.Failed => "FAILED",
                    _ => evt.Phase.ToString()
                };
                Console.Write($"\r  [{evt.Source}] {phase}: {evt.CurrentFile,-60}");
            });

            var sourceList = sources.ToList();
            foreach (var s in sourceList)
            {
                var destination = ResolveDestination(s, config);
                if (destination is null)
                {
                    Console.Error.WriteLine($"No destination configured for source '{s.Name}' — skipping.");
                    continue;
                }

                Console.WriteLine($"\nBacking up: {s.Name} ({s.Path}) → {destination.Name}");
                using var transfer = factory.Create(destination);
                var result = await engine.RunAsync(s, destination, transfer, config.Compression, dryRun, progress, ct: ct);
                Console.WriteLine();

                if (result.Success)
                {
                    Console.WriteLine($"  Status: OK | Files: {result.FilesCount} | " +
                        $"Size: {result.CompressedBytes:N0} bytes | " +
                        $"Verified: {result.VerificationPassed} | " +
                        $"Duration: {result.Duration:mm\\:ss}");
                    if (result.RetryCount > 0)
                        Console.WriteLine($"  Retries: {result.RetryCount}");
                }
                else
                {
                    Console.Error.WriteLine($"  FAILED: {result.ErrorMessage}");
                }
            }

            if (!dryRun)
            {
                Console.WriteLine("\nApplying retention policy...");
                var policy = services.GetRequiredService<IRetentionPolicy>();

                var sourcesByDestination = sourceList
                    .GroupBy(s => s.DestinationId ?? config.Destinations.FirstOrDefault()?.Id)
                    .Where(g => g.Key.HasValue);

                foreach (var group in sourcesByDestination)
                {
                    var dest = config.Destinations.FirstOrDefault(d => d.Id == group.Key);
                    if (dest is null) continue;

                    using var transfer = factory.Create(dest);
                    var sourceNames = group.Select(s => s.Name).ToList();
                    var retentionResult = await policy.ApplyAsync(dest, transfer, config.Retention, sourceNames, dryRun: false, ct);
                    Console.WriteLine($"  [{dest.Name}] Retention: {retentionResult.DeletedArchives.Count} deleted, {retentionResult.RetainedArchives.Count} retained");
                }
            }
        });

        return command;
    }

    private static DestinationConfig? ResolveDestination(SourceConfig source, BackupConfig config)
    {
        if (source.DestinationId.HasValue)
            return config.Destinations.FirstOrDefault(d => d.Id == source.DestinationId.Value);
        return config.Destinations.FirstOrDefault();
    }
}
