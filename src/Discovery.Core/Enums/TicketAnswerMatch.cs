namespace Discovery.Core.Enums;

/// <summary>Como comparar o valor da resposta do questionário nos filtros.</summary>
public enum TicketAnswerMatch
{
    /// <summary>Igualdade exata (default, compatível com o comportamento anterior).</summary>
    Exact = 0,

    /// <summary>Contém o termo (case-insensitive) — útil para ListBox/texto longo.</summary>
    Contains = 1
}
