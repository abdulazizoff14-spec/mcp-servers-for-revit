import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCreateSheetsFromListTool(server: McpServer) {
  server.tool(
    "create_sheets_from_list",
    "Create many Revit sheets in one step from a list of sheet numbers and names, optionally placing one view or schedule in the centre of each sheet. All sheets are created in ONE transaction, so the user can undo the whole job with a single Ctrl+Z. Existing sheet numbers are skipped by default. A view can only sit on one sheet; if it is already placed, the sheet is created empty and a note explains why.",
    {
      sheets: z
        .array(
          z.object({
            number: z.string().describe("Sheet number, e.g. 'A101'"),
            name: z.string().describe("Sheet name, e.g. 'Ground Floor Plan'"),
            viewName: z
              .string()
              .optional()
              .describe("Optional exact name of a view or schedule to place on this sheet, e.g. 'Level 0'"),
          })
        )
        .min(1)
        .describe("The sheets to create"),
      titleBlockName: z
        .string()
        .optional()
        .describe("Optional title block, as 'Family: Type', type name or family name. If omitted, the first title block in the project is used."),
      skipExisting: z
        .boolean()
        .optional()
        .default(true)
        .describe("If true (default), sheet numbers that already exist are skipped instead of reported as failures"),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_sheets_from_list", args);
        });
        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Create sheets failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
        };
      }
    }
  );
}
