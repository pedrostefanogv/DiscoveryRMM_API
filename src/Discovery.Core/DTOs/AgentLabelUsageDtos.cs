namespace Discovery.Core.DTOs;

/// <summary>Label e quantos agentes a possuem. Alimenta o filtro de labels da UI sem carregar todas as labels.</summary>
public class AgentLabelUsageDto
{
    public string Label { get; set; } = string.Empty;
    public int AgentCount { get; set; }
}

/// <summary>Ids de agentes que possuem uma label, com cursor para a proxima pagina.</summary>
public class AgentIdsByLabelResponse
{
    public string Label { get; set; } = string.Empty;
    public int Total { get; set; }
    public IReadOnlyList<Guid> AgentIds { get; set; } = [];
    public Guid? NextCursor { get; set; }
    public bool HasMore { get; set; }
    public int Limit { get; set; }
}
