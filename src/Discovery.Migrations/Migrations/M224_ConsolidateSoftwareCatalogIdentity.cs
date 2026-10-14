using System.Data;
using System.Security.Cryptography;
using System.Text;
using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Consolida o catálogo de software (<c>software_catalog</c>) por identidade
/// canônica = NOME NORMALIZADO.
///
/// Antes, o fingerprint era SHA-256 de nome + publisher + install_id + serial +
/// source. Os três últimos são voláteis (variam por máquina, por hive 32/64
/// bits, por coletor registry × osquery × winget e pelo UninstallString gravado
/// pelo instalador), então o mesmo produto ganhava uma linha de catálogo por
/// instalação. No banco de homologação isso deixou 1.857 linhas de catálogo
/// para 802 nomes distintos (Foxit PDF Editor aparecia 3×; Foxit PDF Reader
/// 5×) e a lista do inventário mostrava o app duplicado com a mesma versão.
///
/// Esta migração:
///   1. escolhe UMA linha canônica por nome normalizado (preferindo a que tem
///      instalações presentes e a mais recente);
///   2. repointa <c>agent_software_inventory</c> para a canônica, fundindo
///      metadados quando o agente tinha as duas linhas;
///   3. remove as linhas de catálogo redundantes;
///   4. recalcula <c>fingerprint</c> com o algoritmo novo e troca o placeholder
///      "Sem fabricante" por NULL.
///
/// O algoritmo de normalização é duplicado de propósito: a migração precisa
/// continuar determinística mesmo se o código da aplicação mudar depois.
/// </summary>
[Migration(20261214_224)]
public class M224_ConsolidateSoftwareCatalogIdentity : Migration
{
    private const string MapTable = "_sw_software_dedup_map";

    /// <summary>
    /// Mapeia cada linha de inventário que pertence a um par duplicado
    /// (agente, catálogo de destino) para a linha SOBREVIVENTE desse par.
    /// </summary>
    private const string DupTargetTable = "_sw_inv_dup_target";

    private const string UnknownPublisher = "Sem fabricante";

    /// <summary>
    /// Coluna adicionada por M223; a migração precisa funcionar mesmo se ela
    /// ainda não existir (ordem de aplicação em ambientes antigos).
    /// </summary>
    private bool HasCustomPackageInventoryLink
        => Schema.Table("app_packages").Exists()
           && Schema.Table("app_packages").Column("inventory_software_id").Exists();

