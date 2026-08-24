# Stage 29 — complete Zulip Unicode emoji catalog

Status: local implementation candidate; waiting for user Visual Studio validation

## Scope

- Complete only the MAUI Composer and message-reaction Unicode emoji catalog.
- Do not change message/reaction protocol behavior, add custom Realm emoji, modify Web, or perform Realm writes.

## Diagnosis

- Both native pickers shared one hard-coded list containing only 24 entries. The picker and scrolling controls were working, but every other supported Unicode emoji was absent from the data source.
- Binding all 1883 entries to both picker grids at once removed that functional limit but made the native popover visibly slow. This all-at-once candidate was rejected.
- Native message presentation still showed Zulip raw emoji Markdown such as `:melting_face:` literally. The raw message must remain authoritative, but the visible body needs a local shortcode projection.

## Final implementation

- Replace the 24-entry literal with a generated catalog matching the target Realm's public Zulip 12.1 `emoji_codes.json`: 1883 unique Unicode entries across all nine official categories.
- Each picker opens on the original 24 localized common choices and exposes one horizontally scrollable category strip: common plus the nine official categories. A Windows native pointer behavior lets the user hold the left mouse button and drag the strip, with a 4 DIP threshold so ordinary category clicks still work. The strip keeps explicit top/bottom space so Windows DPI scaling cannot clip the pill buttons. Switching tabs replaces the bound collection with only that category instead of templating all 1883 entries.
- Remove the redundant right-side header instructions; the popovers retain only “表情” and “添加反应”.
- Build each displayed Unicode sequence directly from Zulip's dash-separated codepoint. Reaction selection retains the matching canonical `emoji_name`, `emoji_code` and `unicode_emoji` type; Composer insertion continues to insert only the Unicode text at the current selection.
- Visible message bodies and quote cards replace all 3339 known Zulip 12.1 canonical names/aliases with their Unicode display value. Unknown names, escaped shortcodes, inline code and fenced code remain literal. `MessageItem.Content`, copy, edit and quote sources keep the untouched raw Markdown.
- The catalog is bundled code, so opening either picker performs no network request. Custom Realm emoji and future server-version additions remain separate capabilities.

## Deterministic evidence

- Read-only public catalog check: target Zulip 12.1 reports 9 categories, 1883 catalog entries, 1883 unique codepoints and 1883 canonical codepoint mappings.
- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:OutputPath=.verify/stage29-emoji-mouse-drag-test-final/` — 266/266 passed.
- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:OutputPath=.verify/stage29-emoji-mouse-drag-build/` — passed with 0 warnings/errors.
- Regression coverage verifies total count, category projection, the two horizontal unclipped category strips, mouse drag threshold/offset/clamping, removed header hints, code uniqueness, reaction type, representative newer/flag/joined-person sequences, known and unknown shortcodes, code-span preservation and raw-content authority.

Not run: Fast, Full, Live, packaging, app startup, screenshot, Realm write, commit or push.

## Visual Studio short check

1. Open the Composer emoji picker. It should open quickly on “常用”; hold the left mouse button on the single category strip and drag it horizontally through all nine other Chinese tabs. A short click must still select the category, and the selected pill's rounded bottom must remain fully visible at current DPI.
2. Under “笑脸”, select `🫠` or `🫡`; it should insert at the current cursor/selection and restore Composer focus.
3. Open a message reaction picker, switch category and select a newly available emoji; the server should accept it and the reaction should render normally.
4. Send or receive raw Zulip content `:melting_face:`. The bubble should show `🫠`; copying/editing/quoting it should still use the original raw shortcode.
5. Confirm category switching and scrolling remain responsive and closing/reopening does not retain a stale keyboard selection.
6. Confirm the redundant “选择后插入光标位置” and “再次选择可移除” text no longer appears.

Manual result: pending.
