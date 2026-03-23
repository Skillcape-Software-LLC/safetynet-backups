namespace HomelabBackup.Core.Models;

public record ManifestEntry(
    string RelativePath,
    long Size,
    string Sha256);
