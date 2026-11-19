using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitMCPCommandSet.Services.Audit;

namespace RevitMCPCommandSet.Commands.Audit
{
    /// <summary>
    /// Read-only model health check. Receives the request and hands it to the event handler.
    /// </summary>
    public class ModelAuditCommand : ExternalEventCommandBase
    {
        private ModelAuditEventHandler _handler => (ModelAuditEventHandler)Handler;

        /// <summary>Must match the name in command.json and the server tool.</summary>
        public override string CommandName => "model_audit";

        public ModelAuditCommand(UIApplication uiApp)
            : base(new ModelAuditEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            int maxExamples = parameters?["maxExamples"]?.ToObject<int>() ?? 10;
            _handler.SetParameters(maxExamples);

            // Big models take longer: allow 2 minutes
            if (RaiseAndWaitForCompletion(120000))
                return _handler.Result;

            throw new TimeoutException("Model audit timed out");
        }
    }
}
