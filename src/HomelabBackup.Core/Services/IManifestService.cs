using HomelabBackup.Core.Infrastructure;
using HomelabBackup.Core.Models;

namespace HomelabBackup.Core.Services;

public interface IManifestService
{
    BackupManifest Create(string sourceName, string sourcePath, string archiveFileName, ArchiveResult archiveResult);
    Task WriteAsync(BackupManifest manifest, string localPath, CancellationToken ct = default);
    Task<BackupManifest> ReadAsync(string localPath, CancellationToken ct = default);
    Task<BackupManifest> ReadFromStreamAsync(Stream stream, CancellationToken ct = default);
    string GetManifestFileName(string archiveFileName);
}
