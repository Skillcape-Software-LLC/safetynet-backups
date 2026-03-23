using System.IO.Compression;
using HomelabBackup.Core.Models;

namespace HomelabBackup.Core.Infrastructure;

public interface IArchiveService
{
    Task<ArchiveResult> CreateArchiveAsync(
        string sourcePath,
        string outputZipPath,
        CompressionLevel level,
        IReadOnlyList<string> excludePatterns,
        IProgress<BackupProgressEvent>? progress = null,
        string? sourceName = null,
        CancellationToken ct = default);

    Task ExtractArchiveAsync(string zipPath, string destinationPath, CancellationToken ct = default);
}
