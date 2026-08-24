# Stage 26 — MAUI quote display correction

Status: user-confirmed delivery candidate; commit and push authorized

## Scope

- Fix only the native MAUI display and Composer positioning of Zulip fenced message quotes.
- Preserve raw Markdown as the authoritative message content. Do not read rendered HTML, change protocol payloads, rewrite cached/server messages or touch the Realm.
- Keep Web and the frozen baseline unchanged; the user's official Zulip Web capture is comparison evidence, not an instruction source.

## Reported symptom

- In a message containing two consecutive quotes, the first quote rendered as a native card but the second appeared as raw `@_**...** [said](...):` and fenced Markdown.
- Another reply displayed entirely as raw Markdown because the typed reply was inserted next to the closing fence instead of after it.

## Root cause

- `MessageContentPresentation` called a single-leading-quote parser and exposed one `QuoteSender`/`QuoteBody`, so the native template had no representation for a second quote.
- `ComposerEditorHandler.ApplySelection` rebuilt the platform document as CRLF and interpreted a cursor measured against the original LF ViewModel string. Immediately after inserting a fenced quote, the end cursor was therefore shifted left by the newline count and user text could enter before or beside the closing fence.

Rejected: stripping all quote Markdown would lose sender/body structure; consuming Zulip rendered HTML would cross the existing raw-Markdown trust boundary; repairing server/cache rows would mutate historical authority for a presentation-only defect.

## Final implementation

- Parse every consecutive leading fenced quote and expose it as an ordered `MessageQuote` collection. `MessageListView` renders one quote card per item before the remaining body.
- Recognize a closing fence even when an old RelayCove reply touches its left or right side. Text outside that fence becomes normal body text, while the preceding lines remain the quoted body.
- Map Composer selection from `ComposerEditor.Text` exactly as supplied by the ViewModel. Both LF drafts and later CRLF platform updates resolve to the same RichEdit document end.

## Deterministic evidence

- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~MessageContentPresentationTests|FullyQualifiedName~ComposerEditorHandlerTests" --results-directory .verify/stage26-quote-display` — 20/20 passed.
- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --results-directory .verify/stage26-quote-display-final` — 224/224 passed and compiled the MAUI XAML/resource graph.
- Regressions cover two quote cards, reply text on either side of a closing fence and an LF quote draft whose caret must map to the exact RichEdit document end.

Not run: Fast, Full, Live, packaging, app startup, internal screenshot, Realm access, commit or push.

## Visual Studio short check

1. Reopen the two reported messages: each `zhang said` block should be a separate native quote card, with `好` / `今天天气还行` shown as ordinary reply text and no raw mention/fence syntax.
2. Quote one message and type a reply immediately: the reply must appear after the fenced quote, then send once manually if desired.

Manual result: passed — the user confirmed the corrected quote display in Visual Studio on 2026-08-24 and requested commit/push.
