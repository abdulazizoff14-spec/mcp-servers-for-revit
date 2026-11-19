using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitMCPCommandSet.Services.Rooms;

namespace RevitMCPCommandSet.Commands.Rooms
{
    /// <summary>
    /// Receives rename/renumber requests and hands them to the event handler.
    /// </summary>
    public class RenameRenumberRoomsCommand : ExternalEventCommandBase
    {
        private RenameRenumberRoomsEventHandler _handler => (RenameRenumberRoomsEventHandler)Handler;

        /// <summary>Must match the name in command.json and the server tool.</summary>
        public override string CommandName => "rename_renumber_rooms";

        public RenameRenumberRoomsCommand(UIApplication uiApp)
            : base(new RenameRenumberRoomsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var changes = parameters?["changes"]?.ToObject<List<RoomChange>>();
            var auto = parameters?["autoNumber"]?.Type == JTokenType.Object
                ? parameters["autoNumber"].ToObject<AutoNumberOptions>()
                : null;
            bool dryRun = parameters?["dryRun"]?.ToObject<bool>() ?? false;

            _handler.SetParameters(changes, auto, dryRun);

            if (RaiseAndWaitForCompletion(60000))
                return _handler.Result;

            throw new TimeoutException("Rename/renumber rooms timed out");
        }
    }
}
