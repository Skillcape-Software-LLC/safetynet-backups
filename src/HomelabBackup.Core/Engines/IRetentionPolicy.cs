using HomelabBackup.Core.Config;

namespace HomelabBackup.Core.Engines;

public interface IRetentionPolicy
{
    Task<RetentionResult> ApplyAsync(
        DestinationConfig destination,
        RetentionConfig retention,
        IReadOnlyList<string> sourceNames,
        bool dryRun,
        CancellationToken ct = default);
}
