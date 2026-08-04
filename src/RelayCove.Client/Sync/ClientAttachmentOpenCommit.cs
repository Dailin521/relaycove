namespace RelayCove.Client.Sync;

// The account shell's final exact-selection gate. It is synchronous so the
// SQLite confirmation callback can linearize authorization without performing
// file I/O or entering the Windows Attachment Manager. The supplied action is
// the already-prepared STA job's no-I/O commit: the gate must invoke it while
// holding its exact identity/selection state, and may report HandedToWindows
// only when it succeeds.
internal delegate ClientAttachmentOpenStatus ClientAttachmentOpenCommit(
    Func<bool> commitPreparedJob);
