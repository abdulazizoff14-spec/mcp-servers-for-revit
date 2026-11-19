using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Audit
{
    /// <summary>One problem the audit found.</summary>
    public class AuditIssue
    {
        /// <summary>"high", "medium" or "low".</summary>
        public string Severity { get; set; }
        public string Check { get; set; }
        public int Count { get; set; }
        public string Why { get; set; }
        public string Fix { get; set; }
        public List<string> Examples { get; set; } = new List<string>();
    }

    public class AuditReport
    {
        public string ProjectName { get; set; }
        public string FilePath { get; set; }
        public double? FileSizeMB { get; set; }
        public bool IsWorkshared { get; set; }
        public string RevitVersion { get; set; }
        /// <summary>0-100. Indicative only: starts at 100 and loses points per issue.</summary>
        public int HealthScore { get; set; }
        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
        public List<object> Levels { get; set; } = new List<object>();
        public List<AuditIssue> Issues { get; set; } = new List<AuditIssue>();
    }

    /// <summary>
    /// READ-ONLY model health check. It opens no transaction, so it can never change the model.
    /// </summary>
    public class ModelAuditEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication _uiApp;
        private Document _doc => _uiApp.ActiveUIDocument.Document;
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        private int _maxExamples = 10;

        public AIResult<AuditReport> Result { get; private set; }

        public void SetParameters(int maxExamples)
        {
            _maxExamples = Math.Max(0, maxExamples);
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            _uiApp = uiapp;
            try
            {
                var r = new AuditReport
                {
                    ProjectName = _doc.Title,
                    FilePath = _doc.PathName,
                    IsWorkshared = _doc.IsWorkshared,
                    RevitVersion = uiapp.Application.VersionName
                };
                if (!string.IsNullOrEmpty(_doc.PathName) && File.Exists(_doc.PathName))
                    r.FileSizeMB = Math.Round(new FileInfo(_doc.PathName).Length / 1048576.0, 1);

                int penalty = 0;

                // --- Levels (in mm, not Revit's internal feet) ---
                foreach (var lvl in new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation))
                    r.Levels.Add(new { lvl.Name, ElevationMm = Math.Round(lvl.Elevation * 304.8) });

                // --- 1. Revit warnings ---
                var warnings = _doc.GetWarnings();
                r.Counts["Warnings"] = warnings.Count;
                if (warnings.Count > 0)
                {
                    var groups = warnings
                        .GroupBy(w => w.GetDescriptionText())
                        .OrderByDescending(g => g.Count())
                        .Select(g => $"{g.Count()} x {g.Key}");
                    AddIssue(r, warnings.Count > 50 ? "high" : "medium", "Revit warnings", warnings.Count,
                        "Warnings slow the model down and often hide real errors (overlaps, duplicates).",
                        "Open Manage > Warnings and fix the biggest groups first.", groups);
                    penalty += Math.Min(25, warnings.Count / 4 + 1);
                }

                // --- 2. Rooms ---
                var rooms = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .ToList();
                r.Counts["Rooms"] = rooms.Count;

                var unplaced = rooms.Where(x => x.Location == null).ToList();
                if (unplaced.Count > 0)
                {
                    AddIssue(r, "medium", "Unplaced rooms", unplaced.Count,
                        "Rooms that exist in schedules but not on any plan. They give wrong room counts.",
                        "Place them in a plan, or delete them.", unplaced.Select(Describe));
                    penalty += Math.Min(10, unplaced.Count * 2);
                }

                var notEnclosed = rooms.Where(x => x.Location != null && x.Area <= 0).ToList();
                if (notEnclosed.Count > 0)
                {
                    AddIssue(r, "high", "Rooms not enclosed", notEnclosed.Count,
                        "Placed rooms with zero area: walls or room separators are missing. Areas will be wrong.",
                        "Close the gaps with walls or room separation lines.", notEnclosed.Select(Describe));
                    penalty += Math.Min(10, notEnclosed.Count * 2);
                }

                var dupNumbers = rooms
                    .Where(x => x.Location != null && !string.IsNullOrWhiteSpace(x.Number))
                    .GroupBy(x => x.Number)
                    .Where(g => g.Count() > 1)
                    .ToList();
                if (dupNumbers.Count > 0)
                {
                    AddIssue(r, "medium", "Duplicate room numbers", dupNumbers.Count,
                        "Two or more rooms share a number. Schedules and door numbering become confusing.",
                        "Renumber the rooms so each number is unique.",
                        dupNumbers.Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Name))}"));
                    penalty += Math.Min(10, dupNumbers.Count * 3);
                }

                // --- 3. Sheets and views ---
                var sheets = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
                r.Counts["Sheets"] = sheets.Count;

                var placedViewIds = new HashSet<ElementId>();
                foreach (var s in sheets)
                    foreach (var id in s.GetAllPlacedViews())
                        placedViewIds.Add(id);

                var schedulesOnSheets = new HashSet<ElementId>(
                    new FilteredElementCollector(_doc).OfClass(typeof(ScheduleSheetInstance))
                        .Cast<ScheduleSheetInstance>().Select(i => i.ScheduleId));

                var emptySheets = sheets
                    .Where(s => s.GetAllPlacedViews().Count == 0 &&
                                !new FilteredElementCollector(_doc, s.Id).OfClass(typeof(ScheduleSheetInstance)).Any())
                    .ToList();
                if (emptySheets.Count > 0)
                {
                    AddIssue(r, "low", "Empty sheets", emptySheets.Count,
                        "Sheets with no views or schedules. They clutter the drawing set.",
                        "Add views or delete them.", emptySheets.Select(s => $"{s.SheetNumber} - {s.Name}"));
                    penalty += Math.Min(5, emptySheets.Count);
                }

                var drawableTypes = new HashSet<ViewType>
                {
                    ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.Elevation, ViewType.Section,
                    ViewType.ThreeD, ViewType.DraftingView, ViewType.Detail, ViewType.AreaPlan, ViewType.EngineeringPlan
                };
                var allViews = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
                r.Counts["Views"] = allViews.Count(v => drawableTypes.Contains(v.ViewType));

                var notOnSheets = allViews
                    .Where(v => drawableTypes.Contains(v.ViewType) && !placedViewIds.Contains(v.Id))
                    .ToList();
                if (notOnSheets.Count > 0)
                {
                    AddIssue(r, "low", "Views not on sheets", notOnSheets.Count,
                        "Working views are normal, but many unused views make the model heavy and hard to navigate.",
                        "Put needed views on sheets; delete or group the rest in the Project Browser.",
                        notOnSheets.Select(v => $"{v.ViewType}: {v.Name}"));
                    penalty += Math.Min(10, notOnSheets.Count / 5);
                }

                var templates = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();
                var usedTemplates = new HashSet<ElementId>(allViews.Select(v => v.ViewTemplateId).Where(id => id != ElementId.InvalidElementId));
                var unusedTemplates = templates.Where(t => !usedTemplates.Contains(t.Id)).ToList();
                r.Counts["ViewTemplates"] = templates.Count;
                if (unusedTemplates.Count > 0)
                {
                    AddIssue(r, "low", "Unused view templates", unusedTemplates.Count,
                        "Templates no view uses. Often leftovers from old projects or imports.",
                        "Keep the firm's standard ones; delete the rest.", unusedTemplates.Select(t => t.Name));
                    penalty += Math.Min(5, unusedTemplates.Count / 2);
                }

                // --- 4. CAD imports ---
                var imports = new FilteredElementCollector(_doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
                var importedNotLinked = imports.Where(i => !i.IsLinked).ToList();
                r.Counts["CadLinks"] = imports.Count - importedNotLinked.Count;
                r.Counts["CadImports"] = importedNotLinked.Count;
                if (importedNotLinked.Count > 0)
                {
                    AddIssue(r, "medium", "Imported CAD files", importedNotLinked.Count,
                        "Imported (not linked) DWGs bring in layers, line styles and junk that bloat the model.",
                        "Delete them and use Link CAD instead.",
                        importedNotLinked.Select(i => _doc.GetElement(i.GetTypeId())?.Name ?? $"Id {i.Id.GetIntValue()}"));
                    penalty += Math.Min(10, importedNotLinked.Count * 3);
                }

                // --- 5. In-place families ---
                var inPlace = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().Where(f => f.IsInPlace).ToList();
                r.Counts["InPlaceFamilies"] = inPlace.Count;
                if (inPlace.Count > 0)
                {
                    AddIssue(r, "medium", "In-place families", inPlace.Count,
                        "In-place families can't be scheduled or reused well and slow the model when copied.",
                        "Rebuild repeated ones as loadable families.", inPlace.Select(f => f.Name));
                    penalty += Math.Min(10, inPlace.Count * 2);
                }

                // --- Simple counts for context ---
                r.Counts["Walls"] = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().GetElementCount();
                r.Counts["Doors"] = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().GetElementCount();
                r.Counts["Windows"] = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().GetElementCount();
                r.Counts["Families"] = new FilteredElementCollector(_doc).OfClass(typeof(Family)).GetElementCount();

                r.HealthScore = Math.Max(0, 100 - penalty);
                var order = new Dictionary<string, int> { ["high"] = 0, ["medium"] = 1, ["low"] = 2 };
                r.Issues = r.Issues.OrderBy(i => order[i.Severity]).ThenByDescending(i => i.Count).ToList();

                Result = new AIResult<AuditReport>
                {
                    Success = true,
                    Message = $"Audit complete: health score {r.HealthScore}/100, {r.Issues.Count} issue type(s) found. Read-only: nothing in the model was changed.",
                    Response = r
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<AuditReport> { Success = false, Message = $"Model audit failed: {ex.Message}" };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private void AddIssue(AuditReport r, string severity, string check, int count, string why, string fix, IEnumerable<string> examples)
        {
            r.Issues.Add(new AuditIssue
            {
                Severity = severity,
                Check = check,
                Count = count,
                Why = why,
                Fix = fix,
                Examples = examples.Take(_maxExamples).ToList()
            });
        }

        private static string Describe(Room room)
        {
            string level = room.Level?.Name ?? "no level";
            return $"{room.Number} {room.Name} ({level}, Id {room.Id.GetIntValue()})";
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName() => "Model Audit";
    }
}
