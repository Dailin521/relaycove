# Stage 25 — 微信式统一会话与私有群聊

Date: 2026-08-21
Status: user-confirmed delivery candidate plus authorized Realm channel reset; commit and push authorized

## Confirmed product contract

- The MAUI conversation pane is one chronological list. It contains only one-to-one/self direct messages and subscribed, active private channels whose `topics_policy` is `empty_topic_only`.
- On 2026-08-21 the user superseded the legacy-channel compatibility assumption and selected MAUI as the only forward product client. All 17 then-active public/private channels were archived in the target Realm; the one already archived channel remained archived. `RelayCove.Web` source is unchanged by this task and is no longer a parity target.
- A private group uses the existing canonical `ChannelTopic(channelId, "")` key. The empty topic is an internal protocol/cache identity and is never shown as a topic in the UI.
- A new group has an explicit name and at least two other active members. Creation is one non-retried `POST /channels/create` containing the creator and selected members, `invite_only=true`, `topics_policy=empty_topic_only`, shared history, no Web-public/default/announcement behavior, and direct creator-only administer/add/remove-subscriber groups.
- The register snapshot's `realm_can_create_private_channel_group`, resolved against the register user-group snapshot, is the fail-closed authority for showing the create-group action. RelayCove never changes Realm configuration.
- The unified row order is pinned first, then latest-message time descending, then canonical key. Group avatars use up to four member avatars in stable user-ID order; missing membership data falls back to the group initial.
- Group settings expose members, name, announcement, local remark, message search, mute, pin, exact empty-topic local-cache clearing and exit. A RelayCove owner is recognized only when administer/add/remove-subscriber groups are the same single direct member with no subgroup. Complex external permission structures expose personal settings only.
- Ownership transfer changes the three owner groups atomically, verifies the new owner, and only then attempts the old owner's exit. Dissolve removes and verifies all other members before the owner exits. Partial stages are reported explicitly and never automatically retried. The service-side private history is not archived or deleted.

## Implementation slices

1. Extend register/subscription contracts, private-group creation, channel policy events and SQLite schema v6 while preserving existing message keys and account isolation.
2. Replace the MAUI channel/topic/private-message groups with one `ConversationListItem` projection and private-chat/private-group creation flow.
3. Project private-group settings and lifecycle operations with authoritative re-reads and phase-specific failure messages.
4. Update the root plan, interaction contract, status and deterministic tests. Run related Core/Zulip.Client/Data/App tests and an App Debug build only.

## Boundaries

- Stage 24.11 through Stage 24.17 remain uncommitted local candidates; Stage 24.14 and Stage 24.17 have user UI confirmation. This task does not rewrite their historical evidence.
- This is now the forward MAUI-only product contract. The frozen `chat-ui-v1` baseline and `RelayCove.Web` remain historical evidence/source, not a parity or delivery target; removing the Web project itself is a separate repository decision.
- The user's explicit channel-reset authorization covered all existing active public/private channels. Zulip exposes archival rather than permanent channel deletion, so the operation used the supported server archive path: history remains recoverable/searchable and no message, account or credential was deleted.
- No Full, Live test suite, packaging, deployment, group create/invite/transfer/dissolve write, commit or push is authorized. Production Realm access was limited to the separately recorded channel inventory, backup verification, archive transaction and postcondition checks below.
- Offline preview may run on the secondary display and may retain only internal screenshots. Final native UI/interaction acceptance belongs to the user in Visual Studio.

## Verification log

### Final implementation

