namespace Discovery.Core.Helpers;

/// <summary>
/// Provides sanitization utilities for values that appear in log output,
/// preventing log injection attacks via newline/control characters.
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Removes carriage return and newline characters from a string to prevent log injection.
    /// Returns null if the input is null.
    /// </summary>
    public static string? Sanitize(string? value)
        => value?.Replace("\r", string.Empty).Replace("\n", " ");
}
