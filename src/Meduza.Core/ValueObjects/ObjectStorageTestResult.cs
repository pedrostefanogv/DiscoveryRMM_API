namespace Meduza.Core.ValueObjects;

/// <summary>
/// Resultado do teste de conectividade com o object storage.
/// </summary>
/// <param name="Success">True se a conexão e acesso ao bucket foram bem-sucedidos.</param>
/// <param name="ConfigurationValid">True se todos os campos obrigatórios estão preenchidos.</param>
/// <param name="BucketReachable">True se o bucket existe e é acessível com as credenciais fornecidas.</param>
/// <param name="Errors">Lista de erros de validação ou conectividade.</param>
/// <param name="LatencyMs">Latência da operação de teste em milissegundos (0 se a validação falhou antes de conectar).</param>
public record ObjectStorageTestResult(
    bool Success,
    bool ConfigurationValid,
    bool BucketReachable,
    string[] Errors,
    long LatencyMs);
