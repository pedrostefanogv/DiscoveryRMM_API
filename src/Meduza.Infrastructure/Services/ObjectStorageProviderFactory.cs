using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// Factory para criar IObjectStorageService baseado no provider configurado em ServerConfiguration.
/// Permite trocar provedor apenas alterando BD - sem mudança de código.
/// 
/// Todos os provedores S3-compatíveis são resolvidos via SDK MinIO.
/// </summary>
public class ObjectStorageProviderFactory : IObjectStorageProviderFactory
{
    private readonly IServerConfigurationRepository _serverConfigRepository;
    private readonly ILogger<ObjectStorageProviderFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public ObjectStorageProviderFactory(
        IServerConfigurationRepository serverConfigRepository,
        ILogger<ObjectStorageProviderFactory> logger,
        ILoggerFactory loggerFactory)
    {
        _serverConfigRepository = serverConfigRepository ?? throw new ArgumentNullException(nameof(serverConfigRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <summary>
    /// Criar instância de storage service para o provider atualmente configurado.
    /// </summary>
    public IObjectStorageService CreateObjectStorageService()
    {
        // TODO: Implementar cache com invalidação explícita quando ServerConfiguration muda
        var config = _serverConfigRepository.GetAsync().Result ?? 
            _serverConfigRepository.GetOrCreateDefaultAsync().Result;
        var settings = ObjectStorageSettingsFromServerConfiguration(config);
        _logger.LogInformation("Creating ObjectStorageService using MinIO S3-compatible provider");
        return CreateMinioStorageService(settings);
    }

    /// <summary>
    /// Criar instância de storage service para um provider específico.
    /// </summary>
    public IObjectStorageService CreateObjectStorageService(ObjectStorageProviderType providerType)
    {
        _logger.LogWarning(
            "Provider override '{ProviderType}' is ignored. MinIO S3-compatible provider is always used.",
            providerType);
        return CreateObjectStorageService();
    }

    /// <summary>
    /// Validar se a configuração é válida para o provider ativo.
    /// </summary>
    public async Task<List<string>> ValidateConfigurationAsync()
    {
        var config = await _serverConfigRepository.GetOrCreateDefaultAsync();
        var settings = ObjectStorageSettingsFromServerConfiguration(config);

        return settings.Validate();
    }

    /// <summary>
    /// Criar serviço S3-compatível via SDK MinIO, independente do provedor de nuvem.
    /// </summary>
    private IObjectStorageService CreateMinioStorageService(ObjectStorageSettings settings)
    {
        var childLogger = _loggerFactory.CreateLogger<MinioObjectStorageProvider>();
        return new MinioObjectStorageProvider(settings, childLogger);
    }

    /// <summary>
    /// Converter ServerConfiguration para ObjectStorageSettings.
    /// Descriptografa a chave secreta se necessário.
    /// </summary>
    private ObjectStorageSettings ObjectStorageSettingsFromServerConfiguration(
        Meduza.Core.Entities.ServerConfiguration serverConfig)
    {

        // SecretKey já vem descriptografado da camada de persistência
        // (descriptografia feita em DatabaseSeeder ou via IDataProtectionProvider)
        // Se implementar criptografia em repouso, descriptografar aqui:
        // var decryptedSecret = _dataProtectionProvider.Decrypt(serverConfig.ObjectStorageSecretKey);
        return new ObjectStorageSettings
        {
            BucketName = serverConfig.ObjectStorageBucketName,
            Endpoint = serverConfig.ObjectStorageEndpoint,
            Region = serverConfig.ObjectStorageRegion,
            AccessKey = serverConfig.ObjectStorageAccessKey,
            SecretKey = serverConfig.ObjectStorageSecretKey,
            UrlTtlHours = serverConfig.ObjectStorageUrlTtlHours,
            UsePathStyle = serverConfig.ObjectStorageUsePathStyle,
            SslVerify = serverConfig.ObjectStorageSslVerify
        };
    }
}
