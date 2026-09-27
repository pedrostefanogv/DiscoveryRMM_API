namespace Discovery.Core.Entities;

/// <summary>Membro (usuário) de um departamento — usado pelo round-robin de atribuição.</summary>
public class DepartmentMember
{
    public Guid Id { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid UserId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    // ── Perfil usado pela triagem por IA ─────────────────────────────────

    /// <summary>Tags de competência do atendente (JSON array de strings).</summary>
    public string? SkillTagsJson { get; set; }

    /// <summary>Nível de experiência nas competências cadastradas (1..5).</summary>
    public int SkillLevel { get; set; } = 3;

    /// <summary>Teto de chamados abertos simultâneos (null = sem teto).</summary>
    public int? MaxOpenTickets { get; set; }

    /// <summary>Peso/preferência do gestor na priorização (0.1 .. 3.0).</summary>
    public decimal Weight { get; set; } = 1.0m;

    /// <summary>Se falso, o atendente não é escolhido automaticamente pela IA.</summary>
    public bool AcceptsAiAssignment { get; set; } = true;
}
