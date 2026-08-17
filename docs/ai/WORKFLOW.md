# RelayCove Engineering Workflow

## 1. Orient

Read `docs/ai/README.md`, then the root plan, STATUS, WORKFLOW and only the task marked active. Dated Stage 22/23/24 task records are historical evidence after their merge and must not override current repository truth. Run `git status --short`, confirm the expected branch, and identify unrelated user changes before editing. Follow the current user authorization for each commit/push and external write.

For UI work, also read `docs/ui/README.md`, the frozen baseline manifest, `docs/ui/INTERACTION_SPEC.md` and `docs/ui/DEVELOPMENT_WORKFLOW.md`.

## 2. Implement a vertical slice

Keep changes inside one independently testable path:

1. contract/domain;
2. adapter/storage;
3. session/use case;
4. ViewModel/View;
5. tests and documentation.

Do not add speculative services, placeholder projects or features outside the active task. Use official Zulip 12.1 OpenAPI/Docs for protocol decisions and record intentional product restrictions separately from server protocol restrictions.

UI implementation follows the mandatory order `formal RelayCove.Web -> browser/user acceptance -> native MAUI parity`. The current formal Web behavior outranks the old frozen prototype for product interaction corrections; frozen HTML remains immutable initial evidence and is never embedded in a WebView. The Web product and MAUI share Token definitions, interaction specifications, capability matrices and acceptance scenarios, but no UI runtime code. Arbitrary attachments, protected media, message actions, server search/saved and current-user channel self-service use reviewed capability slices; mention candidates, complete membership and administrator channel management still require explicit slices.

`RelayCove.Web` uses a locked TypeScript/React/Vite project. Production output must bundle icons/dependencies, exclude development fixture data and avoid runtime CDN. Formal browser API tests inject fake HTTP. The official Zulip Web stays unchanged; never add a RelayCove server, BFF or proxy to make browser tests pass.

Keep the Web delivery cadence explicit. Daily UI work uses repository-root `start-web-dev.cmd`, opens `/` and exercises the formal client against real Realm data by default; do not perform real writes unless the current task explicitly authorizes their exact scope. Deterministic fixtures are automation-only and require explicit fixture/E2E mode. A large-version manual-acceptance sync uses `deploy-web.cmd`, which must complete local verification, versioned archive/SHA-256 checks, atomic switch and HTTPS smoke checks before opening the fixed `/relaycove-web/` entrance. Never add deploy-on-save behavior. Server connection material stays in the private `server-admin` checkout and must not enter this repository or logs.

MAUI visual work follows `docs/ui/MAUI_PREVIEW_WORKFLOW.md`: use the MAUI-aware Visual Studio `Windows Machine` profile and the Debug-only offline preview session. XAML saved inside Visual Studio may use Hot Reload; Codex/external file edits are batched, followed by an App-only no-restore build and one preview restart. Deterministic states use `start-maui-preview.ps1 -Scene/-Theme/-Width/-Height`; `capture-maui-preview.ps1` captures only its recorded PID/EXE with `PrintWindow`. Do not move the user's mouse, inject clicks/keys or depend on foreground focus. The preview may auto-place itself on a non-primary display only under the Debug preview gate. Neither Hot Reload, a secondary-display screenshot nor the in-memory preview can replace XamlC/Release, real Realm, package or clean-VM evidence.

## 3. Verify locally

Run the narrowest relevant tests first. Then run:

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
```

Fast must be deterministic, offline-safe and exclude `RelayCove.Zulip.LiveTests`. NuGet assets and Web dependencies are explicit bootstrap prerequisites (`dotnet restore` / `npm ci`) and must already exist locally. Fast runs only `--no-restore` .NET commands plus Web typecheck, unit tests and production build; it must not restore/install packages or download a browser.

Before delivery, run:

```powershell
pwsh ./scripts/verify.ps1 -Mode Full
```

Full performs Release build/tests, app-project-only publish, ZIP creation, content checks and secret scan, then Web Playwright against local fixture and strict production-subpath previews with fake HTTP. It records console errors/warnings, 1440×900 light/dark, 1024×768 light, keyboard, responsive, asset-path, favicon, cache/security-header and missing-asset evidence. A solution-level `dotnet publish` is prohibited.

Run Live only with explicit authorization and complete isolated configuration:

```powershell
pwsh ./scripts/verify.ps1 -Mode Live
```

Missing host, two dedicated account credentials, recipient allowlist or write confirmation is a failure, not a skip.

The local DAL/zhang bootstrap is deliberately kept under ignored `artifacts/live/`. Before reading the private credential archive or issuing HTTP it requires a separate external Stage 23 run confirmation; authenticated PowerShell requests disable redirects. It requests API keys in process, then verifies three distinct targets: two private/non-archived probes and one public/non-archived join probe, each with exactly the same two approved subscribers. `verify.ps1 -Mode Live` additionally requires the password, recipient allowlist, the existing write confirmation, Stage 23 approval and explicit approval/ID/name for all targets before launching tests. Tracked preflight repeats the authoritative privacy/archive/name/member checks before every write. Independent cleanup tokens restore private unsubscribe, public rejoin and personal mute/pin state, delete temporary event queues and clear secret environment variables. Never copy this bootstrap, passwords or API keys into tracked source, logs, snapshots or command output.

For rapid native work, use App-only Debug build and the narrow affected tests. Do not run Fast for every XAML or small capability edit; run Fast at a coherent batch checkpoint and Full only before a delivery commit. Live is independent from Fast/Full and is rerun only when explicit real-write authorization remains in scope.

For Stage 24 product-polish work, preserve the latest visible message page while refreshing and treat history, mark-read, avatar media and navigation summaries as separate state transitions. Repeated activation must reach the session refresh path; own messages never create unread UI; current-visible realtime auto-read requires active-window/list visibility, a current successful history generation and a pre-event bottom position, but must not be blocked by a follow-scroll acknowledgement; SQLite remains the only cache and Zulip remains authoritative. Use narrow fake tests first, then run Fast at the requested coherent checkpoint. Live remains separate and needs explicit real-write authorization.

Visual Studio exposes two distinct native launch profiles. `Windows Machine` is the formal real-login client and must not set `RELAYCOVE_NATIVE_UI_PREVIEW`; `RelayCove Native Preview` is the Debug-only in-memory/no-network scene profile. Use the latter for Hot Reload and visual work, and the former for final Realm acceptance.

## 4. Review high-risk changes

Authentication, browser credential storage, network protocol, event synchronization, database/migrations, cache authorization, outbox, packaging and deployment/Nginx changes each require an independent read-only review. Verify findings against repository evidence and the frozen plan; advisory findings that contradict an explicit product decision must be documented and rejected with evidence, not implemented blindly.

Resolve all P0/P1 findings or record a genuine blocker. Re-run the affected narrow tests and Fast after fixes.

## 5. Evidence and handoff

Update STATUS and the active task with:

- exact commands and pass/fail counts;
- icon/package SHA-256 values;
- review scope and resolution;
- Web stack/components, login/API boundary, browser results and screenshot paths;
- external writes performed (normally none);
- formal Web message-sync implementation and fake-HTTP evidence, plus any real-account Web acceptance, MAUI UI, VM or Live gates not run;
- known limitations.

A local commit is allowed only after relevant validation. Push, merge, tag, publish, server changes, secret use and destructive cleanup require separate explicit user authorization.
