using YamlDotNet.Serialization;

namespace HomelabBackup.Core.Config;

public class BackupConfig
{
    [YamlMember(Alias = "ssh")]
    public SshConfig Ssh { get; set; } = new();

    [YamlMember(Alias = "sources")]
    public List<SourceConfig> Sources { get; set; } = [];

    [YamlMember(Alias = "destination")]
    public DestinationConfig Destination { get; set; } = new();

    [YamlMember(Alias = "retention")]
    public RetentionConfig Retention { get; set; } = new();

    [YamlMember(Alias = "compression")]
    public string Compression { get; set; } = "optimal";

    [YamlMember(Alias = "schedule")]
    public ScheduleConfig? Schedule { get; set; }

    [YamlMember(Alias = "browse_root")]
    public string? BrowseRoot { get; set; }
}

public class SshConfig
{
    [YamlMember(Alias = "host")]
    public string Host { get; set; } = string.Empty;

    [YamlMember(Alias = "port")]
    public int Port { get; set; } = 22;

    [YamlMember(Alias = "user")]
    public string User { get; set; } = string.Empty;

    [YamlMember(Alias = "key_path")]
    public string KeyPath { get; set; } = "/keys/id_ed25519";
}

public class SourceConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "path")]
    public string Path { get; set; } = string.Empty;

    [YamlMember(Alias = "exclude")]
    public List<string> Exclude { get; set; } = [];
}

public class DestinationConfig
{
    [YamlMember(Alias = "path")]
    public string Path { get; set; } = string.Empty;
}

public class RetentionConfig
{
    [YamlMember(Alias = "keep_last")]
    public int KeepLast { get; set; } = 7;

    [YamlMember(Alias = "max_age_days")]
    public int MaxAgeDays { get; set; } = 30;
}

public class ScheduleConfig
{
    [YamlMember(Alias = "cron")]
    public string? Cron { get; set; }
}
