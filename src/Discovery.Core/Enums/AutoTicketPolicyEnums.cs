namespace Discovery.Core.Enums;

/// <summary>
/// Modo de operação da política de auto-ticket no nível resolvido.
/// Espelha a escada de rollout: Off -> Shadow (observa) -> Canary (escopo
/// restrito) -> Active (cria de fato).
/// </summary>
public enum AutoTicketScopeMode
{
    /// <summary>Nunca avalia nem cria. Nem grava execução de tentativa.</summary>
    Off = 0,

    /// <summary>Avalia e registra "o que teria acontecido", mas nunca cria ticket.</summary>
    Shadow = 1,

    /// <summary>Cria apenas para os clientes/sites do canário.</summary>
    Canary = 2,

    /// <summary>Cria conforme as demais regras da política.</summary>
    Active = 3
}

/// <summary>
/// Impacto do sensor/evento. Alimenta a escada de decisão (magnitude) e serve
/// de piso para a prioridade sugerida pela IA.
/// </summary>
public enum AutoTicketImpact
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

/// <summary>Nível de autonomia da IA no enriquecimento do chamado.</summary>
public enum AutoTicketAiMode
{
    /// <summary>Nem sugere nem aplica.</summary>
    Off = 0,

    /// <summary>Sugere e registra; o atendente aceita ou ignora.</summary>
    Suggest = 1,

    /// <summary>Aplica automaticamente quando a confiança atinge o limiar.</summary>
    AutoApply = 2
}

/// <summary>Veredito do avaliador de sinal — quem DECIDE se o chamado abre.</summary>
public enum AutoTicketSignalDecision
{
    /// <summary>Sinal persistente e acionável: segue para criação.</summary>
    Open = 0,

    /// <summary>Ainda não persistiu o bastante (aguarda).</summary>
    Hold = 1,

    /// <summary>Suprimido por manutenção, dependência, flapping ou não-acionabilidade.</summary>
    Suppress = 2,

    /// <summary>Sinal persistente e grave: cria com prioridade elevada.</summary>
    Escalate = 3
}

/// <summary>
/// Origem da supressão. Torna o motivo auditável e permite medir cada causa
/// separadamente nas métricas.
/// </summary>
public enum AutoTicketSuppressionSource
{
    None = 0,

    /// <summary>Janela de manutenção ativa (agente, site, cliente ou servidor).</summary>
    Maintenance = 1,

    /// <summary>Agente/pai/rota não alcançável — supressão em cascata.</summary>
    Dependency = 2,

    /// <summary>Flapping: transições demais na janela.</summary>
    Flapping = 3,

    /// <summary>Falha transitória que não persistiu.</summary>
    Transient = 4,

    /// <summary>Sensor marcado como "nunca abre chamado" ou fora da allowlist.</summary>
    NotActionable = 5,

    /// <summary>Evento de resolução (não abre chamado novo).</summary>
    Resolved = 6
}

/// <summary>Política de auto-ticket no nível do SENSOR.</summary>
public enum SensorAutoTicketPolicy
{
    /// <summary>Este sensor nunca abre chamado.</summary>
    Never = 0,

    /// <summary>Abre apenas na abertura/escala (comportamento atual).</summary>
    OnEscalation = 1,

    /// <summary>Abre em qualquer transição de alerta, inclusive a primeira.</summary>
    Always = 2
}
