using System.CommandLine;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Engines;
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

        var command = new Command("retention", "Apply retention policy to remote archives")
        {
            Options = { dryRunOption }
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var dryRun = parseResult.GetValue(dryRunOption);

            var config = services.GetRequiredService<BackupConfig>();
            var policy = services.GetRequiredService<IRetentionPolicy>();

            var sourceNames = config.Sources.Select(s => s.Name).ToList();

            Console.WriteLine($"Retention policy: keep_last={config.Retention.KeepLast}, max_age_days={config.Retention.MaxAgeDays}");
            if (dryRun) Console.WriteLine("[DRY RUN MODE]");
            Console.WriteLine();

            var result = await policy.ApplyAsync(config.Destination, config.Retention, sourceNames, dryRun, ct);

            if (result.DeletedArchives.Count > 0)
            {
                Console.WriteLine("Archives to delete:");
                foreach (var entry in result.DeletedArchives)
                {
                    var age = (DateTime.UtcNow - entry.CreatedUtc).Days;
                    Console.WriteLine($"  {entry.ArchiveFileName} (age: {age} days)");
                }
            }
            else
            {
                Console.WriteLine("No archives to delete.");
            }

            Console.WriteLine($"\nRetained: {result.RetainedArchives.Count} | Deleted: {result.DeletedArchives.Count}");
        });

        return command;
    }
}
