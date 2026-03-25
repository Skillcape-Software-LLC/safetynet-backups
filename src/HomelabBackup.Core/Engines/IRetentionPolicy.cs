using HomelabBackup.Core.Config;
using HomelabBackup.Core.Infrastructure;

namespace HomelabBackup.Core.Engines;

public interface IRetentionPolicy
{
    Task<RetentionResult> ApplyAsync(
        DestinationConfig destination,
        ITransferService transfer,
        RetentionConfig retention,
        IReadOnlyList<string> sourceNames,
        bool dryRun,
        CancellationToken ct = default);
}
