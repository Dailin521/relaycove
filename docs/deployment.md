# RelayCove.Web Deployment

## Fixed entrances

| Purpose | Entrance |
|---|---|
| daily local fixture | `http://127.0.0.1:5173/?fixture=chat` |
| large-version server acceptance | `https://hklight.2000521.xyz/relaycove-web/` |
| official Zulip Web | `https://hklight.2000521.xyz/` — unchanged |
| legacy RelayCove service | `https://hklight.2000521.xyz/relaycove/` — unchanged |

`RelayCove.Web` uses a separate `/relaycove-web/` prefix because `/relaycove/` is still owned by the legacy service, admin and update routes. The new path is served as static files on the same HTTPS origin as Zulip, so browser API calls remain direct and same-origin; no RelayCove server, BFF or proxy protocol is introduced.

## Daily local verification

Double-click the repository-root `start-web-dev.cmd`. The tool:

1. checks that the repository-local Vite dependency exists;
2. starts `npm run dev` in a visible console;
3. waits until the RelayCove page is actually reachable;
4. opens the fixture URL in the default browser.

Use `Ctrl+C` in that console to stop Vite. Daily edits stay local and are not automatically synchronized to the server.

## Large-version one-click deployment

Double-click the repository-root `deploy-web.cmd`, or run:

```powershell
pwsh ./scripts/deploy-web.ps1
```

The deployment tool deliberately:

1. runs `npm run verify:full` (typecheck, unit tests, fixture browser tests, production subpath build and deployment-preview browser test);
2. creates a timestamped release archive under ignored `artifacts/web/deploy/`;
3. uploads only `dist/`, never source, `.env`, credentials, `node_modules` or test artifacts;
4. verifies the archive SHA-256 and contents on the host;
5. installs into `/opt/relaycove-web/releases/<release>/relaycove-web/`;
6. atomically switches `/opt/relaycove-web/current`;
7. verifies Nginx, the HTTPS HTML and the hashed JavaScript asset;
8. restores the previous `current` link if the smoke check fails.

The double-click wrapper opens the fixed HTTPS entrance in the default browser after a successful deployment, ready for manual acceptance.

No release cleanup runs automatically. Keeping earlier hashed assets makes rollback and clients holding an older HTML document safe. A deliberate rollback uses:

```powershell
pwsh ./scripts/rollback-web.ps1 -ReleaseId <release-id>
```

The private server connection is resolved through the host-key-pinned helpers in `server-admin`. Override its checkout only with `-ServerAdminRoot` or `RELAYCOVE_SERVER_ADMIN_ROOT`; credentials are never copied into RelayCove.

## One-time host provisioning

The Nginx static locations and HTTP security headers are installed once with:

```powershell
pwsh ./scripts/provision-web-host.ps1
```

Provisioning backs up the current hklight site configuration under `/var/backups/relaycove-web/nginx/<UTC timestamp>/`, inserts one exact include before the existing Zulip root proxy, runs `nginx -t`, restores the backup on failure and only then reloads Nginx.

Runtime paths:

- `/etc/nginx/snippets/relaycove-web-locations.conf`;
- `/etc/nginx/snippets/relaycove-web-security-headers.conf`;
- `/opt/relaycove-web/releases/`;
- `/opt/relaycove-web/current`;
- `/opt/relaycove-web/incoming/`.

The asset location returns 404 for a missing hashed asset instead of falling back to HTML. `index.html` is revalidated; hashed assets are immutable for one year. CSP, HSTS, nosniff, frame denial, referrer and permissions policies are emitted as HTTP response headers.

## Security and capability boundary

- The Web API key remains browser-local by the confirmed private-realm product decision. Browser storage is origin-scoped, not path-scoped: official Zulip and RelayCove.Web share an origin, so the same-origin XSS risk is explicitly accepted and must remain documented.
- The editable Realm contract requires `connect-src 'self' https:`. Narrowing it to `'self'` requires a separate product decision and tests.
- The version deployed by `deploy-web.cmd` contains the formal Stage 22W message client: authenticated identity/register/events, channel topics and DMs, history/unread/read, text/image send/outbox, message actions, protected avatars, controlled image preview/download and reconnect. Automated acceptance uses fake HTTP; a deployment smoke test without credentials proves only the static entry and bundle, not real-account Zulip reads or writes.
- Media URLs are HTTPS/same-Realm/path allowlisted and loaded as revocable Blobs. The runtime caps one message at four image previews, Realm media at four concurrent reads and the page cache at 64 MiB. Upload cancellation precedes logout credential cleanup; neither upload nor message POST is automatically retried.
- Deploying a large-version preview does not complete Stage 21 Live, MAUI manual login or clean-VM gates.
