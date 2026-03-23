namespace HomelabBackup.Core.Infrastructure;

public record RemoteFileInfo(
    string Name,
    string FullPath,
    long Size,
    DateTime LastModifiedUtc);
