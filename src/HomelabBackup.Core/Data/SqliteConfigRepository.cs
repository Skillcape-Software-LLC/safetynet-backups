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
            CREATE TABLE IF NOT EXISTS sources (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                name     TEXT NOT NULL UNIQUE,
                path     TEXT NOT NULL,
                excludes TEXT,
                enabled  INTEGER NOT NULL DEFAULT 1
            );
            """;
        cmd.ExecuteNonQuery();
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
            cmd.CommandText = "SELECT name, path, excludes FROM sources WHERE enabled = 1 ORDER BY id";
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
                        : JsonSerializer.Deserialize<List<string>>(excludesJson) ?? []
                });
            }
        }

        string? Get(string key) => settings.TryGetValue(key, out var v) ? v : null;
        int GetInt(string key, int fallback) =>
            int.TryParse(Get(key), out var v) ? v : fallback;

        var cronValue = Get("cron");

        return new BackupConfig
        {
            Ssh = new SshConfig
            {
                Host = Get("ssh_host") ?? string.Empty,
                Port = GetInt("ssh_port", 22),
                User = Get("ssh_user") ?? string.Empty,
                KeyPath = Get("ssh_key_path") ?? "/keys/id_ed25519"
            },
            Sources = sources,
            Destination = new DestinationConfig { Path = Get("dest_path") ?? string.Empty },
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

        Upsert("ssh_host", config.Ssh.Host);
        Upsert("ssh_port", config.Ssh.Port.ToString());
        Upsert("ssh_user", config.Ssh.User);
        Upsert("ssh_key_path", config.Ssh.KeyPath);
        Upsert("dest_path", config.Destination.Path);
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
            cmd.CommandText = "INSERT INTO sources (name, path, excludes) VALUES ($n, $p, $e)";
            cmd.Parameters.AddWithValue("$n", source.Name);
            cmd.Parameters.AddWithValue("$p", source.Path);
            cmd.Parameters.AddWithValue("$e",
                source.Exclude.Count > 0
                    ? JsonSerializer.Serialize(source.Exclude)
                    : (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