- `Subscription` and register projection now preserve private/Web-public/topic-policy facts and resolve the private-channel creation setting against the register user-group snapshot. Missing or structurally unsupported permission data disables group creation instead of inferring access.
- The Zulip gateway sends one non-retried native create-channel request with the complete initial roster and fixed RelayCove group policy. A successful response is followed by a fresh register snapshot; timeout/transport uncertainty never replays the write and asks the user to inspect the authoritative list.
- Core and schema v6 persist/project only supported one-to-one/self-DMs and private, active, non-Web-public `empty_topic_only` empty-topic groups. Unsupported register/event payloads do not enter the new timeline or search, and a conversation whose policy changes externally is deselected immediately without deleting unrelated server/local history.
- MAUI now projects one filtered/sorted `ConversationListItem` collection, direct/group creation flows, stable up-to-four-member group avatars, topic-free group headers, and one settings surface for personal preferences, authoritative members and group lifecycle actions.
- Local clear-history now removes the exact account/conversation message, empty-topic summary and unread rows in one cache transaction. It does not unsubscribe, delete a server message, remove credentials or affect another account/conversation.
- Owner recognition requires the administer/add/remove-subscriber settings to name the same single direct member. Rename, announcement, invite, remove, transfer, dissolve and exit use non-retried writes with authoritative reads; transfer and dissolve preserve their explicit phase boundary and never repeat a completed first phase.

Rejected approaches: retaining the Zulip channel/topic tree or group-DM projection would contradict the confirmed single timeline; inferring membership/ownership from the generic Realm user directory would claim facts the server did not provide; optimistic local success or write retries could duplicate a non-idempotent operation; server-side deletion was rejected because “清聊天记录” is account-local cache clearing only.

### Deterministic evidence

- `dotnet test tests/RelayCove.Core.Tests/RelayCove.Core.Tests.csproj -c Debug --no-restore --results-directory .verify/stage25-core-final6` — 151/151 passed.
- `dotnet test tests/RelayCove.Zulip.Client.Tests/RelayCove.Zulip.Client.Tests.csproj -c Debug --no-restore --results-directory .verify/stage25-create-topics-policy-json` — 102/102 passed on the final protocol tree.
- `dotnet test tests/RelayCove.Data.Tests/RelayCove.Data.Tests.csproj -c Debug --no-restore --results-directory .verify/stage25-data-final4` — 34/34 passed.
- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --results-directory .verify/stage25-group-dialog-layout-final` — 220/220 passed on the final App tree.
- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore` — passed with 0 warnings and 0 errors.
- Independent read-only protocol, Core, Data and UI reviews found no remaining P0/P1 after fixes for stale register publication, unsupported-event persistence, moved-message unread cleanup, exact cache purging and authority-load failure states.

An initial attempt to run the final App test and App build concurrently made the MAUI XAML compiler contend for the same `obj/.../input.json`; the build passed, and the test was immediately rerun serially to the 219/219 result above. This was command concurrency, not a product-code failure.

### Offline secondary-display preview

All previews used `NativeShellPreviewSession`, disabled network access and `InputAutomation: None`. The preview executable was built into an isolated timestamped output, placed on `DISPLAY2`, captured internally, and stopped by matching the recorded PID and executable path.

| Scene | DIP / pixels / DPI | Internal evidence |
|---|---|---|
| unified shell | 1440×900 / 2160×1350 / 144 (150%) | `artifacts/maui/screenshots/stage25-unified-private-groups/shell-1440-light.png`, SHA-256 `9BE02C5DFE85A835F1B4671D157E497A153B5C6AB01EC45B4100EC0CF3F0110F` |
| owner group settings | 1440×900 / 2160×1350 / 144 (150%) | `artifacts/maui/screenshots/stage25-unified-private-groups/details-1440-light.png`, SHA-256 `D1CBF471FCCE28D29CA76128C808E84697F43E56DF290D88D9551EF4DBDEF114` |
| narrow conversation list | 640×900 / 960×1350 / 144 (150%) | `artifacts/maui/screenshots/stage25-unified-private-groups/narrow-list-640-light.png`, SHA-256 `FC4FD06BF61D1FCF98D246AA21C550AB97A375338E296DE80CF800B36B981AE6` |
| narrow group chat | 640×900 / 960×1350 / 144 (150%) | `artifacts/maui/screenshots/stage25-unified-private-groups/narrow-chat-640-light.png`, SHA-256 `4F8BAFFF734F4261F57FDCB664853F08AC60B2C400AD8F15DBB9896A7DE3A38F` |

