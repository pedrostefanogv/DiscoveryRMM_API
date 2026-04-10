using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

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
    private readonly ISecretProtector _secretProtector;

    public ObjectStorageProviderFactory(
        IServerConfigurationRepository serverConfigRepository,
        ILogger<ObjectStorageProviderFactory> logger,
        ILoggerFactory loggerFactory,
        ISecretProtector secretProtector)
    {
        _serverConfigRepository = serverConfigRepository ?? throw new ArgumentNullException(nameof(serverConfigRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
    }

    /// <summary>
    /// Criar instância de storage service para o provider atualmente configurado.
    /// </summary>
    public IObjectStorageService CreateObjectStorageService()
    {
        var config = _serverConfigRepository.GetAsync().Result ??
            _serverConfigRepository.GetOrCreateDefaultAsync().Result;
        var settings = ObjectStorageSettingsFromServerConfiguration(config);
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            _logger.LogWarning(
                "Object storage não está configurado. Operações de storage serão indisponíveis. Erros: {Errors}",
                string.Join("; ", errors));
            return new NotConfiguredObjectStorageProvider(errors);
        }
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
    /// Testar conectividade com o storage usando as configurações atuais do servidor.
    /// </summary>
    public async Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var config = await _serverConfigRepository.GetOrCreateDefaultAsync();
        var settings = ObjectStorageSettingsFromServerConfiguration(config);
        var validationErrors = settings.Validate();

        if (validationErrors.Count > 0)
        {
            return new ObjectStorageTestResult(
                Success: false,
                ConfigurationValid: false,
                BucketReachable: false,
                Errors: validationErrors.ToArray(),
                LatencyMs: 0);
        }

        var childLogger = _loggerFactory.CreateLogger<MinioObjectStorageProvider>();
        var service = new MinioObjectStorageProvider(settings, childLogger);
        return await service.TestConnectionAsync(cancellationToken);
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
        Discovery.Core.Entities.ServerConfiguration serverConfig)
    {

        var decryptedSecret = _secretProtector.UnprotectOrSelf(serverConfig.ObjectStorageSecretKey);
        return new ObjectStorageSettings
        {
            BucketName = serverConfig.ObjectStorageBucketName,
            Endpoint = serverConfig.ObjectStorageEndpoint,
            Region = serverConfig.ObjectStorageRegion,
            AccessKey = serverConfig.ObjectStorageAccessKey,
            SecretKey = decryptedSecret,
            UrlTtlHours = serverConfig.ObjectStorageUrlTtlHours,
            UsePathStyle = serverConfig.ObjectStorageUsePathStyle,
            SslVerify = serverConfig.ObjectStorageSslVerify
        };
    }
}

/// <summary>
/// Provider retornado quando o object storage não está configurado.
/// Todas as operações lançam InvalidOperationException com mensagem clara.
/// TestConnectionAsync retorna resultado estruturado sem lançar.
/// </summary>
file sealed class NotConfiguredObjectStorageProvider : IObjectStorageService
{
    private readonly string _reason;

    public NotConfiguredObjectStorageProvider(List<string> errors)
    {
        _reason = string.Join("; ", errors);
    }

    private InvalidOperationException NotConfigured() =>
        new($"Object storage não está configurado e não pode ser usado. Configure o storage em Configurações > Servidor. Detalhes: {_reason}");

    public Task<StorageObject> UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
        => throw NotConfigured();

    public Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default)
        => throw NotConfigured();

    public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
        => throw NotConfigured();

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
        => throw NotConfigured();

    public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        => throw NotConfigured();

    public Task<string> GetPresignedDownloadUrlAsync(string objectKey, int ttlHours, CancellationToken cancellationToken = default)
        => throw NotConfigured();

    public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default)
        => throw NotConfigured();

    public Task<StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default)
        => throw NotConfigured();

    public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ObjectStorageTestResult(
            Success: false,
            ConfigurationValid: false,
            BucketReachable: false,
            Errors: [_reason],
            LatencyMs: 0));
}
