namespace Discovery.Core.Interfaces;

public interface IMonitoringEventNormalizationService
{
    string NormalizePayloadJson(string? payloadJson);
    string SerializeLabels(IReadOnlyCollection<string>? labels);
    IReadOnlyList<string> DeserializeLabels(string? labelsJson);
}