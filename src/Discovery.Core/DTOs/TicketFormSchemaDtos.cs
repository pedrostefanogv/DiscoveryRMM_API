namespace Discovery.Core.DTOs;

/// <summary>
/// Opção de um campo de seleção do formulário de abertura de chamado do agent.
/// O servidor é a fonte da verdade: o agent renderiza exatamente as opções que
/// vierem daqui (nada de lista fixa embutida no agent).
/// </summary>
public sealed record TicketFormFieldOptionDto(string Value, string Label);

/// <summary>
/// Descritor de um campo do formulário de abertura de chamado do agent:
/// chave estável, rótulo, tipo de controle, obrigatoriedade, visibilidade,
/// valor padrão e opções (quando for seleção).
/// </summary>
public sealed record TicketFormFieldDto(
    string Key,
    string Label,
    string Type,
    bool Required,
    bool Visible,
    string? DefaultValue,
    IReadOnlyList<TicketFormFieldOptionDto> Options);

/// <summary>
/// Schema do formulário de abertura de chamado de um departamento. Como as
/// opções variam por departamento (modelos e histórico do próprio
/// departamento), o agent só busca o schema depois que o departamento é
/// escolhido.
/// </summary>
public sealed record TicketFormSchemaDto(
    string DepartmentId,
    IReadOnlyList<TicketFormFieldDto> Fields);
