namespace Discovery.Core.Entities;

/// <summary>
/// Registro de idempotência para operações mutáveis (create/comment/close).
/// Quando o cliente reenvia a mesma request com a mesma Idempotency-Key
/// (ex.: após timeout), a API devolve a resposta já persistida em vez de
/// executar a operação de novo.
/// </summary>
public class IdempotencyRecord
{
    public Guid Id { get; set; }

    /// <summary>Dono da chave (ex.: "agent:{guid}" ou "user:{guid}").</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Valor do header Idempotency-Key.</summary>
    public string Key { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Hash do método+path+payload: evita reusar a chave com request diferente.</summary>
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>-1 = em processamento; >=200 = resposta concluída.</summary>
    public int StatusCode { get; set; }

    public string? ResponseBody { get; set; }
    public string? ContentType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public bool IsCompleted => StatusCode >= 200;
}