The first details capture exposed that the offline `details` scene opened the pane without invoking the normal authoritative settings-load path, so its fixture showed zero members. The preview entry now invokes the same load path and has a regression test; the replacement capture shows four members, the direct owner marker and owner-only editing controls. The main shell remains two-column; settings temporarily occupies the right side only after the ellipsis action.

### Authorized Realm channel reset

- Read-only inventory found 17 active channels: IDs `1–10`, `12–18`; ID `11` (`运维`) was already archived. The target set contained 13 public and 4 private channels, with no Web-public channel.
- Zulip's supported “delete channel” operation is archival. Before the write, the enabled backup timer was active and the 2026-08-21 04:16 data/config archives both passed `sha256sum -c`.
- A fail-closed precheck required the exact ID/name/privacy set and one active organization administrator. One outer database transaction then called Zulip 12.1's own `do_deactivate_stream` path for all 17 targets, preserving its default-channel cleanup, events, audit records and archive notices.
- Immediate and independent reads confirmed `active=0`, `archived=18`, `defaults=0`, archived IDs `1–18`, the Zulip container `healthy`, and HTTPS `/api/v1/server_settings` reachable.
- Recovery: organization administrators can unarchive individual channels; channel history was not permanently erased. No direct SQL, volume deletion, message deletion, account change or credential output occurred.

### Visual Studio private-group creation protocol correction

- The user's first real “三人成行” creation attempt failed visibly and was not retried. Server evidence at 2026-08-21 10:26:14 recorded one `POST /api/v1/channels/create` with HTTP 400 and the controlled error `topics_policy is not valid JSON`; the Realm remained `active=0 / archived=18`, proving that no partial group was created.
- Root cause: the create-channel endpoint declares `topics_policy` as `Json[TopicsPolicy]`. RelayCove sent the form value as raw `empty_topic_only`; Zulip 12.1 requires the JSON string form `"empty_topic_only"`. Boolean fields and the three anonymous owner-group objects were already encoded in the accepted shapes.
- Final implementation serializes only the create request's topic-policy enum as a JSON string. The existing channel-update endpoint intentionally keeps its plain-string encoding because its Zulip 12.1 signature is a non-JSON `TopicsPolicy`; this avoids broad or speculative form rewrites.
- Regression coverage inspects the decoded form body, requires the literal `"empty_topic_only"`, parses it as JSON and verifies its string value. The complete Zulip.Client project then passed 102/102. The Agent did not send a second create request; final interaction verification remains with the user in Visual Studio.

### Visual Studio new-group input layout correction

- The user's next Visual Studio capture showed the contact filter and group-name fields touching/overlapping as the dialog was resized. Both entries occupied the same Grid cell; the group-name field was moved down only by a fixed `48` DIP top margin, so its actual row was never measured independently.
- The input area now has two explicit `Auto` rows with 8 DIP spacing. Search remains in row 0 and group name in row 1; the fixed positioning margin was removed. No ViewModel, protocol or Realm behavior changed.
- A structural XAML regression requires both named entries to share the two-row input Grid, occupy distinct rows and keep the group-name entry margin-free. The complete App project passed 220/220 and compiled the MAUI XAML. Native visual acceptance remains with the user in Visual Studio.

Not run: Fast, Full, Live test suite, packaging, deployment, or any real create/invite/remove/transfer/dissolve/send write. `RelayCove.Web` source was not changed. No commit or push was made.

## Visual Studio short check

1. The left pane is one WeChat-style list with no channel/DM groups, `#`, topic rows or public-channel entry.
2. One-to-one/self chats and eligible private groups open directly; group headers show group name and member count without a topic.
3. `+` separates one-person private chat from named groups of at least three total members and explains a denied create capability.
4. Group settings distinguish owner/member capabilities, and clear-history text states that only this account's local group-chat cache is cleared.

Manual result: passed — after the final new-group input-layout correction, the user requested commit and push from the Visual Studio-validated `main` state.
