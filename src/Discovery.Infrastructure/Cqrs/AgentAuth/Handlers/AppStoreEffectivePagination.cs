using System.Buffers.Text;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

/// <summary>
/// Paginação do payload efetivo da app-store entregue ao agent
/// (<c>GET /api/v1/agent-auth/me/app-store</c>).
///
/// Contexto: a coleção efetiva pode passar de 27 mil itens (o catálogo winget
/// inteiro aprovado para um escopo) e, sem paginação, a API serializava tudo
/// numa única resposta de ~30 MB — o agent precisava bufferizar o corpo inteiro
/// (<c>io.ReadAll</c>) e mais uma cópia para decodificar o JSON, com pico de
/// ~95 MB de RAM por atualização de política.
///
/// O cursor é opaco (offset em base64url) e a lista é ordenada de forma
/// determinística no handler, então a mesma paginação pode ser reexecutada entre
/// requisições.
/// </summary>
internal static class AppStoreEffectivePagination
{
    /// <summary>Teto de itens por página (protege a API e o agent de respostas gigantes).</summary>
    public const int MaxPageSize = 5000;

    /// <summary>Tamanho default quando o cliente pagina e não informa pageSize.</summary>
    public const int DefaultPageSize = 1000;

    /// <summary>Aplica o default e o teto ao pageSize pedido pelo cliente.</summary>
    public static int NormalizePageSize(int requested)
        => requested <= 0 ? DefaultPageSize : Math.Min(requested, MaxPageSize);

    /// <summary>Codifica o offset (quantidade de itens já entregues) como cursor opaco.</summary>
    public static string EncodeCursor(int offset)
        => Base64Url.EncodeToString(BitConverter.GetBytes(offset));

    /// <summary>
    /// Decodifica o cursor opaco. Cursor vazio/ausente = primeira página (offset 0).
    /// Retorna false quando o cursor é inválido (o handler devolve 400).
    /// </summary>
    public static bool TryDecodeCursor(string? cursor, out int offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(cursor))
            return true;

        try
        {
            var bytes = Base64Url.DecodeFromChars(cursor.Trim());
            if (bytes.Length != sizeof(int))
                return false;
            offset = BitConverter.ToInt32(bytes, 0);
            return offset >= 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
