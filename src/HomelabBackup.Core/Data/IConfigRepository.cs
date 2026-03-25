using HomelabBackup.Core.Config;

namespace HomelabBackup.Core.Data;

public interface IConfigRepository
{
    /// <summary>Creates tables if they don't exist. Safe to call on every startup.</summary>
    void EnsureSchema();

    /// <summary>Returns true if no settings rows exist (fresh database).</summary>
    bool IsEmpty();

    /// <summary>Reads all settings and sources from the database and builds a BackupConfig.</summary>
    BackupConfig Load();

    /// <summary>Persists all settings and sources to the database (replaces sources entirely).</summary>
    void Save(BackupConfig config);

    // --- Destination CRUD ---

    List<DestinationConfig> GetDestinations();
    int SaveDestination(DestinationConfig destination);
    void DeleteDestination(int id);
}
