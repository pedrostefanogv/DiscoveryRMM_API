namespace Discovery.Core.Entities;

/// <summary>
/// Departamento de um cliente para organizar chamados.
/// Pode ser global (ClientId = null) ou específico de um cliente.
/// Suporta herança de departamentos globais.
/// </summary>
public class Department
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// ClientId do propriedário. Null = Departamento global.
    /// </summary>
    public Guid? ClientId { get; set; }
    
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    
    /// <summary>
    /// Se preenchido, este departamento herda configurações de um global.
    /// </summary>
    public Guid? InheritFromGlobalId { get; set; }
    
    public int SortOrder { get; set; } = 0;
    public bool IsActive { get; set; } = true;

    /// <summary>Estratégia de auto-atribuição (0=None, 1=RoundRobin, 2=LeastOpenTickets).</summary>
    public int AssignmentStrategy { get; set; } = 0;

    /// <summary>Último usuário atribuído (cursor do round-robin).</summary>
    public Guid? RoundRobinLastUserId { get; set; }

    // ── Triagem por IA (AssignmentStrategy = AiTriage) ───────────────────

    /// <summary>Modo da triagem por IA (0=Sugerir, 1=Atribuir automaticamente).</summary>
    public int AiAssignmentMode { get; set; } = 0;

    /// <summary>Confiança mínima da IA (0..1) para aceitar a escolha; abaixo disso usa o fallback.</summary>
    public double AiAssignmentMinConfidence { get; set; } = 0.60;

    /// <summary>Estratégia determinística usada quando a IA não pode decidir (1=RoundRobin, 2=LeastOpenTickets).</summary>
    public int AiAssignmentFallbackStrategy { get; set; } = 1;

    /// <summary>Quantidade máxima de candidatos enviados ao modelo.</summary>
    public int AiAssignmentMaxCandidates { get; set; } = 8;

    /// <summary>Pesos por dimensão do score (JSON; null = pesos padrão).</summary>
    public string? AiAssignmentWeightsJson { get; set; }

    /// <summary>Orientações livres do gestor, anexadas ao prompt de triagem.</summary>
    public string? AiAssignmentInstructions { get; set; }

    /// <summary>Usa afinidade com chamados semelhantes já resolvidos pelo atendente.</summary>
    public bool AiAssignmentUseAffinity { get; set; } = true;

    /// <summary>
    /// Teto de tokens de SAÍDA da triagem neste departamento. É combinado com a
    /// capacidade real do modelo (nunca a ultrapassa) — ver AiTokenLimits.
    /// </summary>
    public int AiAssignmentMaxOutputTokens { get; set; } = 1200;

    // ── Aprendizado: competências e pesos ────────────────────────────────

    /// <summary>Modo do aprendizado de competências (0=Off, 1=Sugerir, 2=Automático).</summary>
    public int AiSkillLearningMode { get; set; } = 1;

    /// <summary>Mínimo de chamados resolvidos para sugerir uma competência.</summary>
    public int AiSkillMinEvidence { get; set; } = 3;

    /// <summary>Máximo de competências sugeridas por atendente.</summary>
    public int AiSkillMaxTags { get; set; } = 12;

    /// <summary>Modo da recalibração de pesos (0=Off, 1=Sugerir, 2=Automático).</summary>
    public int AiWeightLearningMode { get; set; } = 1;

    /// <summary>Ajuste máximo por dimensão em cada ciclo de calibração.</summary>
    public decimal AiWeightMaxDeltaPerCycle { get; set; } = 0.10m;

    /// <summary>Duração do ciclo de calibração, em dias.</summary>
    public int AiWeightCycleDays { get; set; } = 7;

    /// <summary>Peso mínimo permitido por dimensão.</summary>
    public decimal AiWeightMin { get; set; } = 0.05m;

    /// <summary>Peso máximo permitido por dimensão.</summary>
    public decimal AiWeightMax { get; set; } = 0.50m;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
