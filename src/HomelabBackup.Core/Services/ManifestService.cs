using System.Text.Json;
using HomelabBackup.Core.Infrastructure;
using HomelabBackup.Core.Models;

namespace HomelabBackup.Core.Services;

public sealed class ManifestService : IManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public BackupManifest Create(string sourceName, string sourcePath, string archiveFileName, ArchiveResult archiveResult)
    {
        return new BackupManifest(
            Name: sourceName,
            CreatedUtc: DateTime.UtcNow,
            SourcePath: sourcePath,
            ArchiveFileName: archiveFileName,
            UncompressedBytes: archiveResult.UncompressedBytes,
            CompressedBytes: archiveResult.CompressedBytes,
            Files: archiveResult.Files);
    }

    public async Task WriteAsync(BackupManifest manifest, string localPath, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var stream = File.Create(localPath);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, ct);
    }

    public async Task<BackupManifest> ReadAsync(string localPath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(localPath);
        return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, ct)
            ?? throw new InvalidOperationException($"Failed to deserialize manifest from {localPath}");
    }

    public async Task<BackupManifest> ReadFromStreamAsync(Stream stream, CancellationToken ct = default)
    {
        return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, ct)
            ?? throw new InvalidOperationException("Failed to deserialize manifest from stream");
    }

    public string GetManifestFileName(string archiveFileName)
    {
        return Path.ChangeExtension(archiveFileName, ".manifest.json");
    }
}
