using System.Globalization;
using Discovery.Core.Entities;

namespace Discovery.Api.Controllers;

internal static class SoftwareInventoryParser
{
    /// <summary>Limites alinhados com as colunas do banco (software_catalog / HasMaxLength).</summary>
    private const int MaxName = 300;
    private const int MaxVersion = 120;
    private const int MaxPublisher = 300;
    private const int MaxInstallId = 1000;
    private const int MaxSerial = 1000;
    private const int MaxSource = 120;
    private const int MaxInstallSource = 2000;

    public static SoftwareInventoryEntry ToEntry(SoftwareInventoryItemRequest item)
    {
        return new SoftwareInventoryEntry
        {
            Name = Truncate(item.Name, MaxName),
            Version = Truncate(item.Version, MaxVersion),
            Publisher = Truncate(item.Publisher, MaxPublisher),
            InstallId = Truncate(item.InstallId, MaxInstallId),
            Serial = Truncate(item.Serial, MaxSerial),
            Source = Truncate(item.Source, MaxSource),
            InstallDate = ParseInstallDate(item.InstallDate),
            InstallSource = Truncate(item.InstallSource, MaxInstallSource)
        };
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return value.Length > maxLength ? value[..maxLength] : value;
    }

    public static DateTime? ParseInstallDate(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var value = rawValue.Trim();

        if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compactDate))
            return DateTime.SpecifyKind(compactDate.Date, DateTimeKind.Utc);

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return null;

        return DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
    }
}
