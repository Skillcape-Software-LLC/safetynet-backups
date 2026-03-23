namespace HomelabBackup.Core.Models;

public record RetentionRule(
    int KeepLast,
    int MaxAgeDays);
