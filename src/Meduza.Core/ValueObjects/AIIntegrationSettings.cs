namespace Meduza.Core.ValueObjects;

/// <summary>
/// Configurações de integração com IA e servidores MSP.
/// </summary>
public class AIIntegrationSettings
{
    /// <summary>Habilita recursos de IA (chat, análise, etc)</summary>
    public bool Enabled { get; set; } = false;
    
    /// <summary>Habilita Chat IA para usuários</summary>
    public bool ChatAIEnabled { get; set; } = false;
    
    /// <summary>Habilita Base de Conhecimento (assistido por IA)</summary>
    public bool KnowledgeBaseEnabled { get; set; } = false;
    
    /// <summary>Lista de servidores MSP para processamento de IA</summary>
    public string[] MSPServers { get; set; } = [];
    
    /// <summary>Timeout para chamadas de IA (milissegundos)</summary>
    public int TimeoutMs { get; set; } = 30000; // 30s
    
    /// <summary>Máximo de tokens por requisição</summary>
    public int MaxTokensPerRequest { get; set; } = 2000;
}
