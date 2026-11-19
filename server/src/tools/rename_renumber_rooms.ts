import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerRenameRenumberRoomsTool(server: McpServer) {
  server.tool(
    "rename_renumber_rooms",
    "List, rename and renumber Revit rooms. (1) Call with no 'changes' and no 'autoNumber' to LIST all rooms with their id, level, number and name. (2) Use 'changes' for specific rooms, found by roomId or currentNumber. (3) Use 'autoNumber' to number every placed room on one level in order (e.g. prefix 'G', digits 2 gives G01, G02…). The tool never creates duplicate room numbers: clashing changes are refused with a note. Use dryRun=true to preview the plan first, especially for autoNumber. When applied, all changes are ONE transaction, so a single Ctrl+Z undoes them.",
    {
      changes: z
        .array(
          z.object({
            roomId: z.number().int().optional().describe("Room element id (most reliable)"),
            currentNumber: z.string().optional().describe("Current room number, used if roomId is not given"),
            newNumber: z.string().optional().describe("New room number"),
            newName: z.string().optional().describe("New room name"),
          })
        )
        .optional()
        .describe("Specific room changes"),
      autoNumber: z
        .object({
          level: z.string().describe("Level name, e.g. 'Level 0'"),
          prefix: z.string().optional().default("").describe("Text before the number, e.g. 'G' or '1'"),
          start: z.number().int().optional().default(1).describe("First number (default 1)"),
          digits: z.number().int().min(1).max(6).optional().default(2).describe("Minimum digits, e.g. 2 gives 01"),
          sortBy: z
            .enum(["leftToRight", "topToBottom", "currentNumber"])
            .optional()
            .default("leftToRight")
            .describe("Order to number rooms in"),
        })
        .optional()
        .describe("Number all placed rooms on one level in order"),
      dryRun: z
        .boolean()
        .optional()
        .default(false)
        .describe("If true, only show what would change. Nothing is changed in Revit."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("rename_renumber_rooms", args);
        });
        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Rename/renumber rooms failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
        };
      }
    }
  );
}
