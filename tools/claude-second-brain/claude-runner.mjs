import { execFile } from "node:child_process";
import { existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const DEFAULT_MAX_BUFFER_BYTES = 10 * 1024 * 1024;
const DEFAULT_TIMEOUT_SECONDS = 240;
const MAX_TIMEOUT_SECONDS = 300;

export const perspectives = ["analysis", "review", "challenge", "brainstorm"];
export const models = ["opus", "sonnet"];
export const efforts = ["low", "medium", "high", "xhigh", "max"];

const perspectiveInstructions = {
  analysis:
    "Analyze the question independently. Return verified facts, assumptions, risks, and a concrete recommendation.",
  review:
    "Act as a strict independent reviewer. Lead with actionable findings, cite repository paths and symbols, and identify missing verification.",
  challenge:
    "Challenge the proposed approach. Search for counterexamples, hidden assumptions, failure modes, and simpler alternatives.",
  brainstorm:
    "Generate a small set of distinct options. Compare tradeoffs and recommend one option without inventing repository facts."
};

export function normalizeTimeoutSeconds(value, fallback = DEFAULT_TIMEOUT_SECONDS) {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) {
    return fallback;
  }

  return Math.min(MAX_TIMEOUT_SECONDS, Math.max(10, Math.trunc(parsed)));
}

export function normalizeChoice(value, allowed, fallback) {
  return allowed.includes(value) ? value : fallback;
}

function readPositiveNumber(value, fallback) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

export function findWorkspaceRoot(startPath = path.dirname(fileURLToPath(import.meta.url))) {
  let current = path.resolve(startPath);

  while (true) {
    if (existsSync(path.join(current, ".git")) || existsSync(path.join(current, "AGENTS.md"))) {
      return current;
    }

    const parent = path.dirname(current);
    if (parent === current) {
      throw new Error(`Could not locate repository root from ${startPath}`);
    }

    current = parent;
  }
}

export function buildClaudeArgs({
  prompt,
  perspective = "analysis",
  model = process.env.CLAUDE_SECOND_BRAIN_MODEL || "opus",
  effort = process.env.CLAUDE_SECOND_BRAIN_EFFORT || "xhigh",
  repoAccess = true,
  budgetUsd = readPositiveNumber(
    process.env.CLAUDE_SECOND_BRAIN_MAX_BUDGET_USD,
    0.5
  )
}) {
  if (typeof prompt !== "string" || prompt.trim().length === 0) {
    throw new Error("prompt must be a non-empty string");
  }
  if (!perspectives.includes(perspective)) {
    throw new Error(`Unsupported perspective: ${perspective}`);
  }
  if (!models.includes(model)) {
    throw new Error(`Unsupported model: ${model}`);
  }
  if (!efforts.includes(effort)) {
    throw new Error(`Unsupported effort: ${effort}`);
  }

  const systemPrompt = [
    "You are an independent second brain for the RelayCove project.",
    "Treat the repository as read-only. Never claim you edited files or ran commands you could not run.",
    "Read AGENTS.md and relevant docs when repository access is enabled.",
    "Keep the response concise and separate verified facts from assumptions.",
    perspectiveInstructions[perspective]
  ].join("\n");

  return [
    prompt.trim(),
    "--print",
    "--output-format",
    "json",
    "--no-session-persistence",
    "--permission-mode",
    "dontAsk",
    "--no-chrome",
    "--model",
    model,
    "--effort",
    effort,
    "--max-budget-usd",
    String(budgetUsd),
    "--append-system-prompt",
    systemPrompt,
    "--tools",
    repoAccess ? "Read,Glob,Grep" : ""
  ];
}

function summarizeStderr(stderr) {
  const lines = String(stderr ?? "")
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);

  return lines.slice(-5).join("\n").slice(0, 2000);
}

