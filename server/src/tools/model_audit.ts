import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerModelAuditTool(server: McpServer) {
  server.tool(
    "model_audit",
    "READ-ONLY health check of the open Revit model. It never changes anything. Reports: Revit warnings grouped by type, unplaced rooms, rooms not enclosed, duplicate room numbers, empty sheets, views not on sheets, unused view templates, imported (not linked) CAD files, in-place families, level elevations in mm, file size, and an indicative health score out of 100. Each issue has a severity (high/medium/low), why it matters, how to fix it, and examples. Present the result as a short report: score first, then high issues, then medium, then low.",
    {
      maxExamples: z
        .number()
        .int()
        .min(0)
        .max(100)
        .optional()
        .default(10)
        .describe("How many example items to list per issue (default 10)"),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("model_audit", args);
        });
        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Model audit failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
        };
      }
    }
  );
}
