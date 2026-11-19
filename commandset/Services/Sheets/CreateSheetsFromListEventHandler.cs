using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Sheets
{
    /// <summary>
    /// One sheet the user asked for.
    /// </summary>
    public class SheetRequest
    {
        public string Number { get; set; }
        public string Name { get; set; }
        /// <summary>Optional: name of a view or schedule to place in the middle of the sheet.</summary>
        public string ViewName { get; set; }
    }

    /// <summary>
    /// What happened to one requested sheet.
    /// </summary>
    public class SheetResult
    {
        public string Number { get; set; }
        public string Name { get; set; }
        public int? Id { get; set; }
        /// <summary>"created", "skipped" or "failed".</summary>
        public string Status { get; set; }
        public string PlacedView { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// Creates many sheets in one go. Everything happens in ONE transaction,
    /// so a single Ctrl+Z in Revit undoes the whole job.
    /// Each sheet runs in its own sub-transaction, so one bad sheet does not stop the others.
    /// </summary>
    public class CreateSheetsFromListEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication _uiApp;
        private Document _doc => _uiApp.ActiveUIDocument.Document;

        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private List<SheetRequest> _sheets;
        private string _titleBlockName;
        private bool _skipExisting;

        public AIResult<List<SheetResult>> Result { get; private set; }

        public void SetParameters(List<SheetRequest> sheets, string titleBlockName, bool skipExisting)
        {
            _sheets = sheets;
            _titleBlockName = titleBlockName;
            _skipExisting = skipExisting;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            _uiApp = uiapp;
            var results = new List<SheetResult>();

            try
            {
                // 1. Find the title block to use
                var titleBlocks = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsElementType()
                    .Cast<FamilySymbol>()
                    .ToList();

                FamilySymbol titleBlock = null;
                if (!string.IsNullOrWhiteSpace(_titleBlockName))
                {
                    titleBlock = titleBlocks.FirstOrDefault(t =>
                        t.Name.Equals(_titleBlockName, StringComparison.OrdinalIgnoreCase) ||
                        $"{t.FamilyName}: {t.Name}".Equals(_titleBlockName, StringComparison.OrdinalIgnoreCase) ||
                        t.FamilyName.Equals(_titleBlockName, StringComparison.OrdinalIgnoreCase));

                    if (titleBlock == null)
                    {
                        var available = string.Join(", ", titleBlocks.Select(t => $"'{t.FamilyName}: {t.Name}'"));
                        Result = new AIResult<List<SheetResult>>
                        {
                            Success = false,
                            Message = $"Title block '{_titleBlockName}' not found. Available: {(available.Length > 0 ? available : "none loaded")}",
                            Response = results
                        };
                        return;
                    }
                }
                else
                {
                    titleBlock = titleBlocks.FirstOrDefault();
                }

                ElementId titleBlockId = titleBlock?.Id ?? ElementId.InvalidElementId;

                // 2. Collect what already exists
                var existingNumbers = new HashSet<string>(
                    new FilteredElementCollector(_doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Select(s => s.SheetNumber),
                    StringComparer.OrdinalIgnoreCase);

                var views = new FilteredElementCollector(_doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && !(v is ViewSheet))
                    .ToList();

                // 3. Do the work in one transaction = one undo
                using (var tx = new Transaction(_doc, "Claude: Create sheets from list"))
                {
                    tx.Start();

                    foreach (var req in _sheets)
                    {
                        var res = new SheetResult { Number = req.Number, Name = req.Name };
                        results.Add(res);

                        if (string.IsNullOrWhiteSpace(req.Number))
                        {
                            res.Status = "failed";
                            res.Note = "Sheet number is empty";
                            continue;
                        }

                        if (existingNumbers.Contains(req.Number))
                        {
                            res.Status = _skipExisting ? "skipped" : "failed";
                            res.Note = "A sheet with this number already exists";
                            continue;
                        }

                        using (var st = new SubTransaction(_doc))
                        {
                            st.Start();
                            try
                            {
                                ViewSheet sheet = ViewSheet.Create(_doc, titleBlockId);
                                sheet.SheetNumber = req.Number;
                                if (!string.IsNullOrWhiteSpace(req.Name))
                                    sheet.Name = req.Name;

                                if (!string.IsNullOrWhiteSpace(req.ViewName))
                                    PlaceView(sheet, req.ViewName, views, res);

                                st.Commit();
                                existingNumbers.Add(req.Number);
                                res.Id = sheet.Id.GetIntValue();
                                res.Status = "created";
                            }
                            catch (Exception ex)
                            {
                                st.RollBack();
                                res.Status = "failed";
                                res.Note = ex.Message;
                                res.PlacedView = null;
                            }
                        }
                    }

                    tx.Commit();
                }

                int created = results.Count(r => r.Status == "created");
                int skipped = results.Count(r => r.Status == "skipped");
                int failed = results.Count(r => r.Status == "failed");
                string tbText = titleBlock != null ? $"title block '{titleBlock.FamilyName}: {titleBlock.Name}'" : "NO title block (none loaded in project)";

                Result = new AIResult<List<SheetResult>>
                {
                    Success = failed == 0,
                    Message = $"Created {created}, skipped {skipped}, failed {failed}. Used {tbText}. One Ctrl+Z in Revit undoes this whole job.",
                    Response = results
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<List<SheetResult>>
                {
                    Success = false,
                    Message = $"Create sheets failed: {ex.Message}",
                    Response = results
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        /// <summary>
        /// Puts a view (or schedule) in the middle of the sheet.
        /// </summary>
        private void PlaceView(ViewSheet sheet, string viewName, List<View> views, SheetResult res)
        {
            View view = views.FirstOrDefault(v => v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase));
            if (view == null)
            {
                res.Note = $"View '{viewName}' not found, sheet created empty";
                return;
            }

            _doc.Regenerate(); // so the sheet outline is known
            XYZ centre = SheetCentre(sheet);

            if (view is ViewSchedule schedule)
            {
                ScheduleSheetInstance.Create(_doc, sheet.Id, schedule.Id, centre);
                res.PlacedView = view.Name;
            }
            else if (Viewport.CanAddViewToSheet(_doc, sheet.Id, view.Id))
            {
                Viewport.Create(_doc, sheet.Id, view.Id, centre);
                res.PlacedView = view.Name;
            }
            else
            {
                res.Note = $"View '{viewName}' is already on another sheet (a view can only be on one sheet), sheet created empty";
            }
        }

        private static XYZ SheetCentre(ViewSheet sheet)
        {
            BoundingBoxUV o = sheet.Outline;
            if (o != null && o.Max.U - o.Min.U > 0.01)
                return new XYZ((o.Min.U + o.Max.U) / 2, (o.Min.V + o.Max.V) / 2, 0);

            // Fallback: centre of an A1 sheet (841 x 594 mm), in feet
            return new XYZ(841 / 2.0 / 304.8, 594 / 2.0 / 304.8, 0);
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName() => "Create Sheets From List";
    }
}
