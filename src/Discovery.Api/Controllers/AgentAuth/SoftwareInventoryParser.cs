using System.Globalization;
using Discovery.Core.Entities;

namespace Discovery.Api.Controllers;

internal static class SoftwareInventoryParser
{
    public static SoftwareInventoryEntry ToEntry(SoftwareInventoryItemRequest item)
    {
        return new SoftwareInventoryEntry
        {
            Name = item.Name,
            Version = item.Version,
            Publisher = item.Publisher,
            InstallId = item.InstallId,
            Serial = item.Serial,
            Source = item.Source,
            InstallDate = ParseInstallDate(item.InstallDate),
            InstallSource = item.InstallSource
        };
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
