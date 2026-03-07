namespace Meduza.Core.ValueObjects;

public class ReportQueryResult
{
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
}
