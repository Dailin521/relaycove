import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const testDir = path.dirname(fileURLToPath(import.meta.url));
const toolDir = path.resolve(testDir, "..");
const serverPath =
  process.env.CLAUDE_SECOND_BRAIN_SMOKE_SERVER_PATH ||
  path.join(toolDir, "server.mjs");
const workspaceRoot =
  process.env.CLAUDE_SECOND_BRAIN_SMOKE_WORKSPACE ||
  path.resolve(toolDir, "..", "..");
const expectedAnswer =
  process.env.CLAUDE_SECOND_BRAIN_SMOKE_EXPECTED_ANSWER ||
  process.env.CLAUDE_SECOND_BRAIN_SMOKE_EXPECTED_HEADING ||
  "RELAYCOVE";
const smokePrompt =
  process.env.CLAUDE_SECOND_BRAIN_SMOKE_PROMPT ||
  "Read the target workspace AGENTS.md. Return exactly RELAYCOVE if it contains the planned path src/RelayCove.Client; otherwise return exactly WRONG_WORKSPACE.";

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [serverPath],
  cwd: workspaceRoot,
  env: {
    ...process.env,
    CLAUDE_SECOND_BRAIN_MODEL:
      process.env.CLAUDE_SECOND_BRAIN_SMOKE_MODEL || "opus",
    CLAUDE_SECOND_BRAIN_EFFORT:
      process.env.CLAUDE_SECOND_BRAIN_SMOKE_EFFORT || "low",
    CLAUDE_SECOND_BRAIN_MAX_BUDGET_USD:
      process.env.CLAUDE_SECOND_BRAIN_SMOKE_MAX_BUDGET_USD || "0.10",
    CLAUDE_SECOND_BRAIN_TIMEOUT_SECONDS:
      process.env.CLAUDE_SECOND_BRAIN_SMOKE_TIMEOUT_SECONDS || "60"
  }
});

const client = new Client({
  name: "claude-second-brain-smoke-test",
  version: "0.1.0"
});

try {
  await client.connect(transport);

  const tools = await client.listTools();
  assert.ok(tools.tools.some((tool) => tool.name === "consult_claude"));

  const result = await client.callTool({
    name: "consult_claude",
    arguments: {
      prompt: smokePrompt,
      perspective: "analysis",
      repo_access: true,
      timeout_seconds: Number(
        process.env.CLAUDE_SECOND_BRAIN_SMOKE_TIMEOUT_SECONDS || 60
      )
    }
  });

  if (result.isError) {
    throw new Error(`MCP tool returned an error: ${JSON.stringify(result)}`);
  }
  assert.equal(
    path.resolve(result.structuredContent.workspace_root),
    path.resolve(workspaceRoot)
  );
  assert.equal(result.structuredContent.answer.trim(), expectedAnswer);
  assert.equal(result.structuredContent.requested_model, "opus");
  assert.equal(
    result.structuredContent.requested_effort,
    process.env.CLAUDE_SECOND_BRAIN_SMOKE_EFFORT || "low"
  );
  assert.match(result.structuredContent.model, /^claude-/);
  assert.equal(result.structuredContent.model_mismatch, false);

  process.stdout.write(
    `${JSON.stringify({
      ok: true,
      tool: "consult_claude",
      requested_model: result.structuredContent.requested_model,
      requested_effort: result.structuredContent.requested_effort,
      model: result.structuredContent.model,
      model_mismatch: result.structuredContent.model_mismatch,
      cost_usd: result.structuredContent.cost_usd,
      duration_ms: result.structuredContent.duration_ms
    })}\n`
  );
} finally {
  await client.close();
}
