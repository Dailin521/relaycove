# Stage 31 — Windows message-body drag selection

Status: completed and accepted by user in Visual Studio

## Scope

- Allow a Windows user to hold the left mouse button and drag across an ordinary message body to select an arbitrary text range, then copy that selected range with `Ctrl+C`.
- Keep selection local to one visible message body. Do not turn the `CollectionView` into row selection and do not change raw message content, quote parsing, attachments, message actions, unread state or scrolling behavior.
- Keep the existing right-click message action menu. Native selected-text copying in this slice uses `Ctrl+C`; quote cards and attachment labels remain outside this narrow correction.

## Diagnosis

- The ordinary body in `MessageListView.xaml` was a MAUI `Label`. On Windows its native control is a WinUI `TextBlock`, whose `IsTextSelectionEnabled` default is false, so pointer dragging could not create a text selection.
- Existing message pointer handling was not the cause: the viewport hook only clears pending scroll anchors and does not mark left-button events handled; `MessageContextBehavior` handles hover, keyboard menu keys and right-click, but not left-button movement or `Ctrl+C`.

## Final implementation

- Add the Windows-only `SelectableTextBehavior` and attach it only to the ordinary message-body label. The behavior enables the existing native `TextBlock.IsTextSelectionEnabled` property; it does not replace the label with an editor or create a second text copy.
- When a virtualized label receives another binding context, clear the old native selection before the new row is shown. Detaching the behavior also clears and disables selection so a recycled native view cannot retain stale highlighting.
- Preserve `CollectionView.SelectionMode="None"`, message bubble autosizing, left/right alignment, quick actions and the existing full-message copy command.

## Validation evidence

- Baseline `pwsh ./scripts/verify.ps1 -Mode Fast` on `main@6e8d0e9` stopped at the pre-existing repository-wide `dotnet format --verify-no-changes` gate. The reported failures were existing CRLF/whitespace/import-order debt across unrelated files; no mass formatting was applied, and Fast did not reach build/tests.
- The first combined verification attempt exposed an xUnit analyzer correction in the new regression and an `obj` lock caused by running test/build concurrently. The assertion was corrected and subsequent verification was run sequentially.
- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:OutputPath=.verify/stage31-selectable-message-test/ --filter FullyQualifiedName~MainShellLayoutTests.MessageBody_WhenRendered_EnablesNativeTextSelectionWithoutSelectingListRows` — passed 1/1.
- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:OutputPath=.verify/stage31-selectable-message-build/` — passed with 0 warnings and 0 errors.

Not run: complete App suite, Full, Live, package, Agent app startup, screenshot, Realm access or external write.

## Manual result

- The user confirmed the Windows message-body drag selection behavior in Visual Studio and authorized commit/push.
- The accepted scope remains ordinary message bodies only; quote cards, attachment labels, cross-message selection and the existing right-click message menu were not changed.

## Shortest manual check

1. Open a conversation containing a multi-line ordinary text message.
2. Hold the left mouse button and drag across part of its body; confirm only those characters highlight.
3. Press `Ctrl+C` and paste into a temporary local text field; confirm only the highlighted range was copied.
4. Scroll the message out of view and back, then switch conversations; confirm stale highlighting does not appear on another message.
5. Confirm hovering still reveals quick actions and right-click still opens the existing message menu.
