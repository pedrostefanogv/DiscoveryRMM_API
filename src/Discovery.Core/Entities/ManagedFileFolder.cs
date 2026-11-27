using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Pasta lógica da Central de Arquivos S3 (e do Portal Público /download),
/// permitindo criar pastas vazias, renomear em cascata e organizar arquivos por hierarquia.
/// </summary>
public class ManagedFileFolder
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public string ParentPath { get; set; } = "/";
    public string? Description { get; set; }

    public FileScope Scope { get; set; } = FileScope.Global;
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }

    public FileVisibility Visibility { get; set; } = FileVisibility.Public;

    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
