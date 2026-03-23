# Restoring a Backup

Restoration downloads an archive from the remote SFTP server and extracts it to a local path. The source directory is never read during a restore — only the remote archive is used.

## What happens during a restore

1. Connects to the remote SFTP server
2. Locates the target archive (latest for the source, or a specific filename)
3. Downloads the archive to a temporary directory
4. Extracts the ZIP contents to the destination path you specify
5. Cleans up the temp directory

## Restoring from the Web UI

1. Navigate to **Remote Archives** (`/archives`)
2. Find the archive you want to restore (archives are grouped by source, newest first)
3. Click **Restore** on the row
4. In the modal that appears, enter the **local destination path** — the path inside the container where files will be extracted
5. Click **Restore** to confirm

> **Note:** The destination path is a path inside the container. If you want files to land on the host, the path must be within a mounted volume. Map a host directory to `/restore` (or any path) in `docker-compose.yml` and use that path.

Example compose addition:
```yaml
volumes:
  - /host/restore-target:/restore
```

Then enter `/restore` as the destination in the modal.

## Restoring from the CLI

**Restore the latest backup for a source:**
```bash
docker compose run --rm backup-cli restore \
  --source media \
  --dest /restore
```

**Restore a specific archive by filename:**
```bash
docker compose run --rm backup-cli restore \
  --file media_20250315_020000.zip \
  --dest /restore
```

Use `--source` when you want the most recent archive. Use `--file` when you need a specific point-in-time. You cannot specify both at the same time.

To find available archive filenames:
```bash
docker compose run --rm backup-cli list --source media
```

## Choosing a destination path

The `--dest` path (or the modal field) is where files are extracted **inside the container**. Plan your mounts accordingly:

| Scenario | Host path | Container mount | --dest value |
|----------|-----------|-----------------|--------------|
| Restore to original location | `/host/data/media` | `/data/media` | `/data/media` |
| Restore to a staging directory | `/host/restore` | `/restore` | `/restore` |
| Restore to a new location | `/host/new-media` | `/new-media` | `/new-media` |

Restoring to the original source path will overwrite existing files with the archive contents. If you want to compare before overwriting, restore to a staging directory first.

## After restoring

The restore extracts files exactly as they were when the backup was created. Excluded files (from the `exclude` patterns in your config) were never included in the archive, so they will not be present in the restored output.

Check the Logs page (Web UI) or the CLI output for the restore duration and any errors encountered during extraction.
