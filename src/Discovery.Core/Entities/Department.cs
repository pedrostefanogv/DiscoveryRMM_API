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
    /// Se preenchido, este departamento herda membros e configurações de triagem
    /// de um departamento GLOBAL (ClientId = null). A herança é de um único salto:
    /// departamento global nunca herda e o pai precisa estar ativo.
    /// </summary>
    public Guid? InheritFromGlobalId { get; set; }
    
    public int SortOrder { get; set; } = 0;
    public bool IsActive { get; set; } = true;

    /// <summary>Estratégia de auto-atribuição (0=None, 1=RoundRobin, 2=LeastOpenTickets, 3=AiTriage).</summary>
    public int AssignmentStrategy { get; set; } = 0;

    /// <summary>
    /// Override da estratégia de auto-atribuição. NULL = herda a estratégia do
    /// departamento GLOBAL pai (quando o vínculo é utilizável); preenchido =
    /// o próprio valor vence. A M229 faz o backfill base -> override.
    /// </summary>
    public int? AssignmentStrategyOverride { get; set; }

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

    // ── Overrides de herança (NULL = herda do global; preenchido = próprio) ──
    //
    // As colunas acima continuam sendo o valor-base/legado do registro. As
    // colunas *_override abaixo definem se o departamento "fala por si": quando
    // NULL, a configuração efetiva vem do departamento GLOBAL pai (ou do valor
    // base, se não houver pai). A migration M229 faz backfill copiando o valor
    // atual das colunas base para os overrides de TODAS as linhas existentes,
    // preservando o comportamento anterior; só departamentos novos nascem com
    // override NULL e, portanto, herdando por padrão.

    /// <summary>Override do modo da triagem (0=Sugerir, 1=Atribuir automaticamente).</summary>
    public int? AiAssignmentModeOverride { get; set; }

    /// <summary>Override da confiança mínima da IA (0..1).</summary>
    public double? AiAssignmentMinConfidenceOverride { get; set; }

    /// <summary>Override da estratégia de fallback (1=RoundRobin, 2=LeastOpenTickets).</summary>
    public int? AiAssignmentFallbackStrategyOverride { get; set; }

    /// <summary>Override da quantidade máxima de candidatos.</summary>
    public int? AiAssignmentMaxCandidatesOverride { get; set; }

    /// <summary>Override das orientações livres do gestor.</summary>
    public string? AiAssignmentInstructionsOverride { get; set; }

    /// <summary>Override dos pesos por dimensão (JSON).</summary>
    public string? AiAssignmentWeightsJsonOverride { get; set; }

    /// <summary>Override do uso de afinidade na triagem.</summary>
    public bool? AiAssignmentUseAffinityOverride { get; set; }

    /// <summary>Override do teto de tokens de saída da triagem.</summary>
    public int? AiAssignmentMaxOutputTokensOverride { get; set; }

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

    // ── Overrides de herança do aprendizado (NULL = herda do global) ─────
    //
    // Mesmo contrato dos overrides de atribuição acima: a coluna base guarda o
    // valor legado e o *_override define se o departamento fala por si. A M229
    // faz o backfill base -> override em TODAS as linhas, preservando o
    // comportamento anterior; departamentos novos herdam por padrão.

    /// <summary>Override do modo do aprendizado de competências (0=Off, 1=Sugerir, 2=Automático).</summary>
    public int? AiSkillLearningModeOverride { get; set; }

    /// <summary>Override do mínimo de chamados resolvidos para sugerir competência.</summary>
    public int? AiSkillMinEvidenceOverride { get; set; }

    /// <summary>Override do máximo de competências sugeridas por atendente.</summary>
    public int? AiSkillMaxTagsOverride { get; set; }

    /// <summary>Override do modo da recalibração de pesos (0=Off, 1=Sugerir, 2=Automático).</summary>
    public int? AiWeightLearningModeOverride { get; set; }

    /// <summary>Override do ajuste máximo por dimensão em cada ciclo.</summary>
    public decimal? AiWeightMaxDeltaPerCycleOverride { get; set; }

    /// <summary>Override da duração do ciclo de calibração, em dias.</summary>
    public int? AiWeightCycleDaysOverride { get; set; }

    /// <summary>Override do peso mínimo permitido por dimensão.</summary>
    public decimal? AiWeightMinOverride { get; set; }

    /// <summary>Override do peso máximo permitido por dimensão.</summary>
    public decimal? AiWeightMaxOverride { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
