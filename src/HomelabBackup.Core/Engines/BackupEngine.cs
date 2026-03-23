using System.Diagnostics;
using System.Security.Cryptography;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Infrastructure;
using HomelabBackup.Core.Models;
using HomelabBackup.Core.Services;
using Microsoft.Extensions.Logging;

namespace HomelabBackup.Core.Engines;

public sealed class BackupEngine : IBackupEngine
{
    private const int MaxRetries = 2;

    private readonly ISftpService _sftp;
    private readonly IArchiveService _archive;
    private readonly IManifestService _manifest;
    private readonly ILogger<BackupEngine> _logger;

    public BackupEngine(
        ISftpService sftp,
        IArchiveService archive,
        IManifestService manifest,
        ILogger<BackupEngine> logger)
    {
        _sftp = sftp;
        _archive = archive;
        _manifest = manifest;
        _logger = logger;
    }

    public async Task<BackupResult> RunAsync(
        SourceConfig source,
        DestinationConfig destination,
        string compression,
        bool dryRun,
        IProgress<BackupProgressEvent>? progress = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var archiveFileName = $"{source.Name}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip";
        var tempDir = Path.Combine(Path.GetTempPath(), "homelabbackup", Guid.NewGuid().ToString("N"));
        var tempZipPath = Path.Combine(tempDir, archiveFileName);

        try
        {
            // Scan + Compress
            var compressionLevel = ArchiveService.ParseCompressionLevel(compression);
            var archiveResult = await _archive.CreateArchiveAsync(
                source.Path, tempZipPath, compressionLevel, source.Exclude,
                progress, source.Name, ct);

            if (dryRun)
            {
                stopwatch.Stop();
                _logger.LogInformation("[DRY RUN] Would backup {SourceName}: {FileCount} files, {Size:N0} bytes",
                    source.Name, archiveResult.Files.Count, archiveResult.UncompressedBytes);

                return new BackupResult(
                    Success: true,
                    SourceName: source.Name,
                    ArchiveFileName: archiveFileName,
                    Duration: stopwatch.Elapsed,
                    FilesCount: archiveResult.Files.Count,
                    UncompressedBytes: archiveResult.UncompressedBytes,
                    CompressedBytes: archiveResult.CompressedBytes,
                    VerificationPassed: true,
                    RetryCount: 0,
                    ErrorMessage: null);
            }

            // Compute local SHA256 before upload
            var localHash = await ComputeFileHashAsync(tempZipPath, ct);
            _logger.LogDebug("Local archive hash: {Hash}", localHash);

            // Create manifest
            var manifest = _manifest.Create(source.Name, source.Path, archiveFileName, archiveResult);
            var manifestFileName = _manifest.GetManifestFileName(archiveFileName);
            var tempManifestPath = Path.Combine(tempDir, manifestFileName);
            await _manifest.WriteAsync(manifest, tempManifestPath, ct);

            // Transfer with integrity verification and retry
            var remoteDir = $"{destination.Path}/{source.Name}";
            var remoteZipPath = $"{remoteDir}/{archiveFileName}";
            var remoteManifestPath = $"{remoteDir}/{manifestFileName}";

            await _sftp.ConnectAsync(ct);
            await _sftp.EnsureDirectoryExistsAsync(remoteDir, ct);

            int retryCount = 0;
            bool verified = false;

            while (!verified && retryCount <= MaxRetries)
            {
                if (retryCount > 0)
                {
                    _logger.LogWarning("Retry {Attempt}/{MaxRetries} for {ArchiveFileName} — re-uploading",
                        retryCount, MaxRetries, archiveFileName);
                }

                // Upload
                progress?.Report(new BackupProgressEvent(
                    source.Name, archiveFileName, archiveResult.Files.Count, archiveResult.Files.Count,
                    archiveResult.CompressedBytes, BackupPhase.Transferring));

                await _sftp.UploadAsync(tempZipPath, remoteZipPath, null, ct);

                // Verify
                progress?.Report(new BackupProgressEvent(
                    source.Name, archiveFileName, archiveResult.Files.Count, archiveResult.Files.Count,
                    archiveResult.CompressedBytes, BackupPhase.Verifying));

                var remoteHash = await ComputeRemoteFileHashAsync(remoteZipPath, ct);
                _logger.LogDebug("Remote archive hash: {Hash}", remoteHash);

                if (string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
                {
                    verified = true;
                    _logger.LogInformation("Integrity verification passed for {ArchiveFileName}", archiveFileName);
                }
                else
                {
                    _logger.LogWarning("Integrity verification FAILED for {ArchiveFileName}: local={LocalHash}, remote={RemoteHash}",
                        archiveFileName, localHash, remoteHash);

                    // Delete corrupted remote file
                    try { await _sftp.DeleteAsync(remoteZipPath, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete corrupted remote file"); }

                    retryCount++;
                }
            }

            if (!verified)
            {
                stopwatch.Stop();
                var errorMsg = $"Integrity verification failed after {MaxRetries} retries for {archiveFileName}";
                _logger.LogError("{Error}", errorMsg);

                progress?.Report(new BackupProgressEvent(
                    source.Name, archiveFileName, 0, 0, 0, BackupPhase.Failed));

                return new BackupResult(
                    Success: false,
                    SourceName: source.Name,
                    ArchiveFileName: archiveFileName,
                    Duration: stopwatch.Elapsed,
                    FilesCount: archiveResult.Files.Count,
                    UncompressedBytes: archiveResult.UncompressedBytes,
                    CompressedBytes: archiveResult.CompressedBytes,
                    VerificationPassed: false,
                    RetryCount: retryCount,
                    ErrorMessage: errorMsg);
            }

            // Upload manifest (small file, no retry needed)
            await _sftp.UploadAsync(tempManifestPath, remoteManifestPath, null, ct);

            stopwatch.Stop();

            progress?.Report(new BackupProgressEvent(
                source.Name, archiveFileName, archiveResult.Files.Count, archiveResult.Files.Count,
                archiveResult.CompressedBytes, BackupPhase.Complete));

            _logger.LogInformation("Backup complete: {SourceName} → {ArchiveFileName} ({CompressedSize:N0} bytes, {Duration})",
                source.Name, archiveFileName, archiveResult.CompressedBytes, stopwatch.Elapsed);

            return new BackupResult(
                Success: true,
                SourceName: source.Name,
                ArchiveFileName: archiveFileName,
                Duration: stopwatch.Elapsed,
                FilesCount: archiveResult.Files.Count,
                UncompressedBytes: archiveResult.UncompressedBytes,
                CompressedBytes: archiveResult.CompressedBytes,
                VerificationPassed: true,
                RetryCount: retryCount,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Backup cancelled for {SourceName}", source.Name);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Backup failed for {SourceName}", source.Name);

            progress?.Report(new BackupProgressEvent(
                source.Name, "", 0, 0, 0, BackupPhase.Failed));

            return new BackupResult(
                Success: false,
                SourceName: source.Name,
                ArchiveFileName: archiveFileName,
                Duration: stopwatch.Elapsed,
                FilesCount: 0,
                UncompressedBytes: 0,
                CompressedBytes: 0,
                VerificationPassed: false,
                RetryCount: 0,
                ErrorMessage: ex.Message);
        }
        finally
        {
            // Cleanup temp files
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temp directory {TempDir}", tempDir);
            }

            await _sftp.DisconnectAsync();
        }
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<string> ComputeRemoteFileHashAsync(string remotePath, CancellationToken ct)
    {
        using var memoryStream = new MemoryStream();
        await _sftp.DownloadToStreamAsync(remotePath, memoryStream, ct);
        memoryStream.Position = 0;
        var hash = await SHA256.HashDataAsync(memoryStream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
