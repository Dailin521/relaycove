# RelayCove Engineering Workflow

## 1. Orient

Read the root plan, STATUS and active task. Run `git status --short`, confirm the expected branch, and identify unrelated user changes before editing. Stage 21 was authorized directly on local `main` as a one-time reset path; Stage 22W uses a `codex/` branch. Follow the current user authorization for each commit/push and external write.

For UI work, also read `docs/ui/README.md`, the frozen baseline manifest, `docs/ui/INTERACTION_SPEC.md` and `docs/ui/DEVELOPMENT_WORKFLOW.md`.

## 2. Implement a vertical slice

Keep changes inside one independently testable path:

1. contract/domain;
2. adapter/storage;
3. session/use case;
4. ViewModel/View;
5. tests and documentation.

Do not add speculative services, placeholder projects or features outside the active task. Use official Zulip 12.1 OpenAPI/Docs for protocol decisions and record intentional product restrictions separately from server protocol restrictions.

UI implementation follows the mandatory order `formal RelayCove.Web -> browser/user acceptance -> frozen interaction version -> native MAUI`. The Web product and MAUI share Token definitions, interaction specifications, capability matrices and acceptance scenarios, but no UI runtime code. Frozen HTML remains immutable evidence and is never embedded in a WebView. Arbitrary attachments, protected media, message actions and current-user channel unsubscribe use reviewed Stage 22W capability slices; search, mention candidates and remaining channel management still require explicit slices when they cross the current product/API boundary.

`RelayCove.Web` uses a locked TypeScript/React/Vite project. Production output must bundle icons/dependencies, exclude development fixture data and avoid runtime CDN. Formal browser API tests inject fake HTTP. The official Zulip Web stays unchanged; never add a RelayCove server, BFF or proxy to make browser tests pass.

Keep the Web delivery cadence explicit. Daily UI work uses repository-root `start-web-dev.cmd`, opens `/` and exercises the formal client against real Realm data by default; do not perform real writes unless the current task explicitly authorizes their exact scope. Deterministic fixtures are automation-only and require explicit fixture/E2E mode. A large-version manual-acceptance sync uses `deploy-web.cmd`, which must complete local verification, versioned archive/SHA-256 checks, atomic switch and HTTPS smoke checks before opening the fixed `/relaycove-web/` entrance. Never add deploy-on-save behavior. Server connection material stays in the private `server-admin` checkout and must not enter this repository or logs.

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
