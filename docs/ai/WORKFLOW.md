# Stage 21 Engineering Workflow

## 1. Orient

Read the root plan, STATUS and active task. Run `git status --short`, confirm the expected branch, and identify unrelated user changes before editing. The current reset is authorized directly on local `main`; this does not authorize pushing or future direct-main work.

## 2. Implement a vertical slice

Keep changes inside one independently testable path:

1. contract/domain;
2. adapter/storage;
3. session/use case;
4. ViewModel/View;
5. tests and documentation.

Do not add speculative services, placeholder projects or features outside the frozen MVP. Use official Zulip 12.1 OpenAPI/Docs for protocol decisions and record intentional product restrictions separately from server protocol restrictions.

## 3. Verify locally

Run the narrowest relevant tests first. Then run:

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
```

Fast must be deterministic, offline-safe and exclude `RelayCove.Zulip.LiveTests`.

Before delivery, run:

```powershell
pwsh ./scripts/verify.ps1 -Mode Full
```

Full performs Release build/tests, app-project-only publish, ZIP creation, content checks and secret scan. A solution-level `dotnet publish` is prohibited.

Run Live only with explicit authorization and complete isolated configuration:

```powershell
pwsh ./scripts/verify.ps1 -Mode Live
```

Missing host, two dedicated account credentials, recipient allowlist or write confirmation is a failure, not a skip.

## 4. Review high-risk changes

Authentication, network protocol, event synchronization, database/migrations, cache authorization, outbox and packaging each require an independent read-only review. Verify findings against repository evidence and the frozen plan; advisory findings that contradict an explicit product decision must be documented and rejected with evidence, not implemented blindly.

Resolve all P0/P1 findings or record a genuine blocker. Re-run the affected narrow tests and Fast after fixes.

## 5. Evidence and handoff

Update STATUS and the active task with:

- exact commands and pass/fail counts;
- icon/package SHA-256 values;
- review scope and resolution;
- external writes performed (normally none);
- VM, Live or UI gates not run;
- known limitations.

A local commit is allowed only after relevant validation. Push, merge, tag, publish, server changes, secret use and destructive cleanup require separate explicit user authorization.
