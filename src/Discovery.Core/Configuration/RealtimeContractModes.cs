namespace Discovery.Core.Configuration;

/// <summary>
/// Valores canônicos de <c>RealtimeContractMode</c> (chave de appsettings
/// <c>RealtimeContract:Mode</c>).
///
/// O campo virou configuração de UI (CFG-A): antes só existia no appsettings e
/// um valor digitado errado passaria direto para o normalizador de eventos. A
/// normalização/validação central aqui evita que o resolver e o consumidor
/// discordem sobre o que é um modo válido.
/// </summary>
public static class RealtimeContractModes
{
    /// <summary>Default de hoje: escolhe o contrato pela data de hardening.</summary>
    public const string Auto = "auto";

    /// <summary>Contrato estrito: rejeita emissores legados.</summary>
    public const string Strict = "strict";

    /// <summary>Contrato legado: mantém o formato antigo.</summary>
    public const string Legacy = "legacy";

    public static readonly string[] All = [Auto, Strict, Legacy];

    /// <summary>
    /// Normaliza o valor vindo da configuração. Vazio/desconhecido cai para
    /// <see cref="Auto"/> — nunca lança, para uma configuração ruim não derrubar
    /// o dashboard em tempo real.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Auto;

        var trimmed = value.Trim();
        foreach (var mode in All)
        {
            if (string.Equals(mode, trimmed, StringComparison.OrdinalIgnoreCase))
                return mode;
        }

        return Auto;
    }

    /// <summary>true quando o valor é um dos modos aceitos (case-insensitive).</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return All.Any(mode => string.Equals(mode, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
