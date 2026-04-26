namespace HomelabBackup.Web.Services;

public enum MountKind
{
    Source,
    Destination,
    Restore
}

/// <summary>
/// Centralizes the three internal Docker mount points and their corresponding host
/// directories so the UI can present friendly subpaths to users instead of leaking
/// container paths like /browse, /local-backups, or /restores.
/// </summary>
/// <remarks>
/// Persistence layer keeps using container-form paths; this helper only matters at the
/// view boundary. Constructor reads env vars once at startup; instance is registered as
/// a singleton.
/// </remarks>
public sealed class PathPresentation
{
    public string SourceMount { get; }
    public string DestinationMount { get; }
    public string RestoreMount { get; }

    public string? SourceHost { get; }
    public string? DestinationHost { get; }
    public string? RestoreHost { get; }

    public PathPresentation(string? sourceMount = null)
    {
        SourceMount = NormalizeMount(sourceMount
            ?? Environment.GetEnvironmentVariable("BROWSE_ROOT")
            ?? "/browse");
        DestinationMount = "/local-backups";
        RestoreMount = "/restores";

        SourceHost = NullIfBlank(Environment.GetEnvironmentVariable("DOCKER_DIR"));
        DestinationHost = NullIfBlank(Environment.GetEnvironmentVariable("LOCAL_BACKUP_DIR"));
        RestoreHost = NullIfBlank(Environment.GetEnvironmentVariable("RESTORE_DIR"));
    }

    /// <summary>
    /// Strips the mount prefix for display: "/local-backups/foo/bar" → "foo/bar".
    /// Paths not under the mount are returned unchanged.
    /// </summary>
    public string ToRelative(string path, MountKind kind)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var mount = MountFor(kind);
        var normalized = path.Replace('\\', '/');

        if (normalized.Equals(mount, StringComparison.OrdinalIgnoreCase))
            return "";

        var prefix = mount.EndsWith('/') ? mount : mount + "/";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return normalized[prefix.Length..];

        return path;
    }

    /// <summary>
    /// Maps a container path to the equivalent host path if the host root is known.
    /// Returns null when the host env var is unset or the path is not under the mount.
    /// </summary>
    public string? ToHost(string path, MountKind kind)
    {
        var host = HostFor(kind);
        if (host is null) return null;
        var relative = ToRelative(path, kind);
        if (ReferenceEquals(relative, path) && !path.Equals(MountFor(kind), StringComparison.OrdinalIgnoreCase))
            return null;
        return string.IsNullOrEmpty(relative)
            ? host.TrimEnd('/')
            : $"{host.TrimEnd('/')}/{relative.TrimStart('/')}";
    }

    /// <summary>
    /// Reverses ToRelative — converts a user-typed subfolder ("myserver", "/myserver",
    /// or even "/local-backups/myserver") into the canonical container form
    /// "/local-backups/myserver". Empty/blank input returns the bare mount root.
    /// Legacy absolute paths that aren't under the mount (e.g. "/mnt/backup" from
    /// pre-cleanup destinations) are returned unchanged so we don't relocate them.
    /// </summary>
    public string ToContainer(string relativeOrFull, MountKind kind)
    {
        var mount = MountFor(kind);
        if (string.IsNullOrWhiteSpace(relativeOrFull)) return mount;

        var trimmed = relativeOrFull.Trim().Replace('\\', '/');

        if (trimmed.Equals(mount, StringComparison.OrdinalIgnoreCase))
            return mount;
        var prefix = mount.EndsWith('/') ? mount : mount + "/";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return trimmed;

        // Preserve legacy absolute paths so their on-disk location doesn't shift
        // on a re-save. LocalTransferService.Resolve() accepts both forms.
        if (trimmed.Length > 1 && trimmed.StartsWith('/'))
            return trimmed;

        return $"{mount}/{trimmed}";
    }

    /// <summary>
    /// Returns a host-side hint for a subfolder under the given mount, formatted for help
    /// text. When the host env var is set, returns "{HOST_DIR}/{subfolder}". When it isn't,
    /// returns null so callers can fall back to generic copy.
    /// </summary>
    public string? HostHint(string subfolder, MountKind kind)
    {
        var host = HostFor(kind);
        if (host is null) return null;
        var clean = (subfolder ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        return string.IsNullOrEmpty(clean)
            ? host.TrimEnd('/')
            : $"{host.TrimEnd('/')}/{clean}";
    }

    private string MountFor(MountKind kind) => kind switch
    {
        MountKind.Source => SourceMount,
        MountKind.Destination => DestinationMount,
        MountKind.Restore => RestoreMount,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private string? HostFor(MountKind kind) => kind switch
    {
        MountKind.Source => SourceHost,
        MountKind.Destination => DestinationHost,
        MountKind.Restore => RestoreHost,
        _ => null
    };

    private static string NormalizeMount(string mount)
    {
        var n = mount.Replace('\\', '/').TrimEnd('/');
        return string.IsNullOrEmpty(n) ? "/" : n;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
