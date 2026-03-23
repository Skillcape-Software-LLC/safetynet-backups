using HomelabBackup.Core.Config;
using HomelabBackup.Core.Infrastructure;
using HomelabBackup.Core.Models;
using HomelabBackup.Core.Services;
using Microsoft.Extensions.Logging;

namespace HomelabBackup.Core.Engines;

public sealed class RetentionPolicy : IRetentionPolicy
{
    private readonly ISftpService _sftp;
    private readonly IManifestService _manifest;
    private readonly ILogger<RetentionPolicy> _logger;

    public RetentionPolicy(ISftpService sftp, IManifestService manifest, ILogger<RetentionPolicy> logger)
    {
        _sftp = sftp;
        _manifest = manifest;
        _logger = logger;
    }

    public async Task<RetentionResult> ApplyAsync(
        DestinationConfig destination,
        RetentionConfig retention,
        IReadOnlyList<string> sourceNames,
        bool dryRun,
        CancellationToken ct = default)
    {
        var allDeleted = new List<BackupEntry>();
        var allRetained = new List<BackupEntry>();

        try
        {
            await _sftp.ConnectAsync(ct);

            foreach (var sourceName in sourceNames)
            {
                var remoteDir = $"{destination.Path}/{sourceName}";

                IReadOnlyList<RemoteFileInfo> files;
                try
                {
                    files = await _sftp.ListDirectoryAsync(remoteDir, ct);
                }
                catch
                {
                    _logger.LogWarning("Could not list directory for source '{SourceName}', skipping retention", sourceName);
                    continue;
                }

                var manifestFiles = files
                    .Where(f => f.Name.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Build entries from manifests
                var entries = new List<BackupEntry>();
                foreach (var mf in manifestFiles)
                {
                    try
                    {
                        using var stream = new MemoryStream();
                        await _sftp.DownloadToStreamAsync(mf.FullPath, stream, ct);
                        stream.Position = 0;
                        var manifest = await _manifest.ReadFromStreamAsync(stream, ct);

                        entries.Add(new BackupEntry(
                            sourceName,
                            manifest.ArchiveFileName,
                            manifest.CreatedUtc,
                            manifest.CompressedBytes,
                            manifest.Files.Count));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read manifest {ManifestFile}, skipping", mf.Name);
                    }
                }

                // Sort newest first
                entries = entries.OrderByDescending(e => e.CreatedUtc).ToList();

                var cutoffDate = DateTime.UtcNow.AddDays(-retention.MaxAgeDays);

                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    bool inKeepLastWindow = i < retention.KeepLast;
                    bool withinAge = entry.CreatedUtc >= cutoffDate;

                    if (inKeepLastWindow || withinAge)
                    {
                        allRetained.Add(entry);
                    }
                    else
                    {
                        allDeleted.Add(entry);

                        if (!dryRun)
                        {
                            var remoteZipPath = $"{remoteDir}/{entry.ArchiveFileName}";
                            var remoteManifestPath = $"{remoteDir}/{_manifest.GetManifestFileName(entry.ArchiveFileName)}";

                            try
                            {
                                await _sftp.DeleteAsync(remoteZipPath, ct);
                                await _sftp.DeleteAsync(remoteManifestPath, ct);
                                _logger.LogInformation("Deleted {ArchiveFileName} (age: {Age} days)",
                                    entry.ArchiveFileName, (DateTime.UtcNow - entry.CreatedUtc).Days);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to delete {ArchiveFileName}", entry.ArchiveFileName);
                            }
                        }
                        else
                        {
                            _logger.LogInformation("[DRY RUN] Would delete {ArchiveFileName} (age: {Age} days)",
                                entry.ArchiveFileName, (DateTime.UtcNow - entry.CreatedUtc).Days);
                        }
                    }
                }
            }
        }
        finally
        {
            await _sftp.DisconnectAsync();
        }

        return new RetentionResult(allDeleted, allRetained);
    }
}
