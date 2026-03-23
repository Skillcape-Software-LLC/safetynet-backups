namespace HomelabBackup.Core.Models;

public enum BackupPhase
{
    Scanning,
    Compressing,
    Transferring,
    Verifying,
    Complete,
    Failed
}
