# Creating a Backup

## What happens during a backup

1. **Scan** — walks the source directory, applies exclude patterns
2. **Compress** — creates a ZIP archive in a temporary directory using the configured compression level
3. **Transfer** — uploads the archive to the remote SFTP server
4. **Verify** — computes SHA256 of the local archive and the uploaded file; retries up to 2 times on mismatch
5. **Manifest** — uploads a `.manifest.json` file alongside the archive
6. **Cleanup** — removes the temp directory

If verification fails after 2 retries the backup is marked as failed and the corrupted remote file is deleted. The source directory is never modified.

## Triggering a backup from the Web UI

1. Navigate to **Backup Sources** (`/sources`)
2. Click **Backup** next to the source you want to back up
   - Or click **Backup All** to queue all sources
3. A confirmation modal appears for "Backup All"; individual backups start immediately
4. Watch the progress bar — it moves through Scanning → Compressing → Transferring → Verifying
5. A toast notification confirms success or failure when the run finishes

You can also click **Dry Run** first to verify what will be included and how large the archive will be before committing to an upload.

## Triggering a backup from the CLI

```bash
# Back up all sources
docker compose --profile cli run --rm backup-cli backup

# Back up a single source
docker compose --profile cli run --rm backup-cli backup --source media

# Dry run (no upload)
docker compose --profile cli run --rm backup-cli backup --dry-run
docker compose --profile cli run --rm backup-cli backup --source configs --dry-run
```

The CLI prints a summary table when the run completes:

```
Source    Status    Files    Compressed    Verified    Duration    Retries
media     Success   1,842    2.3 GB        ✓           4m 12s      0
```

## Scheduled backups

Set a cron expression in `backup.yml` to have the web service run backups automatically:

```yaml
schedule:
  cron: "0 2 * * *"    # every day at 2:00 AM UTC
```

Common expressions:

| Expression | Meaning |
|------------|---------|
| `0 2 * * *` | Daily at 2:00 AM |
| `0 */6 * * *` | Every 6 hours |
| `0 2 * * 0` | Weekly on Sunday at 2:00 AM |
| `0 2 1 * *` | Monthly on the 1st at 2:00 AM |

All times are UTC. After saving a new cron expression via the Config page, the scheduler picks it up immediately — the next run is shown on the Dashboard.

To disable scheduling without removing the expression, set `cron` to `null` or remove the `schedule` block entirely.

## Managing retention

Retention is not applied automatically after each backup. Run it manually when you want to clean up old archives.

```bash
# Preview what would be deleted (safe — default is dry run)
docker compose --profile cli run --rm backup-cli retention

# Actually delete old archives
docker compose --profile cli run --rm backup-cli retention --dry-run=false
```

The retention policy is defined in `backup.yml`:

```yaml
retention:
  keep_last: 7        # always keep the 7 most recent archives per source
  max_age_days: 30    # delete archives older than 30 days (unless protected by keep_last)
```

Both rules apply together: `keep_last` acts as a floor (even archives older than `max_age_days` are kept if they are among the N most recent).

## Checking backup status

**Web UI:** The Dashboard shows the last backup time, status, and size for each source. The Archives page lists every archive stored on the remote.

**CLI:**
```bash
# List all archives
docker compose --profile cli run --rm backup-cli list

# Filter by source
docker compose --profile cli run --rm backup-cli list --source media
```

Output:
```
Source    Archive                        Created (UTC)         Size       Files
media     media_20250320_020000.zip      2025-03-20 02:00      2.3 GB     1,842
media     media_20250319_020000.zip      2025-03-19 02:00      2.3 GB     1,841
```