    public override void Up()
    {
        if (!Schema.Table("software_catalog").Exists()
            || !Schema.Table("agent_software_inventory").Exists())
        {
            return;
        }

        // Resolvido FORA do bloco WithConnection: a checagem de schema usa a
        // própria conexão do runner e não deve disputar a transação explícita.
        var hasCustomPackageInventoryLink = HasCustomPackageInventoryLink;

        Execute.WithConnection((connection, transaction) =>
        {
            var rows = ReadCatalogRows(connection, transaction);
            if (rows.Count == 0)
                return;

            var fingerprintById = rows.ToDictionary(row => row.Id, row => BuildFingerprint(row.Name));

            var canonicalByFingerprint = rows
                .GroupBy(row => fingerprintById[row.Id])
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(row => row.PresentCount)
                        .ThenByDescending(row => row.UpdatedAt)
                        .ThenBy(row => row.Id)
                        .First());

            var mappings = rows
                .Select(row => (Row: row, Canonical: canonicalByFingerprint[fingerprintById[row.Id]]))
                .Where(pair => pair.Canonical.Id != pair.Row.Id)
                .Select(pair => (OldId: pair.Row.Id, NewId: pair.Canonical.Id))
                .ToList();

            ExecuteNonQuery(connection, transaction,
                $"CREATE TEMP TABLE IF NOT EXISTS {MapTable} (old_id uuid PRIMARY KEY, new_id uuid NOT NULL)");
            ExecuteNonQuery(connection, transaction,
                $"CREATE TEMP TABLE IF NOT EXISTS {DupTargetTable} (id uuid PRIMARY KEY, survivor_id uuid NOT NULL)");

            try
            {
                InsertMappings(connection, transaction, mappings);

                if (mappings.Count > 0)
                {
                    // 1) escolhe UM sobrevivente por par (agente, catálogo de destino)
                    //    e registra a que sobrevivente cada linha pertence;
                    // 2) funde os metadados de TODAS as linhas do par no sobrevivente;
                    // 3) apaga as demais;
                    // 4) repointa o sobrevivente para o catálogo canônico.
                    ExecuteNonQuery(connection, transaction, PopulateDupTargetSql);
                    ExecuteNonQuery(connection, transaction, MergeDuplicateInventorySql);
                    ExecuteNonQuery(connection, transaction, DeleteNonSurvivorInventorySql);
                    ExecuteNonQuery(connection, transaction, RepointInventorySql);

                    // Pacotes custom guardam o softwareId escolhido no editor como
                    // texto, sem FK: sem repontar, o vínculo feito antes desta
                    // consolidação apontaria para um catálogo que deixou de existir.
                    if (hasCustomPackageInventoryLink)
                        ExecuteNonQuery(connection, transaction, RepointCustomPackageLinksSql);

                    ExecuteNonQuery(connection, transaction, DeleteDuplicateCatalogSql);
                }

                UpdateCanonicalCatalogs(connection, transaction, rows, canonicalByFingerprint, fingerprintById);
            }
            finally
            {
                ExecuteNonQuery(connection, transaction, $"DROP TABLE IF EXISTS {DupTargetTable}");
                ExecuteNonQuery(connection, transaction, $"DROP TABLE IF EXISTS {MapTable}");
            }
        });
    }

    public override void Down()
    {
        // Consolidação de dados não é reversível: as linhas de catálogo
        // removidas representavam o MESMO produto e não há backup dos
        // fingerprints antigos. Reverter recriaria as duplicatas.
    }

    // ── Leitura / seleção da linha canônica ─────────────────────────────

    private static List<CatalogRow> ReadCatalogRows(IDbConnection connection, IDbTransaction transaction)
    {
        var rows = new List<CatalogRow>();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.id,
                   c.name,
                   c.publisher,
                   c.updated_at,
                   COALESCE(p.present_count, 0) AS present_count
            FROM software_catalog c
            LEFT JOIN (
                SELECT software_id, count(*) AS present_count
                FROM agent_software_inventory
                WHERE is_present
                GROUP BY software_id
            ) p ON p.software_id = c.id
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CatalogRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetDateTime(3),
                Convert.ToInt64(reader.GetValue(4))));
        }

        return rows;
    }

    private static void UpdateCanonicalCatalogs(
        IDbConnection connection,
        IDbTransaction transaction,
        List<CatalogRow> rows,
        Dictionary<string, CatalogRow> canonicalByFingerprint,
        Dictionary<Guid, string> fingerprintById)
    {
        // Publisher "real" por grupo: ignora o placeholder legado.
        var bestPublisher = new Dictionary<string, string?>();
        foreach (var row in rows)
        {
            var fingerprint = fingerprintById[row.Id];
            if (!IsKnownPublisher(row.Publisher))
                continue;

            if (!bestPublisher.ContainsKey(fingerprint))
                bestPublisher[fingerprint] = row.Publisher!.Trim();
        }

        foreach (var (fingerprint, canonical) in canonicalByFingerprint)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE software_catalog
                SET fingerprint = @fingerprint,
                    publisher   = @publisher,
                    updated_at  = now()
                WHERE id = @id
                """;

            AddParameter(command, "@fingerprint", fingerprint);
            AddParameter(command, "@publisher",
                bestPublisher.TryGetValue(fingerprint, out var publisher) && publisher is not null
                    ? publisher
                    : DBNull.Value);
            AddParameter(command, "@id", canonical.Id);

            command.ExecuteNonQuery();
        }
    }

    // ── SQL de consolidação ─────────────────────────────────────────────

    /// <summary>
    /// Escolhe UM sobrevivente por par (agente, catálogo de destino) e registra,
    /// para cada linha do par, a qual sobrevivente ela pertence.
    ///
    /// Não basta tratar o caso "o agente também tem a linha canônica": há agentes
    /// com DUAS OU MAIS duplicatas e nenhuma linha canônica (149 pares e 414
    /// linhas no banco de homologação). Sem tratar isso, o repoint faria todas
    /// apontarem para o mesmo software_id e violaria
    /// <c>ix_agent_software_inventory_agent_software_unique</c>, abortando a
    /// migração e impedindo a API de subir.
    ///
    /// Critério de permanência: a linha canônica vence (duplicada = false), depois
    /// a que está presente e tem atualização pendente, depois a mais recente.
    /// </summary>
    private const string PopulateDupTargetSql = $"""
        INSERT INTO {DupTargetTable} (id, survivor_id)
        SELECT id, survivor_id
        FROM (
            SELECT i.id,
                   first_value(i.id) OVER (
                       PARTITION BY i.agent_id, COALESCE(m.new_id, i.software_id)
                       ORDER BY (m.old_id IS NOT NULL) ASC,
                                i.is_present DESC,
                                i.update_available DESC,
                                i.updated_at DESC,
                                i.id ASC
                   ) AS survivor_id,
                   count(*) OVER (
                       PARTITION BY i.agent_id, COALESCE(m.new_id, i.software_id)
                   ) AS cnt
            FROM agent_software_inventory i
            LEFT JOIN {MapTable} m ON m.old_id = i.software_id
        ) t
        WHERE t.cnt > 1
        """;

    /// <summary>
    /// Funde os metadados de TODAS as linhas do par no sobrevivente escolhido por
    /// <see cref="PopulateDupTargetSql"/>. Mirar o sobrevivente (e não
    /// <c>k.software_id = new_id</c>) é o que evita perder
    /// <c>available_version</c>/<c>update_available</c>/<c>update_package_id</c>/
    /// <c>install_date</c> quando o agente não tem a linha canônica.
    /// </summary>
    private const string MergeDuplicateInventorySql = $"""
        UPDATE agent_software_inventory AS k
        SET is_present        = k.is_present OR agg.any_present,
            version           = COALESCE(agg.present_version, k.version, agg.any_version),
            collected_at      = GREATEST(k.collected_at, agg.last_collected_at),
            last_seen_at      = GREATEST(k.last_seen_at, agg.last_seen_at),
            first_seen_at     = LEAST(k.first_seen_at, agg.first_seen_at),
            install_date      = COALESCE(k.install_date, agg.install_date),
            install_source    = COALESCE(k.install_source, agg.install_source),
            available_version = COALESCE(k.available_version, agg.available_version),
            update_available  = k.update_available OR agg.any_update,
            update_source     = COALESCE(k.update_source, agg.update_source),
            update_package_id = COALESCE(k.update_package_id, agg.update_package_id),
            updated_at        = GREATEST(k.updated_at, agg.updated_at)
        FROM (
            SELECT t.survivor_id,
                   bool_or(d.is_present)       AS any_present,
                   bool_or(d.update_available) AS any_update,
                   min(d.first_seen_at)        AS first_seen_at,
                   max(d.collected_at)         AS last_collected_at,
                   max(d.last_seen_at)         AS last_seen_at,
                   max(d.updated_at)           AS updated_at,
                   (array_agg(d.version ORDER BY d.is_present DESC, d.updated_at DESC))[1] AS any_version,
                   (array_agg(d.version ORDER BY d.updated_at DESC)
                       FILTER (WHERE d.is_present AND d.version IS NOT NULL))[1] AS present_version,
                   (array_agg(d.install_date ORDER BY d.updated_at DESC)
                       FILTER (WHERE d.install_date IS NOT NULL))[1] AS install_date,
                   (array_agg(d.install_source ORDER BY d.updated_at DESC)
                       FILTER (WHERE d.install_source IS NOT NULL))[1] AS install_source,
                   (array_agg(d.available_version ORDER BY d.updated_at DESC)
                       FILTER (WHERE d.available_version IS NOT NULL))[1] AS available_version,
                   (array_agg(d.update_source ORDER BY d.updated_at DESC)
                       FILTER (WHERE d.update_source IS NOT NULL))[1] AS update_source,
                   (array_agg(d.update_package_id ORDER BY d.updated_at DESC)
                       FILTER (WHERE d.update_package_id IS NOT NULL))[1] AS update_package_id
            FROM {DupTargetTable} t
            JOIN agent_software_inventory d ON d.id = t.id
            GROUP BY t.survivor_id
        ) AS agg
        WHERE k.id = agg.survivor_id
        """;

    /// <summary>
    /// Apaga as linhas que perderam o par para o sobrevivente. Depois disso cada
    /// (agente, catálogo de destino) tem exatamente uma linha, então o
    /// <see cref="RepointInventorySql"/> não pode violar o índice único.
    /// </summary>
    private const string DeleteNonSurvivorInventorySql = $"""
        DELETE FROM agent_software_inventory x
        USING {DupTargetTable} t
        WHERE x.id = t.id
          AND x.id <> t.survivor_id
        """;

    private const string RepointInventorySql = $"""
        UPDATE agent_software_inventory d
        SET software_id = m.new_id
        FROM {MapTable} m
        WHERE d.software_id = m.old_id
        """;

    private const string DeleteDuplicateCatalogSql = $"""
        DELETE FROM software_catalog c
        USING {MapTable} m
        WHERE c.id = m.old_id
        """;

    /// <summary>
    /// Compara os GUIDs ignorando tudo que não é hexadecimal (hífens, chaves,
    /// caixa): a coluna é texto livre (sem FK) e versões antigas do editor podem
    /// ter gravado o formato "N" (32 hex) ou "{...}". Comparar o texto canônico
    /// deixaria esses vínculos órfãos, apontando para um catálogo que a migração
    /// apaga.
    /// </summary>
    private const string RepointCustomPackageLinksSql = $"""
        UPDATE app_packages p
        SET inventory_software_id = m.new_id::text
        FROM {MapTable} m
        WHERE regexp_replace(lower(p.inventory_software_id), '[^0-9a-f]', '', 'g') =
              regexp_replace(lower(m.old_id::text), '[^0-9a-f]', '', 'g')
        """;

    // ── Helpers ─────────────────────────────────────────────────────────

    private static void InsertMappings(
        IDbConnection connection,
        IDbTransaction transaction,
        List<(Guid OldId, Guid NewId)> mappings)
    {
        if (mappings.Count == 0)
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {MapTable} (old_id, new_id) VALUES (@old_id, @new_id)";

        var oldId = command.CreateParameter();
        oldId.ParameterName = "@old_id";
        var newId = command.CreateParameter();
        newId.ParameterName = "@new_id";
        command.Parameters.Add(oldId);
        command.Parameters.Add(newId);

        foreach (var (oldValue, newValue) in mappings)
        {
            oldId.Value = oldValue;
            newId.Value = newValue;
            command.ExecuteNonQuery();
        }
    }

    private static void ExecuteNonQuery(IDbConnection connection, IDbTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool IsKnownPublisher(string? publisher)
        => !string.IsNullOrWhiteSpace(publisher)
           && !string.Equals(publisher.Trim(), UnknownPublisher, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Mesmo algoritmo de <c>Discovery.Core.Helpers.SoftwareIdentity</c>:
    /// trim, minúsculas e espaços internos colapsados; SHA-256 em hex maiúsculo.
    /// </summary>
    private static string BuildFingerprint(string name)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeName(name))));

    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var trimmed = name.Trim();
        var builder = new StringBuilder(trimmed.Length);
        var previousWasSpace = false;

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
            previousWasSpace = false;
        }

        return builder.ToString();
    }

    private sealed record CatalogRow(Guid Id, string Name, string? Publisher, DateTime UpdatedAt, long PresentCount);
}
