import assert from "node:assert/strict";
import test from "node:test";

import {
  buildClaudeArgs,
  findWorkspaceRoot,
  normalizeChoice,
  normalizeTimeoutSeconds,
  parseClaudeResult
} from "../claude-runner.mjs";

test("findWorkspaceRoot locates the RelayCove repository", () => {
  const root = findWorkspaceRoot();
  assert.match(root, /RelayCove$/i);
});

test("buildClaudeArgs limits repository tools to read-only operations", () => {
  const args = buildClaudeArgs({
    prompt: "Review the synchronization design.",
    perspective: "review",
    model: "opus",
    effort: "high",
    repoAccess: true,
    budgetUsd: 0.25
  });

  assert.equal(args[0], "Review the synchronization design.");
  assert.equal(args[args.indexOf("--tools") + 1], "Read,Glob,Grep");
  assert.equal(args[args.indexOf("--permission-mode") + 1], "dontAsk");
  assert.equal(args[args.indexOf("--max-budget-usd") + 1], "0.25");
  assert.ok(!args.includes("Bash"));
  assert.ok(!args.includes("Edit"));
});

test("buildClaudeArgs can disable all repository access", () => {
  const args = buildClaudeArgs({
    prompt: "Compare two supplied options.",
    repoAccess: false
  });

  assert.equal(args[args.indexOf("--tools") + 1], "");
});

test("buildClaudeArgs accepts max effort for an explicit final review", () => {
  const args = buildClaudeArgs({
    prompt: "Perform a narrow final review of the supplied protocol change.",
    perspective: "review",
    effort: "max",
    repoAccess: true
  });

  assert.equal(args[args.indexOf("--effort") + 1], "max");
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
