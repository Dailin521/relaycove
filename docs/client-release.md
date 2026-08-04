# RelayCove Client Internal RC ZIP

This document describes the **internal, unsigned** Windows Client RC ZIP. It is
not an installer, does not update an existing copy, and is not approved for
public distribution.

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
assets, and prohibited local or secret-bearing files.

## Run the RC

1. Create an empty, user-writable directory that is not a source checkout.
2. Extract the ZIP without changing its top-level directory.
3. Start `RelayCove.Client.exe` from the extracted package root directory.
4. To exit the process completely, use the tray menu's explicit exit action;
   closing the main window normally keeps the Client in the tray.

The package is a `win-x64`, self-contained, unpackaged WPF Client. It does not
need a separately installed .NET runtime. It is unsigned: Windows trust
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

This RC does not provide an installer, code signing, timestamping, Updater,
update manifest/API, mandatory-version UI, download-and-replace flow, or
automatic rollback. Do not represent it as an installed or publicly releasable
application.

M4 will next use a stable Client delivery format to define the Updater and
update-manifest contract. M5 retains the real signed-installation, SmartScreen,
VPS, real-login, dual-client, notification, and upgrade/recovery gates.
