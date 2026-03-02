namespace Meduza.Core.Helpers;

public static class IdGenerator
{
    /// <summary>
    /// Gera UUID v7 (time-ordered) usando a API nativa do .NET 9+.
    /// </summary>
    public static Guid NewId() => Guid.CreateVersion7();
}
