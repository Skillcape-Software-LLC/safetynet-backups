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

    private readonly IArchiveService _archive;
    private readonly IManifestService _manifest;
    private readonly ILogger<BackupEngine> _logger;

    public BackupEngine(
        IArchiveService archive,
        IManifestService manifest,
        ILogger<BackupEngine> logger)
    {
        _archive = archive;
        _manifest = manifest;
        _logger = logger;
    }

    public async Task<BackupResult> RunAsync(
        SourceConfig source,
        DestinationConfig destination,
        ITransferService transfer,
        string compression,
        bool dryRun,
        IProgress<BackupProgressEvent>? progress = null,
        SemaphoreSlim? compressionSemaphore = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var archiveFileName = $"{source.Name}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip";
        var tempDir = Path.Combine(Path.GetTempPath(), "homelabbackup", Guid.NewGuid().ToString("N"));
        var tempZipPath = Path.Combine(tempDir, archiveFileName);

        try
        {
            // Sanity-check destination reachability before spending time compressing
            await transfer.ConnectAsync(ct);
            await transfer.DisconnectAsync();

            // Scan + Compress (serialized via semaphore when running in parallel)
            var compressionLevel = ArchiveService.ParseCompressionLevel(compression);

            if (compressionSemaphore is not null)
                await compressionSemaphore.WaitAsync(ct);

            ArchiveResult archiveResult;
            try
            {
                archiveResult = await _archive.CreateArchiveAsync(
                    source.Path, tempZipPath, compressionLevel, source.Exclude,
                    progress, source.Name, ct);
            }
            finally
            {
                compressionSemaphore?.Release();
            }

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

            await transfer.ConnectAsync(ct);
            await transfer.EnsureDirectoryExistsAsync(remoteDir, ct);

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

                await transfer.UploadAsync(tempZipPath, remoteZipPath, null, ct);

                // Verify
                progress?.Report(new BackupProgressEvent(
                    source.Name, archiveFileName, archiveResult.Files.Count, archiveResult.Files.Count,
                    archiveResult.CompressedBytes, BackupPhase.Verifying));

                var remoteHash = await ComputeRemoteFileHashAsync(transfer, remoteZipPath, ct);
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
                    try { await transfer.DeleteAsync(remoteZipPath, ct); }
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
            await transfer.UploadAsync(tempManifestPath, remoteManifestPath, null, ct);

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

            await transfer.DisconnectAsync();
        }
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> ComputeRemoteFileHashAsync(ITransferService transfer, string remotePath, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        using var hashStream = new CryptoStream(Stream.Null, sha256, CryptoStreamMode.Write);
        await transfer.DownloadToStreamAsync(remotePath, hashStream, ct);
        await hashStream.FlushFinalBlockAsync(ct);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }
}
