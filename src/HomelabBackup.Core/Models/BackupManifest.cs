using System.Text.Json.Serialization;

namespace HomelabBackup.Core.Models;

public record BackupManifest(
    string Name,
    DateTime CreatedUtc,
    string SourcePath,
    string ArchiveFileName,
    long UncompressedBytes,
    long CompressedBytes,
    [property: JsonPropertyName("files")]
    IReadOnlyList<ManifestEntry> Files);
