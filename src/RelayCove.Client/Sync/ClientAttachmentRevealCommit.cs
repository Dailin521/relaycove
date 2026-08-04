namespace RelayCove.Client.Sync;

// This delegate is the one-way linearization point for a local reveal. It may
// authorize the shell start, but must not invoke the shell itself: the caller
// intentionally releases every account/cache lock before doing that potentially
// unbounded native operation.
internal delegate ClientAttachmentRevealStatus ClientAttachmentRevealCommit();
