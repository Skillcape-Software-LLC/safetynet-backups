namespace HomelabBackup.Core.Infrastructure;

public interface ITransferService : IAsyncDisposable, IDisposable
{
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task UploadAsync(string localPath, string remotePath, IProgress<long>? progress = null, CancellationToken ct = default);
    Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null, CancellationToken ct = default);
    Task DownloadToStreamAsync(string remotePath, Stream destination, CancellationToken ct = default);
    Task<IReadOnlyList<RemoteFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListSubdirectoriesAsync(string remotePath, CancellationToken ct = default);
    Task DeleteAsync(string remotePath, CancellationToken ct = default);
    Task EnsureDirectoryExistsAsync(string remotePath, CancellationToken ct = default);
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
    bool IsConnected { get; }
}
