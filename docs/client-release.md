# RelayCove Client Internal RC ZIP

This document describes the **internal, unsigned** Windows Client RC ZIP. It is
not an installer and is not approved for public distribution.

## Build and verify

Use an explicit release version from a clean checkout:

```powershell
pwsh ./scripts/publish-client.ps1 -Version 1.0.0-rc.1
pwsh ./scripts/verify-client-release.ps1 -Version 1.0.0-rc.1
```

The verifier expects the current `HEAD` commit by default. When validating an
older artifact, or after documentation-only changes made after the build, pass
the artifact's manifest commit explicitly with `-ExpectedCommit <40-lowercase-hex>`.

The release is written below `artifacts/client/<version>/`. The ZIP and its
`.sha256` sidecar are the delivery inputs; copy both together. The publisher
refuses a dirty checkout by default. `-AllowDirty` and `-AllowDirtySource`
are local diagnostic switches only and must not be used for an RC handoff.

Before extracting, verify the sidecar against the ZIP:

```powershell
$archive = '.\RelayCove.Client-1.0.0-rc.1-win-x64.zip'
Get-FileHash -LiteralPath $archive -Algorithm SHA256
Get-Content -LiteralPath "$archive.sha256" -Raw
```

The hexadecimal SHA-256 from `Get-FileHash` must equal the first field in the
sidecar, and the sidecar filename must name that exact ZIP. The verifier also
checks the ZIP layout, manifest, file hashes, PE architecture, required runtime
assets (including the standalone updater), and prohibited local or secret-bearing
files.

## Run the RC

1. Create an empty, user-writable directory that is not a source checkout.
2. Extract the ZIP without changing its top-level directory.
3. Start `RelayCove.Client.exe` from the extracted package root directory.
4. To exit the process completely, use the tray menu's explicit exit action;
   closing the main window normally keeps the Client in the tray.

The package is a `win-x64`, self-contained, unpackaged WPF Client. Its package
root also contains `RelayCove.Updater.exe`, a separate `win-x64`, self-contained
single-file tool used by the Client's verified update handoff; it is not launched
by hand for normal use. Neither executable needs a separately installed .NET runtime.
The package is unsigned: Windows trust
warnings, SmartScreen behavior, enterprise policy, installation, uninstall, and
file associations are outside this RC contract.

## Local data and safety

Running the executable does not keep account data beside the ZIP. The Client
uses the current user's `%LOCALAPPDATA%\RelayCove` root for its account-scoped
SQLite/cache material and protected credential state. Downloaded attachment
cache and local logs are therefore user data, not release artifacts. Do not
delete that directory merely to replace an extracted RC ZIP.

The release ZIP must never contain a local database, cache, uploads, logs,
DPAPI credential file, `.env` file, signing key, certificate, source file, or
PDB. Treat an unexpected file or a SHA-256 mismatch as a failed handoff and do
not run it.

## Explicit limits and next gates

The Client supports an HTTPS update manifest, bounded download with exact size
and SHA-256 verification, optional/mandatory UI, and an explicit exit handoff to
the packaged Updater. The release operator must publish the ZIP before publishing
the manifest that references it; an invalid or partial artifact must never become
the current manifest target.

This RC still does not provide an installer, code signing, timestamping, silent
installation, or public-distribution trust chain. M5 retains the real VPS/TLS,
real-login, dual-client, notification, and end-to-end upgrade/recovery gates.
