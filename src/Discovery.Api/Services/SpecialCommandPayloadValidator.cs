using System.Text.Json;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;

namespace Discovery.Api.Services;

public sealed class SpecialCommandPayloadValidator
{
    public bool TryNormalize(
        CommandType commandType,
        string payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = payload;
        validationError = string.Empty;

        if (!CommandTypeWireMapper.IsSpecialCommand(commandType))
            return true;

        if (string.IsNullOrWhiteSpace(payload))
        {
            validationError = "payload must be a non-empty JSON object.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            validationError = $"payload must be valid JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                validationError = "payload must be a JSON object.";
                return false;
            }

            return commandType switch
            {
                CommandType.RemoteDebug => RemoteSessionPayloadValidator.TryNormalizeRemoteDebug(document.RootElement, out normalizedPayload, out validationError),
                CommandType.ShowPsadtAlert => NotificationAndAlertPayloadValidator.TryNormalizePsadtAlert(document.RootElement, out normalizedPayload, out validationError),
                CommandType.Notification => NotificationAndAlertPayloadValidator.TryNormalizeNotification(document.RootElement, out normalizedPayload, out validationError),
                CommandType.Update => PowerAndUpdatePayloadValidator.TryNormalizeUpdate(document.RootElement, out normalizedPayload, out validationError),
                CommandType.Restart => PowerAndUpdatePayloadValidator.TryNormalizeRestart(document.RootElement, out normalizedPayload, out validationError),
                CommandType.Shutdown => PowerAndUpdatePayloadValidator.TryNormalizeShutdown(document.RootElement, out normalizedPayload, out validationError),
                CommandType.WakeOnLan => PowerAndUpdatePayloadValidator.TryNormalizeWakeOnLan(document.RootElement, out normalizedPayload, out validationError),
                CommandType.RemoteSessionStart => RemoteSessionPayloadValidator.TryNormalizeRemoteSession(document.RootElement, out normalizedPayload, out validationError),
                CommandType.RemoteSessionStop => RemoteSessionPayloadValidator.TryNormalizeRemoteSession(document.RootElement, out normalizedPayload, out validationError),
                CommandType.RemoteSessionQuality => RemoteSessionPayloadValidator.TryNormalizeRemoteSession(document.RootElement, out normalizedPayload, out validationError),
                CommandType.RecordingStart => RemoteSessionPayloadValidator.TryNormalizeRemoteSession(document.RootElement, out normalizedPayload, out validationError),
                CommandType.RecordingStop => RemoteSessionPayloadValidator.TryNormalizeRemoteSession(document.RootElement, out normalizedPayload, out validationError),
                CommandType.P2pPreload => RemoteSessionPayloadValidator.TryNormalizeP2pPreload(document.RootElement, out normalizedPayload, out validationError),
                CommandType.StartupItem => SoftwareAndAutomationPayloadValidator.TryNormalizeStartupItem(document.RootElement, out normalizedPayload, out validationError),
                CommandType.ScheduledTask => SoftwareAndAutomationPayloadValidator.TryNormalizeScheduledTask(document.RootElement, out normalizedPayload, out validationError),
                CommandType.SoftwareUpdate => SoftwareAndAutomationPayloadValidator.TryNormalizeSoftwareUpdate(document.RootElement, out normalizedPayload, out validationError),
                CommandType.SoftwareUninstall => SoftwareAndAutomationPayloadValidator.TryNormalizeSoftwareUninstall(document.RootElement, out normalizedPayload, out validationError),
                CommandType.DecommissionAgent => PowerAndUpdatePayloadValidator.TryNormalizeDecommission(document.RootElement, out normalizedPayload, out validationError),
                CommandType.TicketChatInvite => NotificationAndAlertPayloadValidator.TryNormalizeTicketChatInvite(document.RootElement, out normalizedPayload, out validationError),
                CommandType.WindowsUpdate => PowerAndUpdatePayloadValidator.TryNormalizeWindowsUpdate(document.RootElement, out normalizedPayload, out validationError),
                CommandType.SoftwareInstall => SoftwareAndAutomationPayloadValidator.TryNormalizeSoftwareInstall(document.RootElement, out normalizedPayload, out validationError),
                CommandType.RunSensors => SoftwareAndAutomationPayloadValidator.TryNormalizeRunSensors(document.RootElement, out normalizedPayload, out validationError),
                CommandType.PowerAction => PowerAndUpdatePayloadValidator.TryNormalizePowerActionCommand(document.RootElement, out normalizedPayload, out validationError),
                _ => true
            };
        }
    }
}
