using System.Net;
using HomelabBackup.Core.Config;
using Microsoft.Extensions.Logging;
using SMBLibrary;
using SMBLibrary.Client;
using SmbFileAttributes = SMBLibrary.FileAttributes;

namespace HomelabBackup.Core.Infrastructure;

/// <summary>
/// Transfer service that copies backups to an SMB/CIFS network share.
/// </summary>
public sealed class SmbTransferService : ITransferService
{
    private const int ReadChunkSize = 65536;
    private const int WriteChunkSize = 65536;

    private readonly DestinationConfig _config;
    private readonly ILogger<SmbTransferService> _logger;
    private SMB2Client? _client;
    private ISMBFileStore? _fileStore;

    public SmbTransferService(DestinationConfig config, ILogger<SmbTransferService> logger)
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

            var host = _config.SmbHost ?? throw new InvalidOperationException("SMB host is not configured.");
            var share = _config.SmbShare ?? throw new InvalidOperationException("SMB share is not configured.");
            var username = _config.SmbUsername ?? throw new InvalidOperationException("SMB username is not configured.");
            var domain = _config.SmbDomain ?? string.Empty;
            var password = _config.SmbPassword ?? string.Empty;

            if (!IPAddress.TryParse(host, out var ip))
            {
                var addresses = Dns.GetHostAddresses(host);
                ip = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                     ?? addresses.First();
            }

            _client = new SMB2Client();
            var connected = _client.Connect(ip, SMBTransportType.DirectTCPTransport);
            if (!connected)
                throw new InvalidOperationException($"Failed to connect to SMB host {host}.");

            var status = _client.Login(domain, username, password);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new InvalidOperationException($"SMB login failed for user '{username}': {status}");

            _fileStore = _client.TreeConnect(share, out status);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new InvalidOperationException($"Failed to connect to SMB share '{share}': {status}");

