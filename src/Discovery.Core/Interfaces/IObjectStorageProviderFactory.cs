using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Factory para criar instâncias de IObjectStorageService baseado no provider ativo.
/// Permite trocar provedor apenas alterando ServerConfiguration, sem mudar código.
/// </summary>
public interface IObjectStorageProviderFactory
{
    /// <summary>
    /// Criar instância de storage service para o provider atualmente configurado.
    /// </summary>
    /// <returns>IObjectStorageService pronto para uso</returns>
    IObjectStorageService CreateObjectStorageService();

    /// <summary>
    /// Criar instância de storage service para o provider atualmente configurado sem bloqueio síncrono.
    /// </summary>
    Task<IObjectStorageService> CreateObjectStorageServiceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validar se a configuração é válida para o provider ativo.
    /// </summary>
    /// <returns>Lista de erros (vazio se válida)</returns>
    Task<List<string>> ValidateConfigurationAsync();

    /// <summary>
    /// Testar conectividade com o storage usando as configurações atuais.
    /// Valida os campos obrigatórios e tenta acessar o bucket configurado.
    /// Não lança exceção — retorna resultado estruturado mesmo em caso de falha.
    /// </summary>
    Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Invalida o cache em memória da instância de storage ativa.</summary>
    void InvalidateCache() { }

    /// <summary>Resolve a configuração efetiva (banco + env no modo interno).</summary>
    Task<ObjectStorageEffectiveSettings> ResolveEffectiveSettingsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ObjectStorageEffectiveSettings(new ObjectStorageSettings(), false, false));

    /// <summary>Testa conectividade usando um rascunho ainda não salvo (ou a config salva se nulo).</summary>
    Task<ObjectStorageTestResult> TestDraftConnectionAsync(ObjectStorageDraftInput draft, CancellationToken cancellationToken = default)
        => TestConnectionAsync(cancellationToken);
}
