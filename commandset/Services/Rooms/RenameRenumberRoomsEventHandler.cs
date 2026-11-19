using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Rooms
{
    /// <summary>One room change. Find the room by RoomId, or by its CurrentNumber.</summary>
    public class RoomChange
    {
        public int? RoomId { get; set; }
        public string CurrentNumber { get; set; }
        public string NewNumber { get; set; }
        public string NewName { get; set; }
    }

    /// <summary>Automatic numbering of all placed rooms on one level.</summary>
    public class AutoNumberOptions
    {
        public string Level { get; set; }
        /// <summary>Text before the number, e.g. "G" gives G01, G02…</summary>
        public string Prefix { get; set; } = "";
        public int Start { get; set; } = 1;
        /// <summary>Minimum digits, e.g. 2 gives 01, 02…</summary>
        public int Digits { get; set; } = 2;
        /// <summary>"leftToRight", "topToBottom" or "currentNumber".</summary>
        public string SortBy { get; set; } = "leftToRight";
    }

    public class RoomChangeResult
    {
        public int Id { get; set; }
        public string Level { get; set; }
        public string OldNumber { get; set; }
        public string OldName { get; set; }
        public string NewNumber { get; set; }
        public string NewName { get; set; }
        /// <summary>"changed", "planned" (dry run), "unchanged" or "failed".</summary>
        public string Status { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// Renames and renumbers rooms. All changes happen in ONE transaction (one Ctrl+Z).
    /// Never creates duplicate room numbers. With DryRun it only shows the plan.
    /// With no changes and no auto-numbering it just lists the rooms.
    /// </summary>
    public class RenameRenumberRoomsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication _uiApp;
        private Document _doc => _uiApp.ActiveUIDocument.Document;
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private List<RoomChange> _changes;
        private AutoNumberOptions _auto;
        private bool _dryRun;

        public AIResult<List<RoomChangeResult>> Result { get; private set; }

        public void SetParameters(List<RoomChange> changes, AutoNumberOptions auto, bool dryRun)
        {
            _changes = changes ?? new List<RoomChange>();
            _auto = auto;
            _dryRun = dryRun;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            _uiApp = uiapp;
            var results = new List<RoomChangeResult>();

            try
            {
                var rooms = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .ToList();

                // --- No instructions: just list the rooms so Claude can plan ---
                if (_changes.Count == 0 && _auto == null)
                {
                    foreach (var room in rooms.OrderBy(x => x.Level?.Elevation ?? 0).ThenBy(x => x.Number))
                        results.Add(Snapshot(room, "unchanged", room.Location == null ? "unplaced" : null));

                    Result = Ok($"Listed {results.Count} rooms. No changes made.", results);
                    return;
                }

                // --- 1. Build the plan: room -> (new number, new name) ---
                var plan = new Dictionary<ElementId, RoomChangeResult>();

                foreach (var ch in _changes)
                {
                    Room room = null;
                    if (ch.RoomId.HasValue)
                    {
                        room = rooms.FirstOrDefault(x => x.Id.GetIntValue() == ch.RoomId.Value);
                    }
                    else if (!string.IsNullOrWhiteSpace(ch.CurrentNumber))
                    {
                        var matches = rooms.Where(x => x.Number == ch.CurrentNumber).ToList();
                        if (matches.Count > 1)
                        {
                            results.Add(new RoomChangeResult
                            {
                                OldNumber = ch.CurrentNumber, Status = "failed",
                                Note = $"{matches.Count} rooms have number '{ch.CurrentNumber}'. Use roomId instead: {string.Join(", ", matches.Select(m => m.Id.GetIntValue()))}"
                            });
                            continue;
                        }
                        room = matches.FirstOrDefault();
                    }

                    if (room == null)
                    {
                        results.Add(new RoomChangeResult
                        {
                            OldNumber = ch.CurrentNumber, Status = "failed",
                            Note = $"Room not found ({(ch.RoomId.HasValue ? "id " + ch.RoomId : "number '" + ch.CurrentNumber + "'")})"
                        });
                        continue;
                    }

                    var r = GetOrAdd(plan, room);
                    if (!string.IsNullOrWhiteSpace(ch.NewNumber)) r.NewNumber = ch.NewNumber.Trim();
                    if (!string.IsNullOrWhiteSpace(ch.NewName)) r.NewName = ch.NewName.Trim();
                }

                if (_auto != null)
                {
                    var level = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>()
                        .FirstOrDefault(l => l.Name.Equals(_auto.Level ?? "", StringComparison.OrdinalIgnoreCase));
                    if (level == null)
                    {
                        Result = new AIResult<List<RoomChangeResult>>
                        {
                            Success = false,
                            Message = $"Level '{_auto.Level}' not found.",
                            Response = results
                        };
                        return;
                    }

                    var onLevel = rooms.Where(x => x.LevelId == level.Id && x.Location is LocationPoint).ToList();
                    IEnumerable<Room> ordered;
                    switch ((_auto.SortBy ?? "").ToLowerInvariant())
                    {
                        case "toptobottom":
                            ordered = onLevel.OrderByDescending(x => Pt(x).Y).ThenBy(x => Pt(x).X);
                            break;
                        case "currentnumber":
                            ordered = onLevel.OrderBy(x => x.Number, new NaturalComparer());
                            break;
                        default:
                            ordered = onLevel.OrderBy(x => Pt(x).X).ThenByDescending(x => Pt(x).Y);
                            break;
                    }

                    int n = _auto.Start;
                    foreach (var room in ordered)
                    {
                        var r = GetOrAdd(plan, room);
                        r.NewNumber = (_auto.Prefix ?? "") + n.ToString().PadLeft(Math.Max(1, _auto.Digits), '0');
                        n++;
                    }
                }

                // --- 2. Never allow duplicate numbers ---
                // Final number of every room = planned number, or its current one.
                var finalNumber = rooms.ToDictionary(x => x.Id, x => plan.TryGetValue(x.Id, out var p) ? p.NewNumber : x.Number);
                var clashes = rooms
                    .Where(x => x.Location != null && !string.IsNullOrEmpty(finalNumber[x.Id]))
                    .GroupBy(x => finalNumber[x.Id])
                    .Where(g => g.Count() > 1);

                foreach (var g in clashes)
                {
                    foreach (var room in g.Where(x => plan.ContainsKey(x.Id) && plan[x.Id].NewNumber != plan[x.Id].OldNumber))
                    {
                        var r = plan[room.Id];
                        var others = g.Where(o => o.Id != room.Id).Select(o => $"{o.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()} (id {o.Id.GetIntValue()})");
                        r.Status = "failed";
                        r.Note = $"Number '{r.NewNumber}' would duplicate {string.Join(", ", others)}";
                    }
                }

                // --- 3. Apply (or just preview) ---
                var toApply = plan.Values.Where(r => r.Status != "failed").ToList();
                foreach (var r in toApply)
                {
                    if (r.NewNumber == r.OldNumber && r.NewName == r.OldName)
                    {
                        r.Status = "unchanged";
                    }
                }
                var changes = toApply.Where(r => r.Status != "unchanged").ToList();

                if (_dryRun)
                {
                    changes.ForEach(r => r.Status = "planned");
                }
                else if (changes.Count > 0)
                {
                    using (var tx = new Transaction(_doc, "Claude: Rename/renumber rooms"))
                    {
                        tx.Start();
                        foreach (var r in changes)
                        {
                            var room = rooms.First(x => x.Id.GetIntValue() == r.Id);
                            try
                            {
                                if (r.NewNumber != r.OldNumber) room.Number = r.NewNumber;
                                if (r.NewName != r.OldName) room.get_Parameter(BuiltInParameter.ROOM_NAME).Set(r.NewName);
                                r.Status = "changed";
                            }
                            catch (Exception ex)
                            {
                                r.Status = "failed";
                                r.Note = ex.Message;
                            }
                        }
                        tx.Commit();
                    }
                }

                results.AddRange(plan.Values.OrderBy(r => r.Level).ThenBy(r => r.NewNumber, new NaturalComparer()));

                int done = results.Count(r => r.Status == "changed");
                int planned = results.Count(r => r.Status == "planned");
                int failed = results.Count(r => r.Status == "failed");
                string msg = _dryRun
                    ? $"PREVIEW ONLY: {planned} room(s) would change, {failed} problem(s). Nothing was changed. Run again with dryRun=false to apply."
                    : $"Changed {done} room(s), {failed} failed. One Ctrl+Z in Revit undoes all of it.";

                Result = new AIResult<List<RoomChangeResult>> { Success = failed == 0, Message = msg, Response = results };
            }
            catch (Exception ex)
            {
                Result = new AIResult<List<RoomChangeResult>> { Success = false, Message = $"Rename/renumber failed: {ex.Message}", Response = results };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private static AIResult<List<RoomChangeResult>> Ok(string msg, List<RoomChangeResult> list) =>
            new AIResult<List<RoomChangeResult>> { Success = true, Message = msg, Response = list };

        private static RoomChangeResult Snapshot(Room room, string status, string note)
        {
            string name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
            return new RoomChangeResult
            {
                Id = room.Id.GetIntValue(),
                Level = room.Level?.Name,
                OldNumber = room.Number,
                OldName = name,
                NewNumber = room.Number,
                NewName = name,
                Status = status,
                Note = note
            };
        }

        private static RoomChangeResult GetOrAdd(Dictionary<ElementId, RoomChangeResult> plan, Room room)
        {
            if (!plan.TryGetValue(room.Id, out var r))
            {
                string name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                r = new RoomChangeResult
                {
                    Id = room.Id.GetIntValue(),
                    Level = room.Level?.Name,
                    OldNumber = room.Number,
                    OldName = name,
                    NewNumber = room.Number,
                    NewName = name
                };
                plan[room.Id] = r;
            }
            return r;
        }

        private static XYZ Pt(Room room) => ((LocationPoint)room.Location).Point;

        /// <summary>Sorts "2" before "10" (normal text sorting would not).</summary>
        private class NaturalComparer : IComparer<string>
        {
            public int Compare(string a, string b)
            {
                a ??= ""; b ??= "";
                int i = 0, j = 0;
                while (i < a.Length && j < b.Length)
                {
                    if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                    {
                        int si = i, sj = j;
                        while (i < a.Length && char.IsDigit(a[i])) i++;
                        while (j < b.Length && char.IsDigit(b[j])) j++;
                        int c = long.Parse(a.Substring(si, Math.Min(18, i - si))).CompareTo(long.Parse(b.Substring(sj, Math.Min(18, j - sj))));
                        if (c != 0) return c;
                    }
                    else
                    {
                        int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                        if (c != 0) return c;
                        i++; j++;
                    }
                }
                return (a.Length - i).CompareTo(b.Length - j);
            }
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName() => "Rename Renumber Rooms";
    }
}
