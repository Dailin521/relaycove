# Stage 30 — message bubble autosize and conversation-search footer

Status: completed and delivered in `main@57c8145`

## Scope

- Make short native message bubbles size to their own content without changing long-message wrapping or message authority.
- Remove the stale left-list “加载更多搜索结果” control after conversation search ends.
- Do not change search protocol, message content, emoji projection, Web, cache, or Realm state.

## Diagnosis

- The bubble lived under a message header whose sender/time/quick-actions width determined the surrounding stack. Its default fill alignment expanded short text and single-emoji bubbles to that header width.
- Search pagination state and `ShowMoreConversationFilterResults` already reset for an empty query, and the existing ViewModel regression passed. The remaining defect was native presentation: a button hosted as `CollectionView.Footer` could retain its previous visible footer after the list returned to ordinary conversations.

## Final implementation

- Set the bubble itself to content-sized start alignment, with an own-message trigger that preserves end alignment. Keep the existing row maximum-width calculation, quote/attachment layout and word wrapping unchanged.
- Move the pagination button from `CollectionView.Footer` into an auto-sized root grid row and bind visibility/command directly to the page ViewModel. The existing condition remains authoritative: nonempty current query, confirmed next page and not busy.
- Add XAML regressions for content-sized bubble alignment and the direct, non-footer pagination binding. Preserve the existing server-search state regression.

## Deterministic evidence

- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:OutputPath=.verify/stage30-message-bubble-autosize-test/ --filter FullyQualifiedName~MainShellLayoutTests.MessageBubble_WhenContentIsShort_SizesToContentAndKeepsOwnMessageAlignment` — 1/1 passed.
- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:OutputPath=.verify/stage30-search-footer-test/ --filter <two focused layout/state tests>` — 2/2 passed.
- Final `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:OutputPath=.verify/stage30-search-footer-build/` — passed with 0 warnings/errors.

Not run: Fast, Full, Live, packaging, Agent app startup, screenshot, Realm access or external write.

## Manual result

- The user confirmed the short-message bubble autosize in Visual Studio.
- The user then confirmed the search-footer residual was fixed and authorized commit/push.
