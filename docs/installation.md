# Installation & Setup

HomelabBackup runs as a Docker container. There is no installer — you configure a directory, place your SSH key, and run the container.

## Prerequisites

- Docker and Docker Compose installed on the host
- An SSH private key with access to the remote SFTP server (Ed25519 or RSA)
- A writable destination directory on the remote server

## Directory Layout

Create a working directory for HomelabBackup. It needs three subdirectories:

```
homelab-backup/
├── config/
│   └── backup.yml        ← your configuration
├── keys/
│   └── id_ed25519        ← your SSH private key (chmod 600)
└── logs/                 ← log output (created automatically)
```

## 1. Copy the compose file

Copy `docker-compose.yml` from this repository into your working directory. The compose file references `./config`, `./keys`, and `./logs` as relative paths, so it must live alongside those directories.

## 2. Create your configuration

Copy `config/backup.yml` from this repository to your working directory and edit it:

```yaml
ssh:
  host: 192.168.1.50        # IP or hostname of your SFTP server
  port: 22
  user: backup-user
  key_path: /keys/id_ed25519  # path inside the container — always /keys/<filename>

sources:
  - name: media
    path: /data/media         # path inside the container (mounted from host)
    exclude:
      - "*.tmp"
      - ".cache"
  - name: configs
    path: /data/configs

destination:
  path: /backups/homelab      # remote path on the SFTP server

retention:
  keep_last: 7
  max_age_days: 30

compression: optimal          # optimal | fastest | no_compression

schedule:
  cron: "0 2 * * *"          # remove or set to null for one-shot mode
```

Key points:
- `key_path` is always a path **inside the container** — use `/keys/your_key_name`
- `sources[].path` is also a container path — map your host data directory to `/data` in the compose file
- Source names must be unique; they become the remote subdirectory names

## 3. Place your SSH key

Copy your private key into `./keys/`:

```bash
cp ~/.ssh/id_ed25519 ./keys/id_ed25519
chmod 600 ./keys/id_ed25519
```

If your key has a passphrase, create a `.env` file next to `docker-compose.yml`:

```bash
BACKUP_KEY_PASSPHRASE=your_passphrase_here
```

## 4. Update volume mounts

Open `docker-compose.yml` and find the data volume for your service. Replace the example host path with the actual path to the data you want to back up:

```yaml
volumes:
  - ./config:/config
  - ./keys:/keys:ro
  - /path/to/your/data:/data:ro   # ← change this
  - ./logs:/logs
```

If you are backing up multiple unrelated directories, add additional bind mounts and use distinct paths inside the container.

## 5. Verify connectivity (optional)

Before running a full backup, confirm the container can reach your SFTP server:

```bash
docker compose run --rm backup-cli list
```

If you see a table of archives (or an empty table with no error), the SSH connection is working.

## Web UI vs CLI

| Mode | When to use |
|------|-------------|
| **Web** (`backup-web`) | Persistent service, scheduled backups, interactive management |
| **CLI** (`backup-cli`) | Scripted/cron-triggered runs, one-shot backups, automation |

Start the web service:

```bash
docker compose up -d backup-web
```

Then open [http://localhost:5200](http://localhost:5200).

The web service runs indefinitely and respects the `schedule.cron` value in `backup.yml`. If no cron is set, backups only run when triggered from the UI.
