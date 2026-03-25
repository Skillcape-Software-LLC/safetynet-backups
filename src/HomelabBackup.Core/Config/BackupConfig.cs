using YamlDotNet.Serialization;

namespace HomelabBackup.Core.Config;

public enum DestinationType { Ssh, Local, Smb }

public class BackupConfig
{
    [YamlMember(Alias = "destinations")]
    public List<DestinationConfig> Destinations { get; set; } = [];

    [YamlMember(Alias = "sources")]
    public List<SourceConfig> Sources { get; set; } = [];

    [YamlMember(Alias = "retention")]
    public RetentionConfig Retention { get; set; } = new();

    [YamlMember(Alias = "compression")]
    public string Compression { get; set; } = "optimal";

    [YamlMember(Alias = "schedule")]
    public ScheduleConfig? Schedule { get; set; }

    [YamlMember(Alias = "browse_root")]
    public string? BrowseRoot { get; set; }
}

public class DestinationConfig
{
    public int Id { get; set; }

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "type")]
    public DestinationType Type { get; set; } = DestinationType.Ssh;

    [YamlMember(Alias = "path")]
    public string Path { get; set; } = string.Empty;

    // SSH fields
    [YamlMember(Alias = "ssh_host")]
    public string? SshHost { get; set; }

    [YamlMember(Alias = "ssh_port")]
    public int SshPort { get; set; } = 22;

    [YamlMember(Alias = "ssh_user")]
    public string? SshUser { get; set; }

    [YamlMember(Alias = "ssh_key_path")]
    public string? SshKeyPath { get; set; }

    // SMB fields
    [YamlMember(Alias = "smb_host")]
    public string? SmbHost { get; set; }

    [YamlMember(Alias = "smb_share")]
    public string? SmbShare { get; set; }

    [YamlMember(Alias = "smb_domain")]
    public string? SmbDomain { get; set; }

    [YamlMember(Alias = "smb_username")]
    public string? SmbUsername { get; set; }

    [YamlMember(Alias = "smb_password")]
    public string? SmbPassword { get; set; }
}

public class SourceConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "path")]
    public string Path { get; set; } = string.Empty;

    [YamlMember(Alias = "exclude")]
    public List<string> Exclude { get; set; } = [];

    [YamlMember(Alias = "destination_id")]
    public int? DestinationId { get; set; }
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
