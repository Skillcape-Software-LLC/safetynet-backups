using HomelabBackup.Core.Config;
using HomelabBackup.Core.Data;

namespace HomelabBackup.Web.Services;

/// <summary>
/// Persists configuration changes to the SQLite database and refreshes the in-memory
/// BackupConfig singleton so engines pick up changes without a container restart.
/// </summary>
public sealed class ConfigService(
    BackupConfig currentConfig,
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

    /// <summary>
    /// Validates, saves or updates a destination, and refreshes the in-memory destination list.
    /// Returns the destination ID (new or existing).
    /// </summary>
    public int SaveDestination(DestinationConfig destination)
    {
        ConfigLoader.ValidateDestination(destination);
        var id = repository.SaveDestination(destination);
        destination.Id = id;
        RefreshDestinations();
        return id;
    }

    /// <summary>
    /// Deletes a destination and updates all sources that referenced it.
    /// </summary>
    public void DeleteDestination(int id)
    {
        repository.DeleteDestination(id);
        RefreshDestinations();

        // Null out DestinationId on in-memory sources that pointed to this destination
        foreach (var source in currentConfig.Sources.Where(s => s.DestinationId == id))
            source.DestinationId = null;
    }

    /// <summary>
    /// Updates the destination assignment for a source and persists it.
    /// </summary>
    public void SetSourceDestination(string sourceName, int? destinationId)
    {
        var source = currentConfig.Sources.FirstOrDefault(
            s => s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
        if (source is null) return;

        source.DestinationId = destinationId;
        repository.Save(currentConfig);
    }

    private void Refresh(BackupConfig from)
    {
        currentConfig.Sources = from.Sources;
        currentConfig.Retention.KeepLast = from.Retention.KeepLast;
        currentConfig.Retention.MaxAgeDays = from.Retention.MaxAgeDays;
        currentConfig.Compression = from.Compression;
        currentConfig.Schedule = from.Schedule;
        currentConfig.BrowseRoot = from.BrowseRoot;
    }

    private void RefreshDestinations()
    {
        var fresh = repository.GetDestinations();
        currentConfig.Destinations.Clear();
        currentConfig.Destinations.AddRange(fresh);
    }
}
