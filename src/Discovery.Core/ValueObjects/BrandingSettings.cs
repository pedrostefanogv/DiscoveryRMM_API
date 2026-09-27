namespace Discovery.Core.ValueObjects;

/// <summary>
/// Configurações de branding da aplicação.
/// </summary>
public class BrandingSettings
{
    public string ApplicationName { get; set; } = "Discovery";
    public string? LogoUrl { get; set; }
    public string PrimaryColor { get; set; } = "#6366f1"; // Indigo (base do ícone)
    public string SecondaryColor { get; set; } = "#ec4899"; // Magenta/Pink (destaque do ícone)
    public string? CompanyName { get; set; }
    public string? CompanyWebsite { get; set; }
    public string? SupportEmail { get; set; }
}