export function parseClaudeResult(stdout, stderr = "") {
  const raw = String(stdout ?? "").trim();
  let payload;

  try {
    payload = JSON.parse(raw);
  } catch {
    const candidates = raw
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.startsWith("{") && line.endsWith("}"));

    if (candidates.length === 0) {
      throw new Error("Claude CLI did not return a JSON result");
    }

    try {
      payload = JSON.parse(candidates.at(-1));
    } catch (error) {
      throw new Error(`Claude CLI returned invalid JSON: ${error.message}`);
    }
  }

  if (payload.is_error || payload.subtype !== "success" || typeof payload.result !== "string") {
    const detail =
      payload.result ||
      payload.api_error_status ||
      payload.terminal_reason ||
      "unknown Claude CLI error";
    throw new Error(`Claude CLI failed: ${detail}`);
  }

  const usedModel = Object.keys(payload.modelUsage ?? {})[0] ?? null;
  const warning = summarizeStderr(stderr);

  return {
    answer: payload.result,
    model: usedModel,
    costUsd:
      typeof payload.total_cost_usd === "number" ? payload.total_cost_usd : null,
    durationMs: typeof payload.duration_ms === "number" ? payload.duration_ms : null,
    warning: warning || null
  };
}

export async function consultClaude({
  prompt,
  perspective,
  model,
  effort,
  repoAccess,
  timeoutSeconds = readPositiveNumber(
    process.env.CLAUDE_SECOND_BRAIN_TIMEOUT_SECONDS,
    DEFAULT_TIMEOUT_SECONDS
  ),
  workspaceRoot = findWorkspaceRoot(),
  command = process.env.CLAUDE_CLI_COMMAND || "claude"
}) {
  const requestedModel =
    model === undefined
      ? normalizeChoice(
          process.env.CLAUDE_SECOND_BRAIN_MODEL,
          models,
          "opus"
        )
      : model;
  const requestedEffort =
    effort === undefined
      ? normalizeChoice(
          process.env.CLAUDE_SECOND_BRAIN_EFFORT,
          efforts,
          "xhigh"
        )
      : effort;
  const args = buildClaudeArgs({
    prompt,
    perspective,
    model: requestedModel,
    effort: requestedEffort,
    repoAccess
  });
  const timeout = normalizeTimeoutSeconds(timeoutSeconds) * 1000;

  return new Promise((resolve, reject) => {
    const child = execFile(
      command,
      args,
      {
        cwd: workspaceRoot,
        encoding: "utf8",
        maxBuffer: DEFAULT_MAX_BUFFER_BYTES,
        timeout,
        windowsHide: true
      },
      (error, stdout, stderr) => {
        if (error) {
          const detail = summarizeStderr(stderr) || error.message;
          if (error.code === "ERR_CHILD_PROCESS_STDIO_MAXBUFFER") {
            reject(
              new Error(
                `Claude CLI exceeded the ${DEFAULT_MAX_BUFFER_BYTES} byte output limit: ${detail}`
              )
            );
            return;
          }

          if (error.killed) {
            reject(
              new Error(
                `Claude CLI timed out after ${timeout / 1000} seconds: ${detail}`
              )
            );
            return;
          }

          const commandHint =
            error.code === "ENOENT"
              ? ` Verify CLAUDE_CLI_COMMAND or install the native Claude CLI.`
              : "";
          reject(new Error(`Claude CLI process failed: ${detail}.${commandHint}`));
          return;
        }

        try {
          const result = parseClaudeResult(stdout, stderr);
          const modelMismatch =
            result.model !== null &&
            !result.model.toLowerCase().includes(requestedModel.toLowerCase());
          const mismatchWarning = modelMismatch
            ? `Requested Claude model '${requestedModel}' but the CLI reported '${result.model}'.`
            : null;

          resolve({
            ...result,
            requestedModel,
            requestedEffort,
            modelMismatch,
            warning: [result.warning, mismatchWarning].filter(Boolean).join("\n") || null
          });
        } catch (parseError) {
          reject(parseError);
        }
      }
    );

    child.stdin?.end();
  });
}
