namespace Meduza.Core.ValueObjects;

public class ReportDocument
{
    public required byte[] Content { get; init; }
    public required string ContentType { get; init; }
    public required string FileExtension { get; init; }
}
