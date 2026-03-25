using HomelabBackup.Core.Config;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace HomelabBackup.Core.Infrastructure;

public sealed class SftpService : ITransferService
{
    private readonly DestinationConfig _config;
    private readonly ILogger<SftpService> _logger;
    private SftpClient? _client;

    public SftpService(DestinationConfig config, ILogger<SftpService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool IsConnected => _client?.IsConnected ?? false;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var passphrase = Environment.GetEnvironmentVariable("BACKUP_KEY_PASSPHRASE");
            PrivateKeyFile keyFile;
            var keyPath = _config.SshKeyPath ?? "/keys/id_ed25519";

            try
            {
                keyFile = string.IsNullOrEmpty(passphrase)
                    ? new PrivateKeyFile(keyPath)
                    : new PrivateKeyFile(keyPath, passphrase);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load SSH key from '{keyPath}': {ex.Message}", ex);
            }

            var host = _config.SshHost ?? throw new InvalidOperationException("SSH host is not configured.");
            var user = _config.SshUser ?? throw new InvalidOperationException("SSH user is not configured.");

            var authMethod = new PrivateKeyAuthenticationMethod(user, keyFile);
            var connectionInfo = new ConnectionInfo(host, _config.SshPort, user, authMethod);

            _client = new SftpClient(connectionInfo);
            _client.Connect();
            _logger.LogInformation("Connected to SFTP host {Host}:{Port}", host, _config.SshPort);
        }, ct);
    }

    public Task DisconnectAsync()
    {
        if (_client?.IsConnected == true)
        {
            _client.Disconnect();
            _logger.LogInformation("Disconnected from SFTP host {Host}", _config.SshHost);
        }
        return Task.CompletedTask;
    }

    public Task UploadAsync(string localPath, string remotePath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureConnected();

            using var fileStream = File.OpenRead(localPath);
            _client!.UploadFile(fileStream, remotePath, offset =>
            {
                progress?.Report((long)offset);
            });

            _logger.LogDebug("Uploaded {LocalPath} → {RemotePath}", localPath, remotePath);
        }, ct);
    }

    public Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureConnected();

            var directory = System.IO.Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var fileStream = File.Create(localPath);
            _client!.DownloadFile(remotePath, fileStream, offset =>
            {
                progress?.Report((long)offset);
            });

            _logger.LogDebug("Downloaded {RemotePath} → {LocalPath}", remotePath, localPath);
        }, ct);
    }

    public Task DownloadToStreamAsync(string remotePath, Stream destination, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureConnected();
            _client!.DownloadFile(remotePath, destination);
        }, ct);
    }

    public Task<IReadOnlyList<RemoteFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<RemoteFileInfo>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureConnected();

            var entries = _client!.ListDirectory(remotePath);
            var result = new List<RemoteFileInfo>();

            foreach (var entry in entries)
            {
                if (entry.Name is "." or "..")
                    continue;
                if (entry.IsDirectory)
                    continue;

                result.Add(new RemoteFileInfo(
                    entry.Name,
                    entry.FullName,
                    entry.Length,
                    entry.LastWriteTimeUtc));
            }

            return result;
        }, ct);
    }

    public Task<IReadOnlyList<string>> ListSubdirectoriesAsync(string remotePath, CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureConnected();

            try
            {
                var entries = _client!.ListDirectory(remotePath);
                return entries
                    .Where(e => e.IsDirectory && e.Name is not ("." or ".."))
                    .Select(e => e.Name)
                    .OrderBy(n => n)
                    .ToList<string>();
            }
            catch (SftpPathNotFoundException)
            {
                return [];
            }
        }, ct);
    }

    public Task DeleteAsync(string remotePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureConnected();
            _client!.DeleteFile(remotePath);
            _logger.LogDebug("Deleted remote file {RemotePath}", remotePath);
        }, ct);
    }

    public Task EnsureDirectoryExistsAsync(string remotePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureConnected();

            var parts = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = "/";

            foreach (var part in parts)
            {
                current = current == "/" ? $"/{part}" : $"{current}/{part}";
                if (!_client!.Exists(current))
                {
                    _client.CreateDirectory(current);
                    _logger.LogDebug("Created remote directory {Path}", current);
                }
            }
        }, ct);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        string? remoteTmpPath = null;
        string? localTmp = null;
        try
        {
            await ConnectAsync(ct);
            remoteTmpPath = $"{_config.Path.TrimEnd('/')}/safetynet-test-{Guid.NewGuid():N}.tmp";
            localTmp = System.IO.Path.GetTempFileName();
            await File.WriteAllTextAsync(localTmp, "safetynet connection test", ct);
            await UploadAsync(localTmp, remoteTmpPath, null, ct);
            await DeleteAsync(remoteTmpPath, ct);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { await DisconnectAsync(); } catch { /* ignored */ }
            if (localTmp is not null && File.Exists(localTmp))
                File.Delete(localTmp);
        }
    }

    private void EnsureConnected()
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP client is not connected. Call ConnectAsync first.");
    }

    public void Dispose()
    {
        if (_client?.IsConnected == true)
            _client.Disconnect();
        _client?.Dispose();
        _client = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _client?.Dispose();
        _client = null;
    }
}
