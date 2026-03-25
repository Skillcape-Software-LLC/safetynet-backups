using System.Diagnostics;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Infrastructure;
using HomelabBackup.Core.Models;
using HomelabBackup.Core.Services;
using Microsoft.Extensions.Logging;

namespace HomelabBackup.Core.Engines;

public sealed class RestoreEngine : IRestoreEngine
{
    private readonly IArchiveService _archive;
    private readonly IManifestService _manifest;
    private readonly ILogger<RestoreEngine> _logger;

    public RestoreEngine(
        IArchiveService archive,
        IManifestService manifest,
        ILogger<RestoreEngine> logger)
    {
        _archive = archive;
        _manifest = manifest;
        _logger = logger;
    }

    public async Task<BackupResult> RestoreLatestAsync(
        string sourceName,
        DestinationConfig remoteDestination,
        ITransferService transfer,
        string? localDestination = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            await transfer.ConnectAsync(ct);
            var remoteDir = $"{remoteDestination.Path}/{sourceName}";

            var files = await transfer.ListDirectoryAsync(remoteDir, ct);
            var manifests = files.Where(f => f.Name.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastModifiedUtc)
                .ToList();

            if (manifests.Count == 0)
                throw new InvalidOperationException($"No backups found for source '{sourceName}'");

            using var manifestStream = new MemoryStream();
            await transfer.DownloadToStreamAsync(manifests[0].FullPath, manifestStream, ct);
            manifestStream.Position = 0;
            var manifest = await _manifest.ReadFromStreamAsync(manifestStream, ct);

            await transfer.DisconnectAsync();

            var destination = localDestination ?? manifest.SourcePath;
            return await RestoreFileAsync(manifest.ArchiveFileName, remoteDestination, transfer, destination, progress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await transfer.DisconnectAsync();
            throw;
        }
    }

    public async Task<BackupResult> RestoreFileAsync(
        string archiveFileName,
        DestinationConfig remoteDestination,
        ITransferService transfer,
        string? localDestination = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        // Validate archive filename doesn't contain path traversal
        if (archiveFileName.Contains("..") || Path.IsPathRooted(archiveFileName))
            throw new ArgumentException($"Invalid archive filename: {archiveFileName}");

        // Infer source name from archive filename (format: sourceName_yyyyMMdd_HHmmss.zip)
        var sourceName = archiveFileName.Split('_')[0];
        var remoteDir = $"{remoteDestination.Path}/{sourceName}";
        var remoteZipPath = $"{remoteDir}/{archiveFileName}";

        // If no destination provided, read manifest to get the original source path
        if (string.IsNullOrWhiteSpace(localDestination))
        {
            try
            {
                await transfer.ConnectAsync(ct);
                var manifestPath = $"{remoteDir}/{_manifest.GetManifestFileName(archiveFileName)}";
                using var manifestStream = new MemoryStream();
                await transfer.DownloadToStreamAsync(manifestPath, manifestStream, ct);
                manifestStream.Position = 0;
                var manifest = await _manifest.ReadFromStreamAsync(manifestStream, ct);
                localDestination = manifest.SourcePath;
                await transfer.DisconnectAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await transfer.DisconnectAsync();
                throw new InvalidOperationException(
                    $"Could not determine restore destination: manifest not found for '{archiveFileName}'. Specify --dest explicitly.", ex);
            }
        }

        var canonicalDest = Path.GetFullPath(localDestination);
        var stopwatch = Stopwatch.StartNew();
        var tempDir = Path.Combine(Path.GetTempPath(), "homelabbackup", Guid.NewGuid().ToString("N"));
        var tempZipPath = Path.Combine(tempDir, archiveFileName);

        try
        {
            Directory.CreateDirectory(tempDir);

            _logger.LogInformation("Restoring {ArchiveFileName} to {Destination}", archiveFileName, canonicalDest);

            await transfer.ConnectAsync(ct);
            await transfer.DownloadAsync(remoteZipPath, tempZipPath, progress, ct);
            await transfer.DisconnectAsync();

            await _archive.ExtractArchiveAsync(tempZipPath, canonicalDest, ct);

            stopwatch.Stop();

            var zipInfo = new FileInfo(tempZipPath);
            _logger.LogInformation("Restore complete: {ArchiveFileName} → {Destination} ({Duration})",
                archiveFileName, canonicalDest, stopwatch.Elapsed);

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

            await transfer.DisconnectAsync();
        }
    }
}
