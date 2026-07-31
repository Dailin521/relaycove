# Repository Guidelines

## Read Order and Scope

Before changing the repository, read this file, the relevant section of
`RelayCove_工程落地方案.md`, `docs/ai/STATUS.md`, and the active task record.
The specification is the product and architecture source of truth. Follow
`docs/ai/WORKFLOW.md` for execution, evidence, review, and handoff rules.
Implement one independently verifiable vertical slice at a time; do not add
unrequested infrastructure, abstractions, or placeholder directories.

## Project Structure

RelayCove is currently design-first. Planned modules are:

- `src/RelayCove.Client/` — Windows WPF UI, notifications, and local cache.
- `src/RelayCove.Server/` — ASP.NET Core API, SignalR, and persistence.
- `src/RelayCove.Shared/` — DTOs, enums, constants, and protocol contracts.
- `src/RelayCove.Updater/` — minimal Windows update launcher.
- `tests/<Project>.Tests/` — tests mirroring source projects.
- `docs/`, `scripts/`, and `installer/` — guidance, automation, and packaging.

## Build, Test, and Style

No runnable solution exists yet. After stage 0 scaffolding, use
`pwsh ./scripts/verify.ps1 -Mode Fast` during development and `-Mode Full`
before commits. Until then, do not claim that the project builds or tests.

Use four-space indentation, file-scoped namespaces, nullable reference types,
and one public type per file. Use `PascalCase` for types and public members,
`camelCase` for locals, `I` for interfaces, and `Async` for asynchronous
methods. Keep I/O asynchronous and logged; never block the WPF UI thread.
Name xUnit tests `Method_WhenCondition_ExpectedResult`. Bug fixes require
regression tests.

## Agent Evidence and Safety

Work on `agent/stage-<number>-<slug>` branches. Inspect `git status` and the
baseline before editing. Repository facts outrank official documentation,
which outranks explicitly labeled assumptions. Mark conclusions as
`已验证`, `未验证`, or `假设`; report a check as passing only after running it.
Stop for unrelated dirty changes, failing baselines, secrets, destructive
actions, ambiguous acceptance criteria, compatibility changes, or new major
dependencies. Local commits are allowed after validation; pushing and merging
require explicit user approval.

## Commits and Pull Requests

Use short imperative commits such as `Add message deduplication`. Keep unrelated
changes separate. Pull requests must explain the reason, impact, verification,
limitations, and development stage; link issues and include screenshots for
WPF changes. Authentication, migrations, synchronization, notifications,
updates, and deployment require an independent review.
