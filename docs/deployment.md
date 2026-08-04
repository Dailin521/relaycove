# Linux server deployment

This guide installs one RelayCove Server instance on Linux x64 from an M4 release archive. It is an operational procedure, not a claim that this repository has run on a VPS.

## Status and boundaries

- `已验证`：the Server reads `ConnectionStrings:Default`, `Storage:UploadsPath`, `Uploads:MaximumFileBytes`, `Authentication:*`, and `BootstrapAdmin:*`; it does not automatically run EF migrations at startup.
- `已验证`：the upload endpoint has an absolute request limit of 100 MiB plus 64 KiB multipart overhead (102,464 KiB). `Uploads:MaximumFileBytes` remains an application-level file limit and must not exceed 100 MiB.
- `未验证`：Linux x64 execution, systemd hardening, Nginx/TLS behaviour, certificate renewal, firewalling, backup/restore, VPS sizing, real-client login, and dual-client recovery. Perform those M5 checks in the intended environment; do not treat Windows offline package validation as a Linux deployment test.
- `未验证`：this release slice does not package the WPF client, updater, installer, update manifest, GitHub Release, or a production deployment.

The release archive has the root `RelayCove.Server-<version>-linux-x64/` and contains `app/`, `migrate/`, `deploy/`, and a manifest. Verify its SHA-256 and manifest with the release verification command before copying anything to a server.

## Prepare the host

The following commands require an administrator account. They are examples for a systemd-based Linux distribution; adapt package-manager and Nginx site-enable commands to the distribution. Replace `chat.example.com` and the version with the approved DNS name and the verified package version.

```sh
sudo useradd --system --user-group --home-dir /var/lib/relaycove --shell /usr/sbin/nologin relaycove
sudo install -d -o relaycove -g relaycove -m 0700 /var/lib/relaycove
sudo install -d -o root -g relaycove -m 0750 /var/lib/relaycove/updates
sudo install -d -o root -g root -m 0755 /opt/relaycove/releases /etc/relaycove
sudo install -d -o root -g root -m 0755 /var/www/certbot
```

Install and configure Nginx and a TLS certificate with the host's approved package and certificate process. Do not expose Kestrel directly to the Internet: the supplied unit binds it only to `127.0.0.1:5080`; Nginx owns the public HTTPS listener.

## Verify and install a release

On the build workstation, run the repository release verifier against the exact archive and sidecar. It is an offline integrity check and must succeed before transfer:

```powershell
pwsh ./scripts/verify-server-release.ps1 -Version <version>
```

Transfer only the verified archive and its `.sha256` sidecar to the host. On the host, place both files in the same directory and verify the sidecar before extraction. The sidecar names the archive, so this command must run from that directory:

```sh
sha256sum -c RelayCove.Server-<version>-linux-x64.tar.gz.sha256
```

Extract only after that command reports `OK`, and always use a new versioned directory; never overwrite the active release in place.

```sh
set -eu

sudo install -d -o root -g root -m 0755 /opt/relaycove/releases/<version>
sudo tar --extract --file RelayCove.Server-<version>-linux-x64.tar.gz \
  --directory /opt/relaycove/releases/<version> --no-same-owner --no-same-permissions
release_root=/opt/relaycove/releases/<version>/RelayCove.Server-<version>-linux-x64
sudo chown -R root:root /opt/relaycove/releases/<version>
sudo find "$release_root" -type d -exec chmod 0755 {} +
sudo find "$release_root" -type f -exec chmod 0644 {} +
sudo chmod 0755 "$release_root/app/RelayCove.Server" "$release_root/migrate/RelayCove.Migrations"
```

Do not change `/opt/relaycove/current` yet. Prepare and validate the new version by its explicit release path; the migration procedure changes the active link atomically only after the migration succeeds.

The release scripts own the archive format and migration-bundle filename. Before the atomic switch, use the executable under the new version's explicit release path; use `current` only after that switch succeeds. Do not substitute `dotnet ef` or run migrations from the application process.

## Configure without committing secrets

Copy `deploy/appsettings.Production.example.json` from the new version's explicit release path to its `app/appsettings.Production.json`, then replace `REPLACE_WITH_PACKAGE_VERSION` and `chat.example.com`. Keep `Update:ManifestPath` at the supplied writable state path, `/var/lib/relaycove/updates/manifest.json`; do not put the live update manifest in the immutable release tree. This application loads production settings from its content root, so placing this file only in `/etc/relaycove` has no effect. Keep the release tree root-owned and only update this file as part of an intentional release configuration change.

```sh
set -eu

release_root=/opt/relaycove/releases/<version>/RelayCove.Server-<version>-linux-x64
sudo install -o root -g relaycove -m 0640 \
  "$release_root/deploy/appsettings.Production.example.json" \
  "$release_root/app/appsettings.Production.json"
sudoedit "$release_root/app/appsettings.Production.json"
sudo install -o root -g root -m 0600 \
  "$release_root/deploy/relaycove.env.example" \
  /etc/relaycove/relaycove.env
sudoedit /etc/relaycove/relaycove.env
```