            _logger.LogInformation("Connected to SMB share \\\\{Host}\\{Share}", host, share);
        }, ct);
    }

    public Task DisconnectAsync()
    {
        _fileStore = null;
        if (_client?.IsConnected == true)
        {
            _client.Logoff();
            _client.Disconnect();
            _logger.LogInformation("Disconnected from SMB host {Host}", _config.SmbHost);
        }
        return Task.CompletedTask;
    }

    public async Task UploadAsync(string localPath, string remotePath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureConnected();

        var smbPath = ToSmbPath(remotePath);
        var parentDir = GetSmbParent(smbPath);
        if (!string.IsNullOrEmpty(parentDir))
            CreateDirectoryIfMissing(parentDir);

        var status = _fileStore!.CreateFile(out var handle, out _, smbPath,
            AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, 0,
            ShareAccess.None, CreateDisposition.FILE_OVERWRITE_IF,
            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (status != NTStatus.STATUS_SUCCESS)
            throw new InvalidOperationException($"Failed to create file '{smbPath}': {status}");

        try
        {
            using var fileStream = File.OpenRead(localPath);
            var buffer = new byte[WriteChunkSize];
            long offset = 0;
            int bytesRead;

            while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                var data = buffer.AsMemory(0, bytesRead).ToArray();
                status = _fileStore.WriteFile(out _, handle, offset, data);
                if (status != NTStatus.STATUS_SUCCESS)
                    throw new InvalidOperationException($"Failed to write to file '{smbPath}': {status}");
                offset += bytesRead;
                progress?.Report(offset);
            }
        }
        finally
        {
            _fileStore!.CloseFile(handle);
        }

        _logger.LogDebug("Uploaded {LocalPath} → {SmbPath}", localPath, smbPath);
    }

    public async Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureConnected();

        var destDir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        using var fileStream = File.Create(localPath);
        long totalBytes = 0;

        await DownloadToStreamCoreAsync(remotePath, fileStream, (bytes) =>
        {
            totalBytes += bytes;
            progress?.Report(totalBytes);
        }, ct);

        _logger.LogDebug("Downloaded {SmbPath} → {LocalPath}", remotePath, localPath);
    }

    public Task DownloadToStreamAsync(string remotePath, Stream destination, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureConnected();
        return DownloadToStreamCoreAsync(remotePath, destination, null, ct);
    }

    private async Task DownloadToStreamCoreAsync(string remotePath, Stream destination, Action<int>? progressCallback, CancellationToken ct)
    {
        var smbPath = ToSmbPath(remotePath);

        var status = _fileStore!.CreateFile(out var handle, out _, smbPath,
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, 0,
            ShareAccess.Read, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (status != NTStatus.STATUS_SUCCESS)
            throw new InvalidOperationException($"Failed to open file '{smbPath}': {status}");

        try
        {
            long offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                status = _fileStore.ReadFile(out var data, handle, offset, ReadChunkSize);

                if (status == NTStatus.STATUS_END_OF_FILE || (status == NTStatus.STATUS_SUCCESS && (data is null || data.Length == 0)))
                    break;

                if (status != NTStatus.STATUS_SUCCESS)
                    throw new InvalidOperationException($"Failed to read file '{smbPath}': {status}");

                await destination.WriteAsync(data, ct);
                offset += data.Length;
                progressCallback?.Invoke(data.Length);
            }
        }
        finally
        {
            _fileStore!.CloseFile(handle);
        }
    }

    public Task<IReadOnlyList<RemoteFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureConnected();

        var smbPath = ToSmbPath(remotePath);

        var status = _fileStore!.CreateFile(out var handle, out _, smbPath,
            AccessMask.GENERIC_READ, 0,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE, null);

        if (status != NTStatus.STATUS_SUCCESS)
            return Task.FromResult<IReadOnlyList<RemoteFileInfo>>([]);

        try
        {
            status = _fileStore.QueryDirectory(out var entries, handle, "*",
                FileInformationClass.FileDirectoryInformation);

            if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_NO_MORE_FILES)
                return Task.FromResult<IReadOnlyList<RemoteFileInfo>>([]);

            var result = new List<RemoteFileInfo>();
            if (entries is not null)
            {
                foreach (FileDirectoryInformation entry in entries)
                {
                    if (entry.FileName is "." or "..")
                        continue;
                    if ((entry.FileAttributes & SmbFileAttributes.Directory) != 0)
                        continue;

                    var fullPath = $"{remotePath.TrimEnd('/')}/{entry.FileName}";
                    result.Add(new RemoteFileInfo(
                        entry.FileName,
                        fullPath,
                        entry.EndOfFile,
                        entry.LastWriteTime.ToUniversalTime()));
                }
            }

            return Task.FromResult<IReadOnlyList<RemoteFileInfo>>(result);
        }
        finally
        {
            _fileStore!.CloseFile(handle);
        }
    }

    public Task<IReadOnlyList<string>> ListSubdirectoriesAsync(string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureConnected();

        var smbPath = ToSmbPath(remotePath);

        var status = _fileStore!.CreateFile(out var handle, out _, smbPath,
            AccessMask.GENERIC_READ, 0,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE, null);

        if (status != NTStatus.STATUS_SUCCESS)
            return Task.FromResult<IReadOnlyList<string>>([]);

        try
        {
            status = _fileStore.QueryDirectory(out var entries, handle, "*",
                FileInformationClass.FileDirectoryInformation);

            if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_NO_MORE_FILES)
                return Task.FromResult<IReadOnlyList<string>>([]);

            var result = new List<string>();
            if (entries is not null)
            {
                foreach (FileDirectoryInformation entry in entries)
                {
                    if (entry.FileName is "." or "..")
                        continue;
                    if ((entry.FileAttributes & SmbFileAttributes.Directory) != 0)
                        result.Add(entry.FileName);
                }
            }

            return Task.FromResult<IReadOnlyList<string>>(result.OrderBy(n => n).ToList<string>());
        }
        finally
        {
            _fileStore!.CloseFile(handle);
        }
    }

    public Task DeleteAsync(string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureConnected();

        var smbPath = ToSmbPath(remotePath);

        var status = _fileStore!.CreateFile(out var handle, out _, smbPath,
            AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, 0,
            ShareAccess.None, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (status != NTStatus.STATUS_SUCCESS)
        {
            _logger.LogWarning("Could not open file for deletion '{SmbPath}': {Status}", smbPath, status);
            return Task.CompletedTask;
        }

        try
        {
            _fileStore.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
        }
        finally
        {
            _fileStore!.CloseFile(handle);
        }

        _logger.LogDebug("Deleted SMB file {SmbPath}", smbPath);
        return Task.CompletedTask;
    }

    public Task EnsureDirectoryExistsAsync(string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureConnected();

        var smbPath = ToSmbPath(remotePath);
        var parts = smbPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;

        foreach (var part in parts)
        {
            current = string.IsNullOrEmpty(current) ? part : $"{current}\\{part}";
            CreateDirectoryIfMissing(current);
        }

        return Task.CompletedTask;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        string? testPath = null;
        try
        {
            await ConnectAsync(ct);
            var basePath = _config.Path.TrimEnd('/').TrimEnd('\\');
            testPath = $"{basePath}/safetynet-test-{Guid.NewGuid():N}.tmp";
            var localTmp = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(localTmp, "safetynet connection test", ct);
                await UploadAsync(localTmp, testPath, null, ct);
                await DeleteAsync(testPath, ct);
            }
            finally
            {
                if (File.Exists(localTmp)) File.Delete(localTmp);
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { await DisconnectAsync(); } catch { /* ignored */ }
        }
    }

    private void CreateDirectoryIfMissing(string smbPath)
    {
        var status = _fileStore!.CreateFile(out var handle, out _,
            smbPath,
            (AccessMask)DirectoryAccessMask.FILE_LIST_DIRECTORY | AccessMask.SYNCHRONIZE, 0,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN_IF,
            CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (status == NTStatus.STATUS_SUCCESS)
            _fileStore.CloseFile(handle);
    }

    private static string ToSmbPath(string path)
    {
        return path.Replace('/', '\\').TrimStart('\\');
    }

    private static string GetSmbParent(string smbPath)
    {
        var idx = smbPath.LastIndexOf('\\');
        return idx > 0 ? smbPath[..idx] : string.Empty;
    }

    private void EnsureConnected()
    {
        if (_client is null || !_client.IsConnected || _fileStore is null)
            throw new InvalidOperationException("SMB client is not connected. Call ConnectAsync first.");
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        _client = null;
        _fileStore = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _client = null;
        _fileStore = null;
    }
}
