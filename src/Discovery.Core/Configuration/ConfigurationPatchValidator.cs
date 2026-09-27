using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace Discovery.Core.Configuration;

/// <summary>
/// Valida as chaves aceitas em um PATCH de configuração (Server/Client/Site).
/// Campos de auditoria, somente-leitura e não mapeados são rejeitados — antes,
/// chaves desconhecidas (nomes legados/typos) eram ignoradas silenciosamente e
/// a API respondia 200 como se o valor tivesse sido salvo.
/// </summary>
public static class ConfigurationPatchValidator
{
    private static readonly HashSet<string> ReservedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "Version", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy"
    };

    /// <summary>Retorna as chaves que não podem ser aplicadas no tipo informado.</summary>
    public static IReadOnlyList<string> FindUnknownFields(Type type, IEnumerable<string> keys)
    {
        var unknown = new List<string>();

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                unknown.Add(key);
                continue;
            }

            if (ReservedProperties.Contains(key))
            {
                unknown.Add(key);
                continue;
            }

            var property = type.GetProperty(
                key,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (property is null || !property.CanWrite)
            {
                unknown.Add(key);
                continue;
            }

            if (property.GetCustomAttribute<NotMappedAttribute>() is not null)
            {
                unknown.Add(key);
            }
        }

        return unknown;
    }

    /// <summary>Lança <see cref="ArgumentException"/> (400 na API) quando há chaves inválidas.</summary>
    public static void EnsureKnownFields(Type type, IEnumerable<string> keys, string level)
    {
        var unknown = FindUnknownFields(type, keys);
        if (unknown.Count == 0)
            return;

        throw new ArgumentException(
            $"Campos não reconhecidos em {level}: {string.Join(", ", unknown.Distinct())}. " +
            "Nenhuma alteração foi aplicada.");
    }
}
