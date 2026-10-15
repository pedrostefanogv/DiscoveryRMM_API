namespace Discovery.Core.ValueObjects;

/// <summary>
/// Item listado de um bucket S3-compatível (usado pela manutenção/sanitização do
/// object storage). Não carrega o conteúdo do objeto — só chave, tamanho e data.
/// </summary>
public sealed record StorageObjectInfo(
    string ObjectKey,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc);

/// <summary>
/// Resultado de uma listagem de bucket. <c>Truncated</c> indica que o provider
/// parou no teto pedido pelo chamador (há mais objetos que os devolvidos).
/// </summary>
public sealed record ObjectStorageListing(
    IReadOnlyList<StorageObjectInfo> Items,
    bool Truncated)
{
    public static ObjectStorageListing Empty { get; } = new([], false);
}
