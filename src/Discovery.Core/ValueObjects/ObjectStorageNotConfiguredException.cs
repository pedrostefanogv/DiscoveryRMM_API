namespace Discovery.Core.ValueObjects;

/// <summary>
/// Object storage não configurado (ou sem credenciais) no servidor.
///
/// Existe como tipo próprio para os controllers devolverem 503 (dependência
/// indisponível, o chamador pode tentar de novo) em vez de 400/500 — antes só era
/// possível distinguir pela mensagem, e o mesmo <see cref="System.InvalidOperationException"/>
/// também significa "arquivo não encontrado no storage" ou "anexo de outra entidade".
/// Herda de InvalidOperationException para não quebrar os catches existentes.
/// </summary>
public sealed class ObjectStorageNotConfiguredException : InvalidOperationException
{
    public ObjectStorageNotConfiguredException(string message) : base(message)
    {
    }
}
