using System.Text.Json;
using HomelabBackup.Core.Config;
using Microsoft.Data.Sqlite;

namespace HomelabBackup.Core.Data;

public sealed class SqliteConfigRepository(string dbPath) : IConfigRepository
{
    private string ConnectionString => $"Data Source={dbPath}";

    public void EnsureSchema()
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
            CREATE TABLE IF NOT EXISTS destinations (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                name         TEXT NOT NULL UNIQUE,
                type         TEXT NOT NULL DEFAULT 'ssh',
                path         TEXT NOT NULL DEFAULT '',
                ssh_host     TEXT,
                ssh_port     INTEGER NOT NULL DEFAULT 22,
                ssh_user     TEXT,
                ssh_key_path TEXT,
                smb_host     TEXT,
                smb_share    TEXT,
                smb_domain   TEXT,
                smb_username TEXT,
                smb_password TEXT
            );
            CREATE TABLE IF NOT EXISTS sources (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                name           TEXT NOT NULL UNIQUE,
                path           TEXT NOT NULL,
                excludes       TEXT,
                enabled        INTEGER NOT NULL DEFAULT 1,
                destination_id INTEGER REFERENCES destinations(id)
            );
            """;
        cmd.ExecuteNonQuery();

        // Add destination_id column if upgrading from older schema
        EnsureColumn(conn, "sources", "destination_id", "INTEGER REFERENCES destinations(id)");

        // Migrate legacy SSH settings to a destination row if needed
        MigrateLegacySshSettings(conn);
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        if (!columns.Contains(column, StringComparer.OrdinalIgnoreCase))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            alter.ExecuteNonQuery();
        }
    }

    private static void MigrateLegacySshSettings(SqliteConnection conn)
    {
        // Check if destinations table is empty
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM destinations";
        var count = (long)countCmd.ExecuteScalar()!;
        if (count > 0) return;

        // Check if legacy SSH settings exist
        using var settingsCmd = conn.CreateCommand();
        settingsCmd.CommandText = "SELECT key, value FROM settings WHERE key IN ('ssh_host','ssh_port','ssh_user','ssh_key_path','dest_path')";
        var settings = new Dictionary<string, string?>();
        using var reader = settingsCmd.ExecuteReader();
        while (reader.Read())
            settings[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);

        if (!settings.ContainsKey("ssh_host") || string.IsNullOrWhiteSpace(settings.GetValueOrDefault("ssh_host")))
            return;

        // Insert a Default SSH destination
        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO destinations (name, type, path, ssh_host, ssh_port, ssh_user, ssh_key_path)
            VALUES ('Default SSH', 'ssh', $path, $host, $port, $user, $key)
            """;
        insertCmd.Parameters.AddWithValue("$path", settings.GetValueOrDefault("dest_path") ?? "/backups");
        insertCmd.Parameters.AddWithValue("$host", settings.GetValueOrDefault("ssh_host") ?? "");
        insertCmd.Parameters.AddWithValue("$port",
            int.TryParse(settings.GetValueOrDefault("ssh_port"), out var p) ? p : 22);
        insertCmd.Parameters.AddWithValue("$user", settings.GetValueOrDefault("ssh_user") ?? "");
        insertCmd.Parameters.AddWithValue("$key", settings.GetValueOrDefault("ssh_key_path") ?? "/keys/id_ed25519");
        insertCmd.ExecuteNonQuery();
    }

    public bool IsEmpty()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM settings";
        return (long)cmd.ExecuteScalar()! == 0;
    }

    public BackupConfig Load()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM settings";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                settings[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var sources = new List<SourceConfig>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, path, excludes, destination_id FROM sources WHERE enabled = 1 ORDER BY id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var excludesJson = reader.IsDBNull(2) ? null : reader.GetString(2);
                sources.Add(new SourceConfig
                {
                    Name = reader.GetString(0),
                    Path = reader.GetString(1),
                    Exclude = excludesJson is null
                        ? []
                        : JsonSerializer.Deserialize<List<string>>(excludesJson) ?? [],
                    DestinationId = reader.IsDBNull(3) ? null : reader.GetInt32(3)
                });
            }
        }

        var destinations = GetDestinations(conn);

        string? Get(string key) => settings.TryGetValue(key, out var v) ? v : null;
        int GetInt(string key, int fallback) =>
            int.TryParse(Get(key), out var v) ? v : fallback;

        var cronValue = Get("cron");

        return new BackupConfig
        {
            Destinations = destinations,
            Sources = sources,
            Retention = new RetentionConfig
            {
                KeepLast = GetInt("keep_last", 7),
                MaxAgeDays = GetInt("max_age_days", 30)
            },
            Compression = Get("compression") ?? "optimal",
            Schedule = string.IsNullOrWhiteSpace(cronValue) ? null : new ScheduleConfig { Cron = cronValue },
            BrowseRoot = Get("browse_root")
        };
    }

    public void Save(BackupConfig config)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();

        void Upsert(string key, string? value)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        Upsert("keep_last", config.Retention.KeepLast.ToString());
        Upsert("max_age_days", config.Retention.MaxAgeDays.ToString());
        Upsert("compression", config.Compression);
        Upsert("cron", config.Schedule?.Cron);
        Upsert("browse_root", config.BrowseRoot);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM sources";
            cmd.ExecuteNonQuery();
        }

        foreach (var source in config.Sources)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO sources (name, path, excludes, destination_id) VALUES ($n, $p, $e, $d)";
            cmd.Parameters.AddWithValue("$n", source.Name);
            cmd.Parameters.AddWithValue("$p", source.Path);
            cmd.Parameters.AddWithValue("$e",
                source.Exclude.Count > 0
                    ? JsonSerializer.Serialize(source.Exclude)
                    : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$d",
                source.DestinationId.HasValue ? source.DestinationId.Value : (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public List<DestinationConfig> GetDestinations()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return GetDestinations(conn);
    }

    private static List<DestinationConfig> GetDestinations(SqliteConnection conn)
    {
        var destinations = new List<DestinationConfig>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, type, path,
                   ssh_host, ssh_port, ssh_user, ssh_key_path,
                   smb_host, smb_share, smb_domain, smb_username, smb_password
            FROM destinations ORDER BY id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            destinations.Add(new DestinationConfig
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Type = Enum.TryParse<DestinationType>(reader.GetString(2), true, out var t) ? t : DestinationType.Ssh,
                Path = reader.GetString(3),
                SshHost = reader.IsDBNull(4) ? null : reader.GetString(4),
                SshPort = reader.IsDBNull(5) ? 22 : reader.GetInt32(5),
                SshUser = reader.IsDBNull(6) ? null : reader.GetString(6),
                SshKeyPath = reader.IsDBNull(7) ? null : reader.GetString(7),
                SmbHost = reader.IsDBNull(8) ? null : reader.GetString(8),
                SmbShare = reader.IsDBNull(9) ? null : reader.GetString(9),
                SmbDomain = reader.IsDBNull(10) ? null : reader.GetString(10),
                SmbUsername = reader.IsDBNull(11) ? null : reader.GetString(11),
                SmbPassword = reader.IsDBNull(12) ? null : reader.GetString(12)
            });
        }
        return destinations;
    }

    public int SaveDestination(DestinationConfig destination)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        if (destination.Id == 0)
        {
            // Insert
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO destinations (name, type, path, ssh_host, ssh_port, ssh_user, ssh_key_path,
                                          smb_host, smb_share, smb_domain, smb_username, smb_password)
                VALUES ($name, $type, $path, $ssh_host, $ssh_port, $ssh_user, $ssh_key_path,
                        $smb_host, $smb_share, $smb_domain, $smb_username, $smb_password);
                SELECT last_insert_rowid();
                """;
            BindDestinationParams(cmd, destination);
            var newId = (long)cmd.ExecuteScalar()!;
            return (int)newId;
        }
        else
        {
            // Update
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE destinations SET
                    name=$name, type=$type, path=$path,
                    ssh_host=$ssh_host, ssh_port=$ssh_port, ssh_user=$ssh_user, ssh_key_path=$ssh_key_path,
                    smb_host=$smb_host, smb_share=$smb_share, smb_domain=$smb_domain,
                    smb_username=$smb_username, smb_password=$smb_password
                WHERE id=$id
                """;
            cmd.Parameters.AddWithValue("$id", destination.Id);
            BindDestinationParams(cmd, destination);
            cmd.ExecuteNonQuery();
            return destination.Id;
        }
    }

    public void DeleteDestination(int id)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();

        // Clear destination_id on sources that reference this destination
        using (var clearCmd = conn.CreateCommand())
        {
            clearCmd.Transaction = tx;
            clearCmd.CommandText = "UPDATE sources SET destination_id = NULL WHERE destination_id = $id";
            clearCmd.Parameters.AddWithValue("$id", id);
            clearCmd.ExecuteNonQuery();
        }

        using (var deleteCmd = conn.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM destinations WHERE id = $id";
            deleteCmd.Parameters.AddWithValue("$id", id);
            deleteCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void BindDestinationParams(SqliteCommand cmd, DestinationConfig d)
    {
        cmd.Parameters.AddWithValue("$name", d.Name);
        cmd.Parameters.AddWithValue("$type", d.Type.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$path", d.Path);
        cmd.Parameters.AddWithValue("$ssh_host", (object?)d.SshHost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ssh_port", d.SshPort);
        cmd.Parameters.AddWithValue("$ssh_user", (object?)d.SshUser ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ssh_key_path", (object?)d.SshKeyPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$smb_host", (object?)d.SmbHost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$smb_share", (object?)d.SmbShare ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$smb_domain", (object?)d.SmbDomain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$smb_username", (object?)d.SmbUsername ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$smb_password", (object?)d.SmbPassword ?? DBNull.Value);
    }
}
