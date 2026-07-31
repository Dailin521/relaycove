import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const testDir = path.dirname(fileURLToPath(import.meta.url));
const toolDir = path.resolve(testDir, "..");
const serverPath = path.join(toolDir, "server.mjs");

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [serverPath],
  cwd: toolDir,
  env: {
    ...process.env,
    CLAUDE_SECOND_BRAIN_MODEL:
      process.env.CLAUDE_SECOND_BRAIN_SMOKE_MODEL || "opus",
    CLAUDE_SECOND_BRAIN_EFFORT: "low",
    CLAUDE_SECOND_BRAIN_MAX_BUDGET_USD: "0.10",
    CLAUDE_SECOND_BRAIN_TIMEOUT_SECONDS: "60"
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
      prompt:
        "Read AGENTS.md and return exactly its first Markdown heading without the leading # or any other text.",
      perspective: "analysis",
      effort: "low",
      repo_access: true,
      timeout_seconds: 60
    }
  });

  if (result.isError) {
    throw new Error(`MCP tool returned an error: ${JSON.stringify(result)}`);
  }
  assert.equal(result.structuredContent.answer.trim(), "Repository Guidelines");
  assert.equal(result.structuredContent.requested_model, "opus");
  assert.match(result.structuredContent.model, /^claude-/);
  assert.equal(result.structuredContent.model_mismatch, false);

  process.stdout.write(
    `${JSON.stringify({
      ok: true,
      tool: "consult_claude",
      requested_model: result.structuredContent.requested_model,
      model: result.structuredContent.model,
      model_mismatch: result.structuredContent.model_mismatch,
      cost_usd: result.structuredContent.cost_usd,
      duration_ms: result.structuredContent.duration_ms
    })}\n`
  );
} finally {
  await client.close();
}
