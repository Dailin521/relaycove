# Stage 24.9 — 官方式话题行与操作菜单

Date: 2026-08-19
Status: local candidate; deterministic verification complete; Visual Studio confirmation pending

## Problem

Stage 24.7 delivered the channel/topic tree, but topic rows still lacked Zulip's selected/hover actions and official topic popover. This follow-up is one bounded interaction problem: reproduce the topic-row status/unread/more affordances and connect the official topic actions without changing Web, Data or Realm contracts.

## Final implementation

- Topic rows are stable observable objects reconciled by canonical channel/topic key. Selection, hover and open-menu state survive refresh; selected, hovered or menu-targeted rows show personal visibility, unread count and more.
- The anchored popover follows Zulip's groups: compact Muted/Inherit/conditional Unmuted/Followed controls; mark-read and canonical link copy; move, resolve/unresolve and delete. Outside click and Escape close the top child first, and final closure returns focus to the originating row.
- A local topic draft without a message anchor shows an empty state and cannot issue mark-read/move/resolve/delete writes. Personal actions require an authenticated user and active subscription. Because this slice does not persist the realm move/resolve group snapshot, whole-topic mutations fail closed to a confirmed organization administrator on an active channel; the server remains final authority.
- Move chooses an explicit destination, resolves the source's oldest message, then performs one `change_all` update using Zulip's documented notification defaults. Resolve/unresolve uses canonical `✔ ` naming and the official repeated-prefix cleanup rule.
- Delete is organization-admin-only and requires confirmation. `complete=false` is reported as partial completion and never automatically repeated. Non-idempotent writes are single attempts; only idempotent narrow mark-read pages through the server cursor.
- Topic links use Zulip hash encoding and may include the known latest message ID. Credentials never enter URLs, request strings, logs or snapshots.

## Corrected approaches

- The first policy adapter used an obsolete subscription-topic path. Official 12.1 evidence requires `POST /user_topics`; implementation and tests were corrected before delivery.
- Using the latest topic message as move target was rejected; the client first performs a read-only oldest-message lookup.
- A fixed four-column policy grid left a gap when Unmuted was unavailable; the final flex group collapses it.
- Enabling move/resolve with unknown group authority was rejected in favor of a conservative administrator gate.

## Deterministic evidence

```powershell
dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore --nologo
# 0 warnings, 0 errors

dotnet test tests/RelayCove.Core.Tests/RelayCove.Core.Tests.csproj -c Debug --no-restore --nologo
# 119/119

dotnet test tests/RelayCove.Zulip.Client.Tests/RelayCove.Zulip.Client.Tests.csproj -c Debug --no-restore --nologo
# 58/58

dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo
# 164/164
```

All network tests use fake HTTP and all App tests use fake sessions. No Fast, Full, Live, package, production Realm connection or real write was run.

The native shell was also launched with the read-only `NativeShellPreviewSession` at 1024×768 on the secondary display. UI Automation expanded `design`, opened the selected `UI 设计讨论` topic popover and captured `artifacts/maui/screenshots/stage24-9-topic-menu/preview-topic-menu.png`. The popover remained anchored to the topic row; selected-row state, the compact personal-policy group, mark-read and copy groups rendered correctly. The preview identity is not an organization administrator, so whole-topic mutation actions correctly remained hidden. The preview process was then stopped. This is offline visual evidence only; Visual Studio/manual acceptance remains open.

## Short manual check

1. Expand a channel; select and hover different topics. Confirm visibility glyph, unread count and more remain aligned.
2. Open a topic menu. Confirm it anchors to that row and the compact policy, read/copy and administrator groups match the reference hierarchy.
3. Open Move, Resolve and Delete, then use outside click or Escape. Only the child closes first; do not confirm a real write during visual acceptance.
4. Close the menu and confirm focus returns to the same topic row's more button.
