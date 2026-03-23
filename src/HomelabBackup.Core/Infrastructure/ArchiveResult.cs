using HomelabBackup.Core.Models;

namespace HomelabBackup.Core.Infrastructure;

public record ArchiveResult(
    string ZipFilePath,
    long UncompressedBytes,
    long CompressedBytes,
    IReadOnlyList<ManifestEntry> Files);
