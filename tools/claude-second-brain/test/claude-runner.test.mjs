import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import {
  buildClaudeEnvironment,
  buildClaudeArgs,
  findWorkspaceRoot,
  normalizeChoice,
  normalizeTimeoutSeconds,
  parseClaudeResult
} from "../claude-runner.mjs";

test("findWorkspaceRoot locates the current project instead of the MCP installation", () => {
  const temporaryRoot = mkdtempSync(path.join(os.tmpdir(), "second-brain-root-"));
  const nestedPath = path.join(temporaryRoot, "src", "feature");

  try {
    mkdirSync(nestedPath, { recursive: true });
    writeFileSync(path.join(temporaryRoot, "AGENTS.md"), "# Test Project\n");

    assert.equal(findWorkspaceRoot(nestedPath), temporaryRoot);
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true });
  }
});

test("findWorkspaceRoot falls back to the launch directory without project markers", () => {
  const temporaryRoot = mkdtempSync(path.join(os.tmpdir(), "second-brain-cwd-"));

  try {
    assert.equal(findWorkspaceRoot(temporaryRoot), temporaryRoot);
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true });
  }
});

test("buildClaudeEnvironment removes inherited project roots", () => {
  const environment = buildClaudeEnvironment({
    ANTHROPIC_API_KEY: "test-key",
    CLAUDE_PROJECT_DIR: "E:\\StaleProject",
    CLAUDE_SECOND_BRAIN_SMOKE_WORKSPACE: "E:\\StaleProject",
    CODEX_WORKSPACE_ROOT: "E:\\StaleProject",
    INIT_CWD: "E:\\PackageSource",
    OLDPWD: "E:\\PreviousProject",
    PWD: "E:\\StaleProject",
    npm_config_local_prefix: "E:\\PackageSource",
    npm_package_json: "E:\\PackageSource\\package.json",
    PATH: "C:\\Tools"
  });

  assert.equal(environment.CLAUDE_PROJECT_DIR, undefined);
  assert.equal(environment.CLAUDE_SECOND_BRAIN_SMOKE_WORKSPACE, undefined);
  assert.equal(environment.CODEX_WORKSPACE_ROOT, undefined);
  assert.equal(environment.INIT_CWD, undefined);
  assert.equal(environment.OLDPWD, undefined);
  assert.equal(environment.PWD, undefined);
  assert.equal(environment.npm_config_local_prefix, undefined);
  assert.equal(environment.npm_package_json, undefined);
  assert.equal(environment.ANTHROPIC_API_KEY, "test-key");
  assert.equal(environment.PATH, "C:\\Tools");
});

test("buildClaudeArgs limits repository tools to read-only operations", () => {
  const args = buildClaudeArgs({
    prompt: "Review the synchronization design.",
    perspective: "review",
    model: "opus",
    effort: "high",
    repoAccess: true,
    workspaceRoot: "D:\\CurrentProject",
    budgetUsd: 0.25
  });

  assert.equal(args[0], "--print");
  assert.match(args[1], /Target workspace root: D:\\CurrentProject/);
  assert.equal(args[args.indexOf("--add-dir") + 1], "D:\\CurrentProject");
  assert.equal(args[args.indexOf("--setting-sources") + 1], "user");
  assert.equal(args[args.indexOf("--tools") + 1], "Read,Glob,Grep");
  assert.equal(args[args.indexOf("--permission-mode") + 1], "dontAsk");
  assert.equal(args[args.indexOf("--max-budget-usd") + 1], "0.25");
  assert.ok(!args.includes("Bash"));
  assert.ok(!args.includes("Edit"));
});

test("buildClaudeArgs can disable all repository access", () => {
  const args = buildClaudeArgs({
    prompt: "Compare two supplied options.",
    repoAccess: false,
    workspaceRoot: undefined
  });

  assert.equal(args[args.indexOf("--tools") + 1], "");
});

test("buildClaudeArgs accepts max effort for an explicit final review", () => {
  const args = buildClaudeArgs({
    prompt: "Perform a narrow final review of the supplied protocol change.",
    perspective: "review",
    effort: "max",
    repoAccess: true,
    workspaceRoot: "D:\\CurrentProject"
  });

  assert.equal(args[args.indexOf("--effort") + 1], "max");
});

test("buildClaudeArgs requires an absolute workspace for repository access", () => {
  assert.throws(
    () =>
      buildClaudeArgs({
        prompt: "Review this repository.",
        repoAccess: true,
        workspaceRoot: "relative/path"
      }),
    /workspaceRoot must be an absolute path/
  );
});

test("parseClaudeResult returns the answer and bounded metadata", () => {
  const stdout = JSON.stringify({
    is_error: false,
    subtype: "success",
    result: "Use option A.",
    total_cost_usd: 0.12,
    duration_ms: 2345,
    modelUsage: {
      "claude-opus-5": {}
    }
  });

  assert.deepEqual(parseClaudeResult(stdout, "one warning"), {
    answer: "Use option A.",
    model: "claude-opus-5",
    costUsd: 0.12,
    durationMs: 2345,
    warning: "one warning"
  });
});

test("parseClaudeResult accepts pretty-printed JSON", () => {
  const stdout = JSON.stringify(
    {
      is_error: false,
      subtype: "success",
      result: "Pretty output",
      modelUsage: {}
    },
    null,
    2
  );

  assert.equal(parseClaudeResult(stdout).answer, "Pretty output");
});

test("parseClaudeResult rejects failed CLI payloads", () => {
  const stdout = JSON.stringify({
    is_error: true,
    subtype: "error",
    result: "rate limited"
  });

  assert.throws(() => parseClaudeResult(stdout), /rate limited/);
});

test("environment defaults fall back and timeouts are clamped", () => {
  assert.equal(normalizeChoice("invalid", ["opus", "sonnet"], "opus"), "opus");
  assert.equal(normalizeChoice("sonnet", ["opus", "sonnet"], "opus"), "sonnet");
  assert.equal(normalizeTimeoutSeconds("invalid", 240), 240);
  assert.equal(normalizeTimeoutSeconds(2), 10);
  assert.equal(normalizeTimeoutSeconds(999), 300);
});