Set `Authentication__SigningKey` in the environment file to a new Base64 value that decodes to at least 32 bytes. For example, generate it in a secure administrator session with `openssl rand -base64 48`; do not put its output in shell history, tickets, source control, logs, or the JSON configuration. The system manager reads `/etc/relaycove/relaycove.env`; the service account does not need to open that file after systemd passes the value to the process.

`BootstrapAdmin__Enabled` must stay `false` and the three bootstrap credential variables must be absent on normal starts. For the one permitted first-empty-database bootstrap, set all four variables immediately before the first service start. After the account is created, stop the service, remove the username, display name, and password variables, set `BootstrapAdmin__Enabled=false`, and start it again. A non-empty database is never modified by bootstrap.

## Install systemd and Nginx

Copy the supplied templates, replace the DNS and certificate paths in the Nginx file, then validate before reload. `client_max_body_size 102464k` is exactly the endpoint's 100 MiB plus 64 KiB ceiling; it does not relax the application's own multipart or configured file checks.

```sh
set -eu

release_root=/opt/relaycove/releases/<version>/RelayCove.Server-<version>-linux-x64
sudo install -o root -g root -m 0644 "$release_root/deploy/relaycove.service" /etc/systemd/system/relaycove.service
sudo install -o root -g root -m 0644 "$release_root/deploy/nginx.conf" /etc/nginx/conf.d/relaycove.conf
sudo nginx -t
sudo systemctl enable nginx
if sudo systemctl is-active --quiet nginx; then
  sudo systemctl reload nginx
else
  sudo systemctl start nginx
fi
sudo systemctl daemon-reload
sudo systemctl enable relaycove.service
```

The Nginx template uses a standard HTTP-to-HTTPS redirect, explicit TLS certificate paths, a loopback upstream, and WebSocket upgrade headers only where needed for `/hubs/chat`. It disables the access log for that location because SignalR WebSocket/SSE requests can carry `access_token` in the query string; do not re-enable a request-target log there. Its `map` directive must be included from Nginx's `http {}` context, as is normal for `conf.d`; do not paste it inside a `server {}` block. The Server accepts one forwarded hop only from loopback, so authentication rate limits remain partitioned by the real client address without trusting public `X-Forwarded-*` input.

## First migration and start

Migrations are an explicit maintenance action. The safe order is **stop → consistent backup → migration → start**. Do not run two service instances against the same SQLite database, and do not attempt an online migration.

```sh
set -eu

sudo systemctl stop relaycove.service
sudo systemctl is-active --quiet relaycove.service && exit 1

# While the service is stopped, stage the database, any present SQLite sidecars,
# and uploads together. Publish the backup only after its integrity file is complete.
stamp=$(date -u +%Y%m%dT%H%M%SZ)
backup_root=/var/backups/relaycove/$stamp
backup_staging=/var/backups/relaycove/.$stamp.staging
sudo test ! -e "$backup_root"
sudo test ! -e "$backup_staging"
sudo install -d -o root -g root -m 0700 "$backup_staging"
sudo test ! -e /var/lib/relaycove/relaycove.db || sudo cp -a /var/lib/relaycove/relaycove.db "$backup_staging/"
sudo test ! -e /var/lib/relaycove/relaycove.db-wal || sudo cp -a /var/lib/relaycove/relaycove.db-wal "$backup_staging/"
sudo test ! -e /var/lib/relaycove/relaycove.db-shm || sudo cp -a /var/lib/relaycove/relaycove.db-shm "$backup_staging/"
sudo test ! -d /var/lib/relaycove/uploads || sudo cp -a /var/lib/relaycove/uploads "$backup_staging/"
sudo touch "$backup_staging/BACKUP.CONTENTS"
sudo sh -c "cd '$backup_staging' && find . -type f ! -name BACKUP.SHA256 ! -name BACKUP.COMPLETE -print0 | sort -z | xargs -0 sha256sum > BACKUP.SHA256"
sudo touch "$backup_staging/BACKUP.COMPLETE"
sudo mv -T "$backup_staging" "$backup_root"

release_root=/opt/relaycove/releases/<version>/RelayCove.Server-<version>-linux-x64
sudo -H -u relaycove env \
  ConnectionStrings__Default='Data Source=/var/lib/relaycove/relaycove.db;Foreign Keys=True;Default Timeout=5' \
  "$release_root/migrate/RelayCove.Migrations" \
  --connection 'Data Source=/var/lib/relaycove/relaycove.db;Foreign Keys=True;Default Timeout=5'

# Switch the active release only after a successful migration. The temporary link
# is on the same filesystem, so mv replaces the old symlink atomically.
sudo ln -sfn "$release_root" /opt/relaycove/current.next
sudo mv -Tf /opt/relaycove/current.next /opt/relaycove/current
sudo systemctl start relaycove.service
sudo systemctl status --no-pager relaycove.service
```

