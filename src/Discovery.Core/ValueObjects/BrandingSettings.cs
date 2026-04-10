namespace Discovery.Core.ValueObjects;

/// <summary>
/// Configurações de branding da aplicação.
/// </summary>
public class BrandingSettings
{
    public string ApplicationName { get; set; } = "Discovery";
    public string? LogoUrl { get; set; }
    public string PrimaryColor { get; set; } = "#1E90FF"; // Dodger Blue
    public string SecondaryColor { get; set; } = "#FFD700"; // Gold
    public string? CompanyName { get; set; }
    public string? CompanyWebsite { get; set; }
    public string? SupportEmail { get; set; }
}
