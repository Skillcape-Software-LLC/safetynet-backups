using System.CommandLine;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Engines;
using HomelabBackup.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabBackup.Cli.Commands;

public static class BackupCommand
{
    public static Command Create(IServiceProvider services)
    {
        var sourceOption = new Option<string?>("--source") { Description = "Back up a specific source (default: all)" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Show what would be backed up without transferring" };

        var command = new Command("backup", "Back up configured sources to remote host")
        {
            Options = { sourceOption, dryRunOption }
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption);
            var dryRun = parseResult.GetValue(dryRunOption);

            var config = services.GetRequiredService<BackupConfig>();
            var engine = services.GetRequiredService<IBackupEngine>();

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

            foreach (var s in sources.ToList())
            {
                Console.WriteLine($"\nBacking up: {s.Name} ({s.Path})");
                var result = await engine.RunAsync(s, config.Destination, config.Compression, dryRun, progress, ct);
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
        });

        return command;
    }
}
