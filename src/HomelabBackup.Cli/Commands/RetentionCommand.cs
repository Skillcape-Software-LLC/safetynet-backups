using System.CommandLine;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Engines;
using HomelabBackup.Core.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabBackup.Cli.Commands;

public static class RetentionCommand
{
    public static Command Create(IServiceProvider services)
    {
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would be deleted without actually deleting",
            DefaultValueFactory = _ => true
        };

        var command = new Command("retention", "Apply retention policy to archives")
        {
            Options = { dryRunOption }
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var dryRun = parseResult.GetValue(dryRunOption);

            var config = services.GetRequiredService<BackupConfig>();
            var policy = services.GetRequiredService<IRetentionPolicy>();
            var factory = services.GetRequiredService<TransferServiceFactory>();

            if (config.Destinations.Count == 0)
            {
                Console.Error.WriteLine("No destinations configured.");
                return;
            }

            Console.WriteLine($"Retention policy: keep_last={config.Retention.KeepLast}, max_age_days={config.Retention.MaxAgeDays}");
            if (dryRun) Console.WriteLine("[DRY RUN MODE]");
            Console.WriteLine();

            // Apply retention per destination
            var sourcesByDestination = config.Sources
                .GroupBy(s => s.DestinationId ?? config.Destinations.FirstOrDefault()?.Id)
                .Where(g => g.Key.HasValue);

            foreach (var group in sourcesByDestination)
            {
                var dest = config.Destinations.FirstOrDefault(d => d.Id == group.Key);
                if (dest is null) continue;

                Console.WriteLine($"Destination: {dest.Name}");
                using var transfer = factory.Create(dest);
                var sourceNames = group.Select(s => s.Name).ToList();
                var result = await policy.ApplyAsync(dest, transfer, config.Retention, sourceNames, dryRun, ct);

                if (result.DeletedArchives.Count > 0)
                {
                    Console.WriteLine("  Archives to delete:");
                    foreach (var entry in result.DeletedArchives)
                    {
                        var age = (DateTime.UtcNow - entry.CreatedUtc).Days;
                        Console.WriteLine($"    {entry.ArchiveFileName} (age: {age} days)");
                    }
                }
                else
                {
                    Console.WriteLine("  No archives to delete.");
                }

                Console.WriteLine($"  Retained: {result.RetainedArchives.Count} | Deleted: {result.DeletedArchives.Count}");
                Console.WriteLine();
            }
        });

        return command;
    }
}