The backup is consistent only because the service is stopped before copying the database, its possible `-wal`/`-shm` sidecars, and the uploads directory as one stopped-service set. On an initial empty host these items do not yet exist, so the backup directory is intentionally empty before the first bundle creates the database. Preserve every non-empty backup until the release has passed the intended environment's acceptance checks. The migration bundle is self-contained and single-file, so native libraries can be extracted at execution. `sudo -H -u relaycove` sets `HOME=/var/lib/relaycove`, the service-owned `0700` state directory prepared above, rather than allowing an administrator home or arbitrary temporary location to become the extraction location. It also keeps database, uploads, and extraction state owned by the service identity.

## Upgrade and failure recovery

For every upgrade, first verify the new archive, install and configure it under its new versioned path without changing `current`, then repeat **stop → consistent backup → migrate with the new version's explicit path → atomically switch `current` → start**. Keep the prior active link target, release directory, and stopped-service backup until acceptance is complete.

If archive verification, configuration validation, migration, Nginx validation, or service startup fails:

1. Keep `relaycove.service` stopped; do not retry by running the application against a partly migrated database.
2. Record `journalctl -u relaycove.service` output without copying environment values or secrets.
3. If migration failed, note that `current` still identifies the prior release; do not start it against a possibly changed database until an operator has selected and restored a known-good stopped-service backup when restoration is required.
4. Restore the database and uploads as one exact stopped-service set: first quarantine the current uploads directory and remove the current `relaycove.db`, `relaycove.db-wal`, and `relaycove.db-shm`, then copy back each item that exists in the selected backup. A database sidecar or uploads directory absent from the backup must also be absent from the restored state.
5. Retain or atomically repoint `/opt/relaycove/current` to the selected known-good release, and re-check its configuration and permissions before starting.

Example exact-set restore after selecting the backup deliberately:

```sh
set -eu
backup_root=/var/backups/relaycove/<selected-stamp>
restore_hold=/var/backups/relaycove/pre-restore-$(date -u +%Y%m%dT%H%M%SZ)
sudo systemctl stop relaycove.service
sudo test -d "$backup_root"
sudo test -f "$backup_root/BACKUP.COMPLETE"
sudo test -f "$backup_root/BACKUP.SHA256"
sudo sh -c "cd '$backup_root' && sha256sum -c BACKUP.SHA256"
sudo install -d -o root -g root -m 0700 "$restore_hold"
for state_item in relaycove.db relaycove.db-wal relaycove.db-shm uploads; do
  if sudo test -e "/var/lib/relaycove/$state_item"; then
    sudo mv "/var/lib/relaycove/$state_item" "$restore_hold/$state_item"
  fi
done
for state_item in relaycove.db relaycove.db-wal relaycove.db-shm uploads; do
  if sudo test -e "$backup_root/$state_item"; then
    sudo cp -a "$backup_root/$state_item" "/var/lib/relaycove/$state_item"
    sudo chown -R relaycove:relaycove "/var/lib/relaycove/$state_item"
  fi
done
```

There is no automatic migration rollback, automatic database restore, or multi-instance SQLite failover in this release slice. Treat a failed migration as an operator decision, not as a reason to start the service with uncertain state.

## Operational checks

After the service and Nginx are running, use host-approved monitoring to check `systemctl status relaycove.service`, `journalctl -u relaycove.service`, Nginx error logs, certificate expiry, disk capacity under `/var/lib/relaycove`, and backup restore drills. Do not log the signing key, bootstrap credentials, Authorization headers, tokens, upload contents, or private attachment paths.

The service can read but cannot write `/var/lib/relaycove/updates`; only an administrator publishes updates. The manifest generator does not copy the Client ZIP. Use same-filesystem temporary names, verify the ZIP, publish it first, and atomically replace `manifest.json` last:

```sh
set -eu
update_root=/var/lib/relaycove/updates
artifact_name=RelayCove.Client-<version>-win-x64.zip
artifact_source=/root/relaycove-staging/$artifact_name
manifest_source=/root/relaycove-staging/manifest.json
expected_artifact_sha256=<verified-lowercase-sha256>

sudo install -o root -g relaycove -m 0640 "$artifact_source" "$update_root/.$artifact_name.next"
actual_artifact_sha256=$(sudo sha256sum "$update_root/.$artifact_name.next" | cut -d ' ' -f 1)
test "$actual_artifact_sha256" = "$expected_artifact_sha256"
sudo mv -Tf "$update_root/.$artifact_name.next" "$update_root/$artifact_name"
sudo install -o root -g relaycove -m 0640 "$manifest_source" "$update_root/.manifest.json.next"
sudo mv -Tf "$update_root/.manifest.json.next" "$update_root/manifest.json"
```

Never publish a manifest that names an absent or partially transferred artifact.

Before declaring deployment ready, perform the M5 Linux/VPS gate in the real target environment: HTTPS and certificate renewal, public firewall rules, Nginx WebSocket reconnects, service restart recovery, database backup and restore, upload limit behaviour, real-client login, and two-client authorization/revocation behaviour. These remain `未验证` for this Windows/offline M4 package work.
