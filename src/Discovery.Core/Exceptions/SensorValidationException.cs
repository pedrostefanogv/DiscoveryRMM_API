namespace Discovery.Core.Exceptions;

/// <summary>
/// Erro de validação de sensor (entrada inválida). O controller converte em
/// HTTP 400 com a mensagem em PT-BR; não é falha de infraestrutura.
/// </summary>
public sealed class SensorValidationException(string message) : Exception(message);
