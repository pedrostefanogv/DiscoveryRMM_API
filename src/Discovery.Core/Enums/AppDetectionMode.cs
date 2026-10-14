namespace Discovery.Core.Enums;

/// <summary>
/// Estratégia de detecção de instalação de um pacote custom usada pelo agent.
/// </summary>
public enum AppDetectionMode
{
    /// <summary>Sem detecção (comportamento original).</summary>
    None = 0,

    /// <summary>
    /// Vínculo com um app do inventário local: casa por nome normalizado
    /// (<c>DetectionName</c>) e, quando informado, fabricante (<c>DetectionPublisher</c>);
    /// com <c>DetectionMinVersion</c> a versão instalada precisa ser maior ou igual.
    /// </summary>
    InventoryLink = 1,

    /// <summary>Executa <c>DetectionCommand</c> (PowerShell); exit 0 = já instalado.</summary>
    Command = 2
}
