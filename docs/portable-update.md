# RelayCove Portable ZIP Update Core

M4-03 supplies the offline apply core for the internal `portable-zip` Client RC.
It is deliberately not an installer or a complete automatic-update experience.
The next Client slice owns manifest fetching, download, user choice, explicit
Client exit, and the structured handoff to `RelayCove.Updater.exe`.

## Package and invocation boundary

Each Client RC ZIP contains a separate `RelayCove.Updater.exe`. It is a
`win-x64`, self-contained, single-file executable, so the replacement helper
does not depend on a machine-wide .NET installation or the files in the Client
directory being replaced. The Client must pass a downloaded archive and its
already validated expected size, SHA-256, target/current version, package root,
and exact exiting Client process identity. The Updater does not download from
the network and does not accept an arbitrary executable or command line.

For this internal RC, extract releases only to a normal user-writable portable
directory. Do not use `Program Files`, a source checkout, a drive root, a
junction, or a directory controlled by another user. Before replacement the
Updater bootstraps outside the active package, verifies the archive and inner
manifest, waits only for the specified Client process to exit, stages on the
same volume, and swaps the old directory to a backup before activating the new
one. It never kills the Client process.

## Recovery and data

The updater keeps its staging, backup, and recovery journal beside the portable
package so an interrupted directory swap can converge on a complete old or new
package on the next run. This is an offline replacement boundary, not a promise
of health-check rollback after the new Client has been started.

Account data is intentionally outside the portable package at
`%LOCALAPPDATA%\RelayCove`. Replacing or restoring a package must not delete
that directory, credentials, local SQLite data, downloads, or logs.

## Explicit limits

The M4-03 helper is unsigned and accepts a release only after the Client has
obtained it from the internal controlled distribution path. HTTPS plus a
same-source SHA-256 protects against accidental transfer damage; it does not
protect against a compromised release host or TLS trust chain. Code signing,
timestamping, SmartScreen reputation, installer/elevation support, Program
Files, reboot scheduling, incremental updates, channels, silent updates, and
public distribution remain outside this RC and require later release work.
