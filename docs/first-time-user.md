# First-Time User Guide

This guide walks you through everything from generating an SSH key to running your first backup. It covers common pitfalls and explains the path system in detail so nothing surprises you mid-setup.

> If you haven't set up your directory layout and `docker-compose.yml` yet, start with [Installation & Setup](installation.md) and come back here.

---

## Before you begin — checklist

- [ ] Docker and Docker Compose are installed and working (`docker compose version` returns a version)
- [ ] You have SSH access to the machine that will store your backups
- [ ] You know the path on that machine where backups should land
- [ ] You have Docker volume mounts configured for the data you want to back up

If any of these are unclear, the sections below cover each one.

---

## Generating an SSH key

HomelabBackup authenticates to your remote server using an SSH private key. Ed25519 is recommended — it produces short keys and is widely supported.

### Linux / macOS

Open a terminal:

```bash
ssh-keygen -t ed25519 -C "homelab-backup"
```

When prompted:
- **File location**: press Enter to accept the default (`~/.ssh/id_ed25519`), or specify a custom path like `~/.ssh/homelab_backup`
- **Passphrase**: optional but recommended. If you set one, see [Passphrase-protected keys](#passphrase-protected-keys) below.

This creates two files:
```
~/.ssh/id_ed25519        ← private key  (keep this secret, never share it)
~/.ssh/id_ed25519.pub    ← public key   (this goes on the server)
```

### Windows (PowerShell)

```powershell
ssh-keygen -t ed25519 -C "homelab-backup"
```

Default location is `C:\Users\YourName\.ssh\id_ed25519`. You can accept the default or specify another path.

> **Windows note:** If `ssh-keygen` is not found, enable the OpenSSH Client optional feature:
> Settings → System → Optional Features → Add a Feature → OpenSSH Client.

---

## Authorizing the key on the remote server

The **public key** (`.pub` file) must be added to the `~/.ssh/authorized_keys` file on the remote server under the user account HomelabBackup will connect as.

### Option A — using `ssh-copy-id` (Linux/macOS only)

```bash
ssh-copy-id -i ~/.ssh/id_ed25519.pub backup-user@192.168.1.50
```

This appends the public key automatically and sets permissions correctly.

### Option B — manual (any OS)

1. Print the public key:
   ```bash
   cat ~/.ssh/id_ed25519.pub
   ```
   Copy the entire output — it's one long line starting with `ssh-ed25519`.

2. On the remote server, append it to `authorized_keys`:
   ```bash
   mkdir -p ~/.ssh
   chmod 700 ~/.ssh
   echo "ssh-ed25519 AAAA...your key here..." >> ~/.ssh/authorized_keys
   chmod 600 ~/.ssh/authorized_keys
   ```

### Verify it works

Before touching HomelabBackup at all, confirm you can log in key-only:

```bash
ssh -i ~/.ssh/id_ed25519 backup-user@192.168.1.50
```

If this opens a shell without a password prompt, you're ready. If it still asks for a password, the key isn't authorized yet — check the `authorized_keys` file and permissions.

### Passphrase-protected keys

If your key has a passphrase, HomelabBackup needs it at startup. Create a `.env` file in the same directory as your `docker-compose.yml`:

```bash
BACKUP_KEY_PASSPHRASE=your_passphrase_here
```

Docker Compose picks this up automatically. Do not wrap the value in quotes.

---

## Understanding destination paths

`destination.path` in `backup.yml` is the path **on the remote server** where archives are stored. What this looks like depends on the operating system of that server.

### Linux / NAS (Synology, TrueNAS, Ubuntu, etc.)

Use a standard Unix absolute path:

```yaml
destination:
  path: /backups/homelab
```

On a Synology NAS, shared folders live under `/volume1/`. For example, a shared folder called `backup` would be:

```yaml
destination:
  path: /volume1/backup/homelab
```

**The directory must exist.** HomelabBackup will not create it. Log into the server and run:

```bash
mkdir -p /backups/homelab
```

### Windows SFTP server (OpenSSH for Windows)

Windows paths use drive letters (`C:\`, `D:\`), but SFTP uses forward slashes and a special format to address them.

With **Windows OpenSSH Server**, drives are accessible as:

```
/c:/path/to/folder      ← C: drive
/d:/path/to/folder      ← D: drive
/b:/backups/homelab     ← B: drive
```

Example — backing up to `D:\Backups\Homelab` on a Windows server:

```yaml
destination:
  path: /d:/Backups/Homelab
```

**How to find the right path:**

1. Open a terminal and connect via SFTP:
   ```bash
   sftp -i ~/.ssh/id_ed25519 backup-user@192.168.1.50
   ```
2. Type `pwd` to see where you land by default.
3. Navigate to the folder you want:
   ```
   sftp> cd /d:/Backups
   sftp> ls
   ```
4. Whatever path `pwd` shows after `cd`ing there is what goes in `destination.path`.

> **Case sensitivity:** Linux paths are case-sensitive (`/backups` ≠ `/Backups`). Windows paths are not, but use whatever casing the folder actually has to avoid confusion.

---

## Understanding source paths (host → container)

This is the most common source of confusion for new users.

HomelabBackup runs inside a Docker container. It cannot see your host filesystem directly. You must **mount** the directories you want to back up into the container, then reference the **container-side path** in `backup.yml`.

### How the mapping works

In `docker-compose.yml`, a volume mount looks like:

```yaml
volumes:
  - /host/path/to/your/data:/data:ro
```

The left side is the path **on your machine**. The right side is where it appears **inside the container**. The `:ro` flag means read-only (recommended for backup sources).

In `backup.yml`, you always reference the right side:

```yaml
sources:
  - name: media
    path: /data/media       # ← container path, not host path
```

### Multiple sources from different locations

If your data is spread across multiple directories, add more volume mounts:

```yaml
# docker-compose.yml
volumes:
  - ./config:/config
  - ./keys:/keys:ro
  - /mnt/media:/data/media:ro          # media library
  - /home/chad/documents:/data/docs:ro # documents
  - ./logs:/logs
```

```yaml
# backup.yml
sources:
  - name: media
    path: /data/media
  - name: docs
    path: /data/docs
```

### Windows host paths in Docker Compose

If HomelabBackup itself is running on a Windows machine (unusual but supported), use forward slashes or the Docker Desktop format:

```yaml
volumes:
  - C:/Users/chad/Documents:/data/docs:ro
  - D:/Media:/data/media:ro
```

---

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

---

## Running your first backup (dry run)

Before transferring any data, do a dry run to confirm everything looks right.

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

---

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

---

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

---

## Common problems

**"Connection refused" or timeout when running `list`**
The container cannot reach the SFTP server. Verify the `host` and `port` in `backup.yml` and confirm the server is running. If HomelabBackup is running on the same machine as the SFTP server, use the host's LAN IP instead of `127.0.0.1` — the container has its own network namespace.

**"Permission denied (publickey)"**
The SSH key isn't authorized on the server. Re-check `~/.ssh/authorized_keys` on the remote and confirm the file permissions are `600`. Also confirm `key_path` in `backup.yml` points to the right file under `/keys/`.

**"No such file or directory" for a source path**
The path in `sources[].path` doesn't match what's mounted in the container. Open `docker-compose.yml`, find the volume mounts, and make sure the container-side path matches exactly.

**"No such file or directory" for the destination**
The destination directory doesn't exist on the remote server. Create it manually via SSH or SFTP before running a backup.

**Archives are being created on the wrong drive (Windows SFTP)**
Check the destination path format. Use `/d:/Backups/Homelab` (lowercase drive letter, leading slash, colon after letter). Connect via `sftp` manually and `pwd` to confirm you're in the right place.
