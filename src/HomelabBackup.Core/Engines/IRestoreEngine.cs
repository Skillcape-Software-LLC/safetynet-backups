using HomelabBackup.Core.Config;
using HomelabBackup.Core.Models;

namespace HomelabBackup.Core.Engines;

public interface IRestoreEngine
{
    /// <summary>
    /// Restores the latest backup for the given source. If localDestination is null,
    /// the original source path stored in the manifest is used as the destination.
    /// </summary>
    Task<BackupResult> RestoreLatestAsync(
        string sourceName,
        DestinationConfig remoteDestination,
        string? localDestination = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Restores a specific archive by filename. If localDestination is null,
    /// the original source path stored in the manifest is used as the destination.
    /// </summary>
    Task<BackupResult> RestoreFileAsync(
        string archiveFileName,
        DestinationConfig remoteDestination,
        string? localDestination = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default);
}
