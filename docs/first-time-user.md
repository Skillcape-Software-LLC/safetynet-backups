# First-Time User Guide

This guide assumes you have completed [Installation & Setup](installation.md).

## What HomelabBackup does

Each backup run produces a ZIP archive per source. Archives are uploaded to your remote SFTP server and verified with a SHA256 checksum before the run is considered complete. A manifest JSON file is stored alongside each archive and tracks metadata (file list, sizes, timestamp). Retention cleanup is a separate, manual step.

Archive files follow this naming pattern:
```
{sourceName}_{yyyyMMdd_HHmmss}.zip
```

On the remote server they are organized into subdirectories by source name:
```
/backups/homelab/
├── media/
│   ├── media_20250315_020000.zip
│   └── media_20250315_020000.zip.manifest.json
└── configs/
    ├── configs_20250315_020001.zip
    └── configs_20250315_020001.zip.manifest.json
```

## Web UI orientation

Open [http://localhost:5200](http://localhost:5200) after starting the web service.

### Dashboard

The dashboard gives you a health summary at a glance:

- **Sources** — how many sources are configured
- **Last Status** — OK or Failed based on the most recent backup run
- **Next Run** — calculated from your cron schedule, or "One-shot" if no schedule is set
- **Source Status table** — last backup time, status badge, compressed size, duration, and a verification checkmark for each source

If a source shows "Never", no backup has run for it yet.

### Backup Sources (`/sources`)

This is where you trigger backups manually and watch live progress. Each source has two buttons:

- **Backup** — runs a real backup (uploads and verifies)
- **Dry Run** — scans and compresses but does not upload anything; useful for checking what will be backed up and how large the archive will be

The progress bar shows the current phase (Scanning → Compressing → Transferring → Verifying) and a file counter.

**Backup All** queues all sources in sequence. You will be shown a confirmation modal first.

### Remote Archives (`/archives`)

Lists every archive stored on the remote server, grouped by source. This page connects to SFTP each time it loads — hit **Refresh** if you want the latest listing.

From here you can restore or delete individual archives.

### Configuration (`/config`)

A form-based editor for `backup.yml`. Changes take effect on the next backup run. After saving, the running web service picks up the new config automatically — no restart needed for most settings.

The **Validate** button checks the config without saving. The **Show Diff** button shows a side-by-side comparison of current vs pending YAML before you commit a change.

### Logs (`/logs`)

A live log viewer. Useful for diagnosing connection failures, understanding what happened during a run, or confirming a scheduled backup fired. You can filter by log level (Info / Warning / Error) or by logger name.

## CLI orientation

All CLI commands take the config path from the `CONFIG_PATH` environment variable (set automatically by Docker Compose).

```bash
# List all archives on the remote server
docker compose --profile cli run --rm backup-cli list

# List archives for a single source
docker compose --profile cli run --rm backup-cli list --source media

# Show what a backup would do without uploading
docker compose --profile cli run --rm backup-cli backup --dry-run

# Back up all sources
docker compose --profile cli run --rm backup-cli backup

# Back up a single source
docker compose --profile cli run --rm backup-cli backup --source configs
```

## Running your first backup (dry run)

Before transferring any data, do a dry run to confirm everything looks right:

**Web UI:** Go to **Backup Sources**, click **Dry Run** next to a source, and watch the progress panel.

**CLI:**
```bash
docker compose --profile cli run --rm backup-cli backup --dry-run
```

A dry run reports:
- How many files will be included
- The estimated compressed size
- Which files would be excluded

If the output looks correct, proceed with a real backup.
