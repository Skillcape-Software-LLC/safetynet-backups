using HomelabBackup.Core.Config;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace HomelabBackup.Core.Infrastructure;

public sealed class SftpService : ISftpService
{
    private readonly SshConfig _config;
    private readonly ILogger<SftpService> _logger;
    private SftpClient? _client;

    public SftpService(SshConfig config, ILogger<SftpService> logger)
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

            try
            {
                keyFile = string.IsNullOrEmpty(passphrase)
                    ? new PrivateKeyFile(_config.KeyPath)
                    : new PrivateKeyFile(_config.KeyPath, passphrase);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load SSH key from '{_config.KeyPath}': {ex.Message}", ex);
            }

            var authMethod = new PrivateKeyAuthenticationMethod(_config.User, keyFile);
            var connectionInfo = new ConnectionInfo(_config.Host, _config.Port, _config.User, authMethod);

            _client = new SftpClient(connectionInfo);
            _client.Connect();
            _logger.LogInformation("Connected to SFTP host {Host}:{Port}", _config.Host, _config.Port);
        }, ct);
    }

    public Task DisconnectAsync()
    {
        if (_client?.IsConnected == true)
        {
            _client.Disconnect();
            _logger.LogInformation("Disconnected from SFTP host {Host}", _config.Host);
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

            var directory = Path.GetDirectoryName(localPath);
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
