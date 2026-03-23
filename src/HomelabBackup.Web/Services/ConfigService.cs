using HomelabBackup.Core.Config;
using HomelabBackup.Core.Data;

namespace HomelabBackup.Web.Services;

/// <summary>
/// Persists configuration changes to the SQLite database and refreshes the in-memory
/// BackupConfig singleton so engines pick up changes without a container restart.
/// </summary>
public sealed class ConfigService(
    BackupConfig currentConfig,
    SshConfig currentSsh,
    IConfigRepository repository)
{
    /// <summary>
    /// Validates, saves to SQLite, and refreshes the singleton config in-place.
    /// Throws <see cref="HomelabBackup.Core.Config.ConfigurationException"/> on validation failure.
    /// </summary>
    public void Save(BackupConfig incoming)
    {
        ConfigLoader.Validate(incoming);
        repository.Save(incoming);
        Refresh(incoming);
    }

    private void Refresh(BackupConfig from)
    {
        // SSH — update the registered SshConfig singleton (shared with SftpService)
        currentSsh.Host = from.Ssh.Host;
        currentSsh.Port = from.Ssh.Port;
        currentSsh.User = from.Ssh.User;
        currentSsh.KeyPath = from.Ssh.KeyPath;

        // Keep BackupConfig.Ssh in sync (it points to the same object, but be explicit)
        currentConfig.Ssh.Host = from.Ssh.Host;
        currentConfig.Ssh.Port = from.Ssh.Port;
        currentConfig.Ssh.User = from.Ssh.User;
        currentConfig.Ssh.KeyPath = from.Ssh.KeyPath;

        currentConfig.Sources = from.Sources;
        currentConfig.Destination.Path = from.Destination.Path;
        currentConfig.Retention.KeepLast = from.Retention.KeepLast;
        currentConfig.Retention.MaxAgeDays = from.Retention.MaxAgeDays;
        currentConfig.Compression = from.Compression;
        currentConfig.Schedule = from.Schedule;
        currentConfig.BrowseRoot = from.BrowseRoot;
    }
}
