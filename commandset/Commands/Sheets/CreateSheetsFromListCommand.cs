using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitMCPCommandSet.Services.Sheets;

namespace RevitMCPCommandSet.Commands.Sheets
{
    /// <summary>
    /// Receives the request from Claude and hands it to the event handler,
    /// which runs safely on Revit's main thread.
    /// </summary>
    public class CreateSheetsFromListCommand : ExternalEventCommandBase
    {
        private CreateSheetsFromListEventHandler _handler => (CreateSheetsFromListEventHandler)Handler;

        /// <summary>Must match the name in command.json and the server tool.</summary>
        public override string CommandName => "create_sheets_from_list";

        public CreateSheetsFromListCommand(UIApplication uiApp)
            : base(new CreateSheetsFromListEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var sheets = parameters["sheets"]?.ToObject<List<SheetRequest>>();
            if (sheets == null || sheets.Count == 0)
                throw new ArgumentException("No sheets provided");

            string titleBlockName = parameters["titleBlockName"]?.ToString();
            bool skipExisting = parameters["skipExisting"]?.ToObject<bool>() ?? true;

            _handler.SetParameters(sheets, titleBlockName, skipExisting);

            // Big lists take longer: allow 60 seconds
            if (RaiseAndWaitForCompletion(60000))
                return _handler.Result;

            throw new TimeoutException("Create sheets timed out");
        }
    }
}
