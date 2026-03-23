using System.Diagnostics;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Infrastructure;
using HomelabBackup.Core.Models;
using HomelabBackup.Core.Services;
using Microsoft.Extensions.Logging;

namespace HomelabBackup.Core.Engines;

public sealed class RestoreEngine : IRestoreEngine
{
    private readonly ISftpService _sftp;
    private readonly IArchiveService _archive;
    private readonly IManifestService _manifest;
    private readonly ILogger<RestoreEngine> _logger;

    public RestoreEngine(
        ISftpService sftp,
        IArchiveService archive,
        IManifestService manifest,
        ILogger<RestoreEngine> logger)
    {
        _sftp = sftp;
        _archive = archive;
        _manifest = manifest;
        _logger = logger;
    }

    public async Task<BackupResult> RestoreLatestAsync(
        string sourceName,
        DestinationConfig remoteDestination,
        string localDestination,
        CancellationToken ct = default)
    {
        try
        {
            await _sftp.ConnectAsync(ct);
            var remoteDir = $"{remoteDestination.Path}/{sourceName}";

            var files = await _sftp.ListDirectoryAsync(remoteDir, ct);
            var manifests = files.Where(f => f.Name.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastModifiedUtc)
                .ToList();

            if (manifests.Count == 0)
                throw new InvalidOperationException($"No backups found for source '{sourceName}'");

            // Read the latest manifest to get the archive filename
            using var manifestStream = new MemoryStream();
            await _sftp.DownloadToStreamAsync(manifests[0].FullPath, manifestStream, ct);
            manifestStream.Position = 0;
            var manifest = await _manifest.ReadFromStreamAsync(manifestStream, ct);

            await _sftp.DisconnectAsync();

            return await RestoreFileAsync(manifest.ArchiveFileName, remoteDestination, localDestination, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _sftp.DisconnectAsync();
            throw;
        }
    }

    public async Task<BackupResult> RestoreFileAsync(
        string archiveFileName,
        DestinationConfig remoteDestination,
        string localDestination,
        CancellationToken ct = default)
    {
        // Validate destination path
        var canonicalDest = Path.GetFullPath(localDestination);
        if (string.IsNullOrWhiteSpace(canonicalDest))
            throw new ArgumentException("Restore destination path cannot be empty.");

        // Validate archive filename doesn't contain path traversal
        if (archiveFileName.Contains("..") || Path.IsPathRooted(archiveFileName))
            throw new ArgumentException($"Invalid archive filename: {archiveFileName}");

        var stopwatch = Stopwatch.StartNew();
        var tempDir = Path.Combine(Path.GetTempPath(), "homelabbackup", Guid.NewGuid().ToString("N"));
        var tempZipPath = Path.Combine(tempDir, archiveFileName);

        // Infer source name from archive filename (format: sourceName_yyyyMMdd_HHmmss.zip)
        var sourceName = archiveFileName.Split('_')[0];
        var remoteDir = $"{remoteDestination.Path}/{sourceName}";
        var remoteZipPath = $"{remoteDir}/{archiveFileName}";

        try
        {
            Directory.CreateDirectory(tempDir);

            _logger.LogInformation("Restoring {ArchiveFileName} to {Destination}", archiveFileName, localDestination);

            await _sftp.ConnectAsync(ct);
            await _sftp.DownloadAsync(remoteZipPath, tempZipPath, null, ct);
            await _sftp.DisconnectAsync();

            await _archive.ExtractArchiveAsync(tempZipPath, localDestination, ct);

            stopwatch.Stop();

            var zipInfo = new FileInfo(tempZipPath);
            _logger.LogInformation("Restore complete: {ArchiveFileName} → {Destination} ({Duration})",
                archiveFileName, localDestination, stopwatch.Elapsed);

            return new BackupResult(
                Success: true,
                SourceName: sourceName,
                ArchiveFileName: archiveFileName,
                Duration: stopwatch.Elapsed,
                FilesCount: 0,
                UncompressedBytes: 0,
                CompressedBytes: zipInfo.Length,
                VerificationPassed: true,
                RetryCount: 0,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Restore cancelled for {ArchiveFileName}", archiveFileName);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Restore failed for {ArchiveFileName}", archiveFileName);

            return new BackupResult(
                Success: false,
                SourceName: sourceName,
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
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best effort cleanup */ }

            await _sftp.DisconnectAsync();
        }
    }
}
