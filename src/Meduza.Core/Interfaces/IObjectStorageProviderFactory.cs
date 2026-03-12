using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

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
    /// Criar instância de storage service para um provider específico.
    /// Útil para testes ou migrações entre provedores.
    /// </summary>
    /// <param name="providerType">Tipo de provider desejado</param>
    /// <returns>IObjectStorageService pronto para uso</returns>
    IObjectStorageService CreateObjectStorageService(ObjectStorageProviderType providerType);

    /// <summary>
    /// Validar se a configuração é válida para o provider ativo.
    /// </summary>
    /// <returns>Lista de erros (vazio se válida)</returns>
    Task<List<string>> ValidateConfigurationAsync();
}
