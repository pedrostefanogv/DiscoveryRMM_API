using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Caching.Memory;
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
    private const string CacheKey = "object-storage:service";
    private readonly IServerConfigurationRepository _serverConfigRepository;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ObjectStorageProviderFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISecretProtector _secretProtector;

    public ObjectStorageProviderFactory(
        IServerConfigurationRepository serverConfigRepository,
        IMemoryCache cache,
        ILogger<ObjectStorageProviderFactory> logger,
        ILoggerFactory loggerFactory,
        ISecretProtector secretProtector)
    {
        _serverConfigRepository = serverConfigRepository ?? throw new ArgumentNullException(nameof(serverConfigRepository));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
    }

    /// <summary>
    /// Criar instância de storage service para o provider atualmente configurado.
    /// </summary>
    public IObjectStorageService CreateObjectStorageService()
        => new DeferredObjectStorageProvider(CreateObjectStorageServiceAsync());

    public async Task<IObjectStorageService> CreateObjectStorageServiceAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (_cache.TryGetValue<IObjectStorageService>(CacheKey, out var cachedService) && cachedService is not null)
            return cachedService;

        var config = await _serverConfigRepository.GetAsync() ??
            await _serverConfigRepository.GetOrCreateDefaultAsync();
        var settings = ObjectStorageSettingsFromServerConfiguration(config);
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            _logger.LogWarning(
                "Object storage não está configurado. Operações de storage serão indisponíveis. Erros: {Errors}",
                string.Join("; ", errors));
            var notConfigured = new NotConfiguredObjectStorageProvider(errors);
            _cache.Set(CacheKey, notConfigured, TimeSpan.FromSeconds(15));
            return notConfigured;
        }

        _logger.LogInformation("Creating ObjectStorageService using MinIO S3-compatible provider");
        var service = CreateMinioStorageService(settings);
        _cache.Set(CacheKey, service, TimeSpan.FromSeconds(30));
        return service;
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

file sealed class DeferredObjectStorageProvider : IObjectStorageService
{
    private readonly Task<IObjectStorageService> _innerTask;

    public DeferredObjectStorageProvider(Task<IObjectStorageService> innerTask)
    {
        _innerTask = innerTask;
    }

    private Task<IObjectStorageService> GetInnerAsync() => _innerTask;

    public async Task<StorageObject> UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
        => await (await GetInnerAsync()).UploadAsync(objectKey, content, contentType, cancellationToken);

    public async Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default)
        => await (await GetInnerAsync()).DownloadAsync(objectKey, cancellationToken);

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
        => await (await GetInnerAsync()).ExistsAsync(objectKey, cancellationToken);

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
        => await (await GetInnerAsync()).DeleteAsync(objectKey, cancellationToken);

    public async Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        => await (await GetInnerAsync()).DeleteByPrefixAsync(prefix, cancellationToken);

    public async Task<string> GetPresignedDownloadUrlAsync(string objectKey, int ttlHours, CancellationToken cancellationToken = default)
        => await (await GetInnerAsync()).GetPresignedDownloadUrlAsync(objectKey, ttlHours, cancellationToken);

    public async Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default)
        => await (await GetInnerAsync()).GetPresignedUploadUrlAsync(objectKey, ttlMinutes, contentType, cancellationToken);

    public async Task<StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default)
        => await (await GetInnerAsync()).GetMetadataAsync(objectKey, cancellationToken);

    public async Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
        => await (await GetInnerAsync()).TestConnectionAsync(cancellationToken);
}
