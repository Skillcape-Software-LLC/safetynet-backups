using Cronos;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HomelabBackup.Core.Config;

public static class ConfigLoader
{
    private static readonly string[] ValidCompressionValues = ["optimal", "fastest", "no_compression"];

    public static BackupConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaultConfig = new BackupConfig();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, Serialize(defaultConfig));
            return defaultConfig;
        }

        var yaml = File.ReadAllText(path);

        if (string.IsNullOrWhiteSpace(yaml))
            return new BackupConfig();

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        try
        {
            return deserializer.Deserialize<BackupConfig>(yaml) ?? new BackupConfig();
        }
        catch (Exception ex) when (ex is not ConfigurationException)
        {
            throw new ConfigurationException($"Failed to parse configuration: {ex.Message}", ex);
        }
    }

    public static void Validate(BackupConfig config)
    {
        foreach (var source in config.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name))
                throw new ConfigurationException("Each source must have a name.");
            if (string.IsNullOrWhiteSpace(source.Path))
                throw new ConfigurationException($"Source '{source.Name}' must have a path.");
        }

        if (!ValidCompressionValues.Contains(config.Compression, StringComparer.OrdinalIgnoreCase))
            throw new ConfigurationException(
                $"Invalid compression value '{config.Compression}'. Must be one of: {string.Join(", ", ValidCompressionValues)}");

        if (config.Retention.KeepLast < 1)
            throw new ConfigurationException("Retention keep_last must be at least 1.");

        if (config.Retention.MaxAgeDays < 1)
            throw new ConfigurationException("Retention max_age_days must be at least 1.");

        if (config.Schedule?.Cron is not null)
        {
            try
            {
                CronExpression.Parse(config.Schedule.Cron);
            }
            catch (CronFormatException ex)
            {
                throw new ConfigurationException($"Invalid cron expression '{config.Schedule.Cron}': {ex.Message}", ex);
            }
        }
    }

    public static void ValidateDestination(DestinationConfig dest)
    {
        if (string.IsNullOrWhiteSpace(dest.Name))
            throw new ConfigurationException("Destination name is required.");

        if (string.IsNullOrWhiteSpace(dest.Path))
            throw new ConfigurationException($"Destination '{dest.Name}' must have a path.");

        switch (dest.Type)
        {
            case DestinationType.Ssh:
                if (string.IsNullOrWhiteSpace(dest.SshHost))
                    throw new ConfigurationException($"SSH destination '{dest.Name}': host is required.");
                if (dest.SshPort is < 1 or > 65535)
                    throw new ConfigurationException($"SSH destination '{dest.Name}': port must be between 1 and 65535.");
                if (string.IsNullOrWhiteSpace(dest.SshUser))
                    throw new ConfigurationException($"SSH destination '{dest.Name}': user is required.");
                if (string.IsNullOrWhiteSpace(dest.SshKeyPath))
                    throw new ConfigurationException($"SSH destination '{dest.Name}': key path is required.");
                break;

            case DestinationType.Smb:
                if (string.IsNullOrWhiteSpace(dest.SmbHost))
                    throw new ConfigurationException($"SMB destination '{dest.Name}': host is required.");
                if (string.IsNullOrWhiteSpace(dest.SmbShare))
                    throw new ConfigurationException($"SMB destination '{dest.Name}': share name is required.");
                if (string.IsNullOrWhiteSpace(dest.SmbUsername))
                    throw new ConfigurationException($"SMB destination '{dest.Name}': username is required.");
                break;

            case DestinationType.Local:
                // path validation already covered above
                break;
        }
    }

    public static string Serialize(BackupConfig config)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        return serializer.Serialize(config);
    }
}
