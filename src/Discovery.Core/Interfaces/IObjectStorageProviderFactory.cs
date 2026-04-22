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
}
