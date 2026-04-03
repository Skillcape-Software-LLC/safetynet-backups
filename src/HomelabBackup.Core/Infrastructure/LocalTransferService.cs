using System.Security.Cryptography;
using HomelabBackup.Core.Config;
using Microsoft.Extensions.Logging;

namespace HomelabBackup.Core.Infrastructure;

/// <summary>
/// Transfer service that copies backups to a local directory on the host.
/// </summary>
public sealed class LocalTransferService : ITransferService
{
    private const string MountRoot = "/local-backups";

    private readonly DestinationConfig _config;
    private readonly ILogger<LocalTransferService> _logger;
    private readonly string _basePath;

    public LocalTransferService(DestinationConfig config, ILogger<LocalTransferService> logger)
    {
        _config = config;
        _logger = logger;
        _basePath = Path.Combine(MountRoot, _config.Path.TrimStart('/'));
    }

    /// <summary>
    /// Resolves a path configured by the user (e.g. /slytherin/source/file.zip)
    /// to the real container path under the volume mount (e.g. /local-backups/slytherin/source/file.zip).
    /// </summary>
    private string Resolve(string path) => Path.Combine(MountRoot, path.TrimStart('/'));

    public bool IsConnected => true; // Always "connected" for local

    public Task ConnectAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_basePath);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync() => Task.CompletedTask;

    public async Task UploadAsync(string localPath, string remotePath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var resolvedPath = Resolve(remotePath);
        var destDir = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        await using var source = File.OpenRead(localPath);
        await using var dest = File.Create(resolvedPath);

        var buffer = new byte[81920];
        long totalBytes = 0;
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalBytes += bytesRead;
            progress?.Report(totalBytes);
        }

        await dest.FlushAsync(ct);
        _logger.LogDebug("Copied {LocalPath} → {RemotePath}", localPath, remotePath);
    }

    public async Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var destDir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        await using var source = File.OpenRead(Resolve(remotePath));
        await using var dest = File.Create(localPath);

        var buffer = new byte[81920];
        long totalBytes = 0;
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalBytes += bytesRead;
            progress?.Report(totalBytes);
        }

        await dest.FlushAsync(ct);
        _logger.LogDebug("Copied {RemotePath} → {LocalPath}", remotePath, localPath);
    }

    public async Task DownloadToStreamAsync(string remotePath, Stream destination, CancellationToken ct = default)
    {
        using var source = File.OpenRead(Resolve(remotePath));
        await source.CopyToAsync(destination, ct);
    }

    public Task<IReadOnlyList<RemoteFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var resolvedPath = Resolve(remotePath);

        if (!Directory.Exists(resolvedPath))
            return Task.FromResult<IReadOnlyList<RemoteFileInfo>>([]);

        var files = Directory.GetFiles(resolvedPath)
            .Select(f =>
            {
                var info = new FileInfo(f);
                return new RemoteFileInfo(info.Name, f, info.Length, info.LastWriteTimeUtc);
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<RemoteFileInfo>>(files);
    }

    public Task<IReadOnlyList<string>> ListSubdirectoriesAsync(string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var resolvedPath = Resolve(remotePath);

        if (!Directory.Exists(resolvedPath))
            return Task.FromResult<IReadOnlyList<string>>([]);

        var dirs = Directory.GetDirectories(resolvedPath)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Cast<string>()
            .OrderBy(n => n)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(dirs);
    }

    public Task DeleteAsync(string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var resolvedPath = Resolve(remotePath);
        if (File.Exists(resolvedPath))
        {
            File.Delete(resolvedPath);
            _logger.LogDebug("Deleted local file {Path}", resolvedPath);
        }
        return Task.CompletedTask;
    }

    public Task EnsureDirectoryExistsAsync(string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Resolve(remotePath));
        return Task.CompletedTask;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_basePath);
            var testFile = Path.Combine(_basePath, $"safetynet-test-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(testFile, "safetynet connection test", ct);
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
