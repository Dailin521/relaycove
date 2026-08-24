# Stage 28 — unified conversation search

Status: local implementation candidate; waiting for user Visual Studio validation

## Scope

- Upgrade only the MAUI left conversation search. The right message-search overlay, Web, protocol contracts, server data and Realm settings remain unchanged.
- Preserve the Stage 25 supported-conversation boundary: one-to-one/self DM and eligible private empty-topic groups only.

## Diagnosis

- The left field was labelled “搜索聊天” but only filtered the already-projected conversation rows by title and latest detail. Its `CurrentCultureIgnoreCase` substring check did support contiguous partial text, but it could not see older cached messages, private-group members or server history.
- The right search combined local conversations/users/messages with Zulip's server `search` narrow and pagination, so its coverage was materially broader.

## Final implementation

- Every edit immediately performs contiguous, culture-insensitive partial matching over conversation titles, latest summaries, authoritative loaded group-member names, sender names and all messages currently projected from local cache.
- After a 300 ms debounce, the same query uses the existing read-only `IClientSession.SearchMessagesAsync` path. Results are filtered to supported RelayCove conversations and merged with local results. Each matched message remains a separate row, including multiple matches from the same person/conversation; only duplicate message IDs are collapsed. Superseded query/account operations are cancelled and stale completions fail closed.
- The first server page reads 50 matches. When Zulip reports older results, the list exposes “加载更多搜索结果”; subsequent pages merge new message hits without discarding existing results.
- A message-result row carries only its matched message ID as transient UI navigation state. Clicking it uses the existing `OpenMessageAsync` path to open that conversation at the match; ordinary title/member matches retain one normal conversation entry and latest-message activation.
- Busy and failure text are shown in the existing compact search header. Server failure preserves local results and does not affect messaging.

## Deterministic evidence

- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:OutputPath=.verify/stage28-search-footer-test/` — 255/255 passed.
- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:OutputPath=.verify/stage28-search-footer-build/` — passed with 0 warnings/errors.
- Regressions cover multiple local and server message matches from one conversation, a server-only historical DM absent from the current list, pagination/message-ID deduplication, opening the selected matched message, rejecting a superseded delayed query and immediately hiding the pagination footer when search is cleared or exhausted.

Not run: Fast, Full, Live, packaging, app startup, screenshot, Realm write, commit or push.

## Visual Studio short check

1. Enter part of a chat name and part of its latest summary; results should update immediately.
2. Enter a continuous fragment contained in several messages from the same person. After the brief “正在搜索历史消息…” state, every matching message should appear as its own row rather than one row for that person.
3. Click different historical-message rows. Each should open around its own message rather than an unrelated row.
4. Search a group member's partial name; after the authoritative roster is available, the group should match.
5. Rapidly replace one query with another. Results from the old query must not reappear; a server failure should retain local matches and show a compact explanation.
6. Clear the query or open a matched result. “加载更多搜索结果” must disappear immediately; it is visible only while the current nonempty query has a confirmed older page and is not already loading.

Manual result: pending.
