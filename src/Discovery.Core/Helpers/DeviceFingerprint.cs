using System.Security.Cryptography;
using System.Text;

namespace Discovery.Core.Helpers;

/// <summary>
/// Helper para cálculo do fingerprint de hardware usado na Recuperação de Dispositivos.
/// O fingerprint combina o hash da TPM Endorsement Key (EK) e o UUID SMBIOS em um
/// hash SHA-256 único, priorizando o TPM e usando o SMBIOS como fallback.
/// </summary>
public static class DeviceFingerprint
{
    /// <summary>
    /// Calcula o hash combinado do fingerprint. Prioriza TPM EK; usa SMBIOS UUID como fallback.
    /// Retorna null se nenhum dos dois estiver disponível.
    /// </summary>
    public static string? ComputeHash(string? tpmEkHash, string? smbiosUuid)
    {
        var tpm = Normalize(tpmEkHash);
        var uuid = Normalize(smbiosUuid);

        if (string.IsNullOrEmpty(tpm) && string.IsNullOrEmpty(uuid))
            return null;

        var combined = $"{tpm}|{uuid}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
