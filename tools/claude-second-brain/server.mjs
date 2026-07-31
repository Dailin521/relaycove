import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

import {
  consultClaude,
  efforts,
  models,
  normalizeChoice,
  normalizeTimeoutSeconds,
  perspectives
} from "./claude-runner.mjs";

const defaultModel = normalizeChoice(
  process.env.CLAUDE_SECOND_BRAIN_MODEL,
  models,
  "opus"
);
const defaultEffort = normalizeChoice(
  process.env.CLAUDE_SECOND_BRAIN_EFFORT,
  efforts,
  "high"
);
const defaultTimeout = normalizeTimeoutSeconds(
  process.env.CLAUDE_SECOND_BRAIN_TIMEOUT_SECONDS,
  240
);

const server = new McpServer(
  {
    name: "relaycove-claude-second-brain",
    version: "0.1.0"
  },
  {
    instructions:
      "Use consult_claude for independent analysis, challenges, or critical reviews after the primary agent has gathered repository facts. Claude is read-only and must not replace local verification."
  }
);

server.registerTool(
  "consult_claude",
  {
    title: "Consult Claude Second Brain",
    description:
      "Ask the local Claude Code CLI for an independent read-only analysis of RelayCove. Use for architecture tradeoffs, challenge passes, and critical reviews—not trivial questions. Repository access exposes only Read, Glob, and Grep.",
    inputSchema: {
      prompt: z.string().min(1).max(40_000),
      perspective: z.enum(perspectives).default("analysis"),
      model: z.enum(models).default(defaultModel),
      effort: z.enum(efforts).default(defaultEffort),
      repo_access: z.boolean().default(true),
      timeout_seconds: z.number().int().min(10).max(300).default(defaultTimeout)
    },
    outputSchema: {
      answer: z.string(),
      requested_model: z.string(),
      model: z.string().nullable(),
      model_mismatch: z.boolean(),
      cost_usd: z.number().nullable(),
      duration_ms: z.number().nullable(),
      warning: z.string().nullable()
    },
    annotations: {
      readOnlyHint: true,
      destructiveHint: false,
      idempotentHint: false,
      openWorldHint: true
    }
  },
  async ({
    prompt,
    perspective,
    model,
    effort,
    repo_access: repoAccess,
    timeout_seconds: timeoutSeconds
  }) => {
    try {
      const result = await consultClaude({
        prompt,
        perspective,
        model,
        effort,
        repoAccess,
        timeoutSeconds
      });
      const structuredContent = {
        answer: result.answer,
        requested_model: result.requestedModel,
        model: result.model,
        model_mismatch: result.modelMismatch,
        cost_usd: result.costUsd,
        duration_ms: result.durationMs,
        warning: result.warning
      };

      return {
        content: [
          {
            type: "text",
            text: result.answer
          }
        ],
        structuredContent
      };
    } catch (error) {
      return {
        content: [
          {
            type: "text",
            text: `Claude consultation failed: ${error.message}`
          }
        ],
        isError: true
      };
    }
  }
);

const transport = new StdioServerTransport();
await server.connect(transport);
