using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Resolve a política de auto-ticket com herança Server -> Client -> Site.
///
/// Semântica (idêntica à do ConfigurationResolver do produto): um valor
/// não-nulo no nível mais específico sobrescreve o nível acima; null herda.
/// Além disso respeita o LockedFieldsJson de cada nível — campo travado no topo
/// ignora tentativa de override abaixo (mecanismo JÁ existente no produto, hoje
/// sem nenhum campo travado por padrão).
///
/// Cache curto em memória por escopo, no mesmo espírito do cache Redis de 5 min
/// do ConfigurationResolver, mas local para não introduzir dependência de Redis
/// no caminho da ingestão.
/// </summary>
public class AutoTicketPolicyResolver(
    DiscoveryDbContext db,
    IOptions<AutoTicketOptions> bootstrapOptions,
    ILogger<AutoTicketPolicyResolver> logger) : IAutoTicketPolicyResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Teto de entradas do cache.
    ///
    /// O cache é estático (estado de processo) e chaveado por escopo. Sem teto,
    /// um MSP com muitos clientes/sites acumularia entradas que NUNCA são
    /// removidas — a expiração só era checada na leitura da MESMA chave, então
    /// escopos que não voltam ficavam na memória para sempre. Num processo de
    /// longa duração (a API), isso é vazamento lento.
    /// </summary>
    private const int MaxCacheEntries = 512;

    private readonly record struct CacheKey(Guid? ClientId, Guid? SiteId);
    private static readonly Dictionary<CacheKey, (ResolvedAutoTicketPolicy Policy, DateTime ExpiresAt)> Cache = new();
    private static readonly Lock CacheLock = new();

    public async Task<ResolvedAutoTicketPolicy> ResolveAsync(
        Guid? clientId = null,
        Guid? siteId = null,
        CancellationToken cancellationToken = default)
    {
        var key = new CacheKey(clientId, siteId);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var hit) && hit.ExpiresAt > DateTime.UtcNow)
                return hit.Policy;
        }

        var resolved = await ResolveUncachedAsync(clientId, siteId, cancellationToken);

        lock (CacheLock)
        {
            PruneCache();
            Cache[key] = (resolved, DateTime.UtcNow.Add(CacheTtl));
        }

        return resolved;
    }

    /// <summary>Invalida o cache (chamado ao salvar a política).</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock) { Cache.Clear(); }
    }

    /// <summary>
    /// Remove entradas expiradas e, se ainda estiver acima do teto, descarta as
    /// mais antigas. Chamado a cada escrita — o custo é O(n) sobre um dicionário
    /// pequeno (teto de 512), bem abaixo do custo da consulta ao banco que ele
    /// evita.
    ///
    /// Deve ser chamado com <see cref="CacheLock"/> já adquirido.
    /// </summary>
    private static void PruneCache()
    {
        var now = DateTime.UtcNow;

        // 1) Descarta o que já expirou.
        var expired = Cache.Where(entry => entry.Value.ExpiresAt <= now)
                           .Select(entry => entry.Key)
                           .ToList();
        foreach (var key in expired)
            Cache.Remove(key);

        // 2) Se ainda exceder o teto, remove as de expiração mais próxima
        //    (as mais antigas), até voltar ao limite.
        if (Cache.Count < MaxCacheEntries) return;

        var overflow = Cache.OrderBy(entry => entry.Value.ExpiresAt)
                            .Take(Cache.Count - MaxCacheEntries + 1)
                            .Select(entry => entry.Key)
                            .ToList();
        foreach (var key in overflow)
            Cache.Remove(key);
    }

    public async Task<string?> GetRawPolicyJsonAsync(
        string level,
        Guid? targetId = null,
        CancellationToken cancellationToken = default)
    {
        if (level.Equals(AutoTicketPolicyLevel.Server, StringComparison.OrdinalIgnoreCase))
        {
            return await db.ServerConfigurations.AsNoTracking()
                .OrderBy(s => s.Id)
                .Select(s => s.AutoTicketPolicyJson)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (targetId is null) return null;

        if (level.Equals(AutoTicketPolicyLevel.Client, StringComparison.OrdinalIgnoreCase))
        {
            return await db.ClientConfigurations.AsNoTracking()
                .Where(c => c.ClientId == targetId.Value)
                .Select(c => c.AutoTicketPolicyJson)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (level.Equals(AutoTicketPolicyLevel.Site, StringComparison.OrdinalIgnoreCase))
        {
            return await db.SiteConfigurations.AsNoTracking()
                .Where(s => s.SiteId == targetId.Value)
                .Select(s => s.AutoTicketPolicyJson)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return null;
    }

    private async Task<ResolvedAutoTicketPolicy> ResolveUncachedAsync(
        Guid? clientId,
        Guid? siteId,
        CancellationToken cancellationToken)
    {
        // ── Nível 1: global (Server). O appsettings entra como default de
        //    bootstrap para preservar o comportamento já shipado.
        var bootstrap = bootstrapOptions.Value;
        var serverJson = await GetRawPolicyJsonAsync(AutoTicketPolicyLevel.Server, null, cancellationToken);
        var server = AutoTicketPolicy.Parse(serverJson);

        var provenance = new Dictionary<string, string>();
        var lockedFields = await GetLockedFieldsAsync(clientId, siteId, cancellationToken);

        // Base de trabalho: começa do appsettings e aplica o blob do servidor.
        var working = new Working();

        void Apply(AutoTicketPolicy source, string level)
        {
            // A proveniência é exposta ao Site, que consulta por chave camelCase
            // ('enabled', 'minDurationSeconds'). O dicionário é serializado com
            // chaves LITERAIS (a naming policy não alcança Dictionary), então a
            // conversão é feita aqui — senão toda consulta do Site falharia e o
            // fallback '?? Default' exibiria "Default" em todas as linhas.
            void Record(string field, string source)
                => provenance[ToCamelCase(field)] = source;

            void Set<T>(string field, T? value) where T : struct
            {
                if (IsLocked(field, lockedFields) && level != AutoTicketPolicyLevel.Server) return;
                if (value is null) return;
                typeof(Working).GetProperty(field)!.SetValue(working, value);
                Record(field, lockedFields.Contains(field) ? AutoTicketPolicyLevel.Locked : level);
            }
            void SetRef(string field, object? value)
            {
                if (IsLocked(field, lockedFields) && level != AutoTicketPolicyLevel.Server) return;
                if (value is null) return;
                if (value is Array arr && arr.Length == 0) return;
                typeof(Working).GetProperty(field)!.SetValue(working, value);
                Record(field, lockedFields.Contains(field) ? AutoTicketPolicyLevel.Locked : level);
            }

            Set(nameof(AutoTicketPolicy.Enabled), source.Enabled);
            Set(nameof(AutoTicketPolicy.Mode), source.Mode);
            SetRef(nameof(AutoTicketPolicy.CanaryClientIds), source.CanaryClientIds);
            SetRef(nameof(AutoTicketPolicy.CanarySiteIds), source.CanarySiteIds);
            Set(nameof(AutoTicketPolicy.MinConsecutiveFailures), source.MinConsecutiveFailures);
            Set(nameof(AutoTicketPolicy.MinDurationSeconds), source.MinDurationSeconds);
            Set(nameof(AutoTicketPolicy.MinImpactToOpen), source.MinImpactToOpen);
            Set(nameof(AutoTicketPolicy.MinImpactToEscalate), source.MinImpactToEscalate);
            Set(nameof(AutoTicketPolicy.OpenOnWarning), source.OpenOnWarning);
            Set(nameof(AutoTicketPolicy.OpenOnPredictive), source.OpenOnPredictive);
            Set(nameof(AutoTicketPolicy.MaintenanceGraceMinutes), source.MaintenanceGraceMinutes);
            Set(nameof(AutoTicketPolicy.ReNotifyAfterMaintenanceEnds), source.ReNotifyAfterMaintenanceEnds);
            Set(nameof(AutoTicketPolicy.RespectAgentMaintenance), source.RespectAgentMaintenance);
            Set(nameof(AutoTicketPolicy.RespectMaintenanceWindow), source.RespectMaintenanceWindow);
            Set(nameof(AutoTicketPolicy.SuppressWhenHostNotReachable), source.SuppressWhenHostNotReachable);
            Set(nameof(AutoTicketPolicy.DependencySuppressionEnabled), source.DependencySuppressionEnabled);
            Set(nameof(AutoTicketPolicy.DependencyMaxDepth), source.DependencyMaxDepth);
            Set(nameof(AutoTicketPolicy.CloseTicketResolvedDuringMaintenance), source.CloseTicketResolvedDuringMaintenance);
            Set(nameof(AutoTicketPolicy.FlapWindowMinutes), source.FlapWindowMinutes);
            Set(nameof(AutoTicketPolicy.FlapTransitionsThreshold), source.FlapTransitionsThreshold);
            Set(nameof(AutoTicketPolicy.DedupWindowMinutes), source.DedupWindowMinutes);
            Set(nameof(AutoTicketPolicy.DedupWindowMaxHours), source.DedupWindowMaxHours);
            Set(nameof(AutoTicketPolicy.ReopenWindowMinutes), source.ReopenWindowMinutes);
            Set(nameof(AutoTicketPolicy.MaxCreatedTicketsPerHourPerAlertCode), source.MaxCreatedTicketsPerHourPerAlertCode);
            Set(nameof(AutoTicketPolicy.IncrementOccurrenceCounter), source.IncrementOccurrenceCounter);
            SetRef(nameof(AutoTicketPolicy.AllowedSensorKeys), source.AllowedSensorKeys);
            SetRef(nameof(AutoTicketPolicy.NeverTicketSensorKeys), source.NeverTicketSensorKeys);
            Set(nameof(AutoTicketPolicy.DefaultDepartmentId), source.DefaultDepartmentId);
            SetRef(nameof(AutoTicketPolicy.DefaultCategory), source.DefaultCategory);
            Set(nameof(AutoTicketPolicy.AiMode), source.AiMode);
            Set(nameof(AutoTicketPolicy.AiMinConfidence), source.AiMinConfidence);
            SetRef(nameof(AutoTicketPolicy.AiSuggestibleFields), source.AiSuggestibleFields);
            SetRef(nameof(AutoTicketPolicy.AiContentGateFields), source.AiContentGateFields);
            Set(nameof(AutoTicketPolicy.AiAllowEquipmentContext), source.AiAllowEquipmentContext);
            SetRef(nameof(AutoTicketPolicy.AiRedactFields), source.AiRedactFields);
            Set(nameof(AutoTicketPolicy.AiAnonymizeAssetNames), source.AiAnonymizeAssetNames);
        }

        // Bootstrap do appsettings (preserva o comportamento shipado).
        Apply(new AutoTicketPolicy
        {
            Enabled = bootstrap.Enabled,
            Mode = bootstrap.Enabled
                ? (bootstrap.ShadowMode ? AutoTicketScopeMode.Shadow : AutoTicketScopeMode.Active)
                : AutoTicketScopeMode.Off,
            ReopenWindowMinutes = bootstrap.ReopenWindowMinutes,
            MaxCreatedTicketsPerHourPerAlertCode = bootstrap.MaxCreatedTicketsPerHourPerAlertCode,
            CanaryClientIds = bootstrap.CanaryClientIds,
            CanarySiteIds = bootstrap.CanarySiteIds
        }, AutoTicketPolicyLevel.Default);

        // Nível global (blob do servidor) sobrescreve o bootstrap.
        Apply(server, AutoTicketPolicyLevel.Server);

        // ── Nível 2: cliente ───────────────────────────────────────────────
        if (clientId.HasValue)
        {
            var clientJson = await GetRawPolicyJsonAsync(AutoTicketPolicyLevel.Client, clientId, cancellationToken);
            Apply(AutoTicketPolicy.Parse(clientJson), AutoTicketPolicyLevel.Client);
        }

        // ── Nível 3: site ──────────────────────────────────────────────────
        if (siteId.HasValue)
        {
            var siteJson = await GetRawPolicyJsonAsync(AutoTicketPolicyLevel.Site, siteId, cancellationToken);
            Apply(AutoTicketPolicy.Parse(siteJson), AutoTicketPolicyLevel.Site);
        }

        // ── Curto-circuito: desligado em qualquer nível para ali ────────────
        string? disabledByLevel = null;
        if (working.Enabled == false)
        {
            disabledByLevel = provenance.TryGetValue(nameof(AutoTicketPolicy.Enabled), out var lvl)
                ? lvl
                : AutoTicketPolicyLevel.Server;

            // Registra que o motivo é o desligamento, e zera o resto para deixar
            // explícito que nada mais será avaliado.
            working.Mode = AutoTicketScopeMode.Off;

            logger.LogDebug(
                "AutoTicket desabilitado no nível {Level} para client {ClientId} / site {SiteId}.",
                disabledByLevel, clientId, siteId);
        }

        return new ResolvedAutoTicketPolicy
        {
            Enabled = working.Enabled ?? false,
            Mode = working.Mode ?? AutoTicketScopeMode.Off,
            DisabledByLevel = disabledByLevel,
            CanaryClientIds = working.CanaryClientIds ?? [],
            CanarySiteIds = working.CanarySiteIds ?? [],
            MinConsecutiveFailures = working.MinConsecutiveFailures ?? 3,
            MinDurationSeconds = working.MinDurationSeconds ?? 600,
            MinImpactToOpen = working.MinImpactToOpen ?? AutoTicketImpact.Medium,
            MinImpactToEscalate = working.MinImpactToEscalate ?? AutoTicketImpact.High,
            OpenOnWarning = working.OpenOnWarning ?? true,
            OpenOnPredictive = working.OpenOnPredictive ?? true,
            MaintenanceGraceMinutes = working.MaintenanceGraceMinutes ?? 30,
            ReNotifyAfterMaintenanceEnds = working.ReNotifyAfterMaintenanceEnds ?? true,
            RespectAgentMaintenance = working.RespectAgentMaintenance ?? true,
            RespectMaintenanceWindow = working.RespectMaintenanceWindow ?? true,
            SuppressWhenHostNotReachable = working.SuppressWhenHostNotReachable ?? true,
            DependencySuppressionEnabled = working.DependencySuppressionEnabled ?? false,
            DependencyMaxDepth = working.DependencyMaxDepth ?? 3,
            CloseTicketResolvedDuringMaintenance = working.CloseTicketResolvedDuringMaintenance ?? true,
            FlapWindowMinutes = working.FlapWindowMinutes ?? 60,
            FlapTransitionsThreshold = working.FlapTransitionsThreshold ?? 4,
            DedupWindowMinutes = working.DedupWindowMinutes ?? 60,
            DedupWindowMaxHours = working.DedupWindowMaxHours ?? 24,
            ReopenWindowMinutes = working.ReopenWindowMinutes ?? 0,
            MaxCreatedTicketsPerHourPerAlertCode = working.MaxCreatedTicketsPerHourPerAlertCode ?? 0,
            IncrementOccurrenceCounter = working.IncrementOccurrenceCounter ?? true,
            AllowedSensorKeys = working.AllowedSensorKeys ?? [],
            NeverTicketSensorKeys = working.NeverTicketSensorKeys ?? [],
            DefaultDepartmentId = working.DefaultDepartmentId,
            DefaultCategory = working.DefaultCategory,
            AiMode = working.AiMode ?? AutoTicketAiMode.Off,
            AiMinConfidence = working.AiMinConfidence ?? 0.60,
            AiSuggestibleFields = working.AiSuggestibleFields ?? [],
            AiContentGateFields = working.AiContentGateFields ?? [],
            AiAllowEquipmentContext = working.AiAllowEquipmentContext ?? true,
            AiRedactFields = working.AiRedactFields ?? ["password", "secret", "token", "apikey", "api_key", "credential"],
            AiAnonymizeAssetNames = working.AiAnonymizeAssetNames ?? false,
            Provenance = provenance
        };
    }

    /// <summary>Campos travados nos níveis superiores (união Server+Client).</summary>
    private async Task<HashSet<string>> GetLockedFieldsAsync(
        Guid? clientId, Guid? siteId, CancellationToken ct)
    {
        var locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var serverLocked = await db.ServerConfigurations.AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => s.LockedFieldsJson)
            .FirstOrDefaultAsync(ct);
        MergeLocked(locked, serverLocked);

        if (clientId.HasValue)
        {
            var clientLocked = await db.ClientConfigurations.AsNoTracking()
                .Where(c => c.ClientId == clientId.Value)
                .Select(c => c.LockedFieldsJson)
                .FirstOrDefaultAsync(ct);
            MergeLocked(locked, clientLocked);
        }

        if (siteId.HasValue)
        {
            var siteLocked = await db.SiteConfigurations.AsNoTracking()
                .Where(s => s.SiteId == siteId.Value)
                .Select(s => s.LockedFieldsJson)
                .FirstOrDefaultAsync(ct);
            MergeLocked(locked, siteLocked);
        }

        return locked;
    }

    private static void MergeLocked(HashSet<string> target, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var names = JsonSerializer.Deserialize<string[]>(json);
            if (names is null) return;
            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name)) target.Add(name.Trim());
            }
        }
        catch (JsonException)
        {
            // LockedFieldsJson corrompido: ignora a trava em vez de falhar.
        }
    }

    /// <summary>PascalCase -> camelCase, para as chaves expostas ao Site.</summary>
    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0])) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static bool IsLocked(string field, HashSet<string> locked)
        => locked.Count > 0 && locked.Contains(field);

    /// <summary>Acumulador mutável da resolução (todos os campos anuláveis).</summary>
    private sealed class Working
    {
        public bool? Enabled { get; set; }
        public AutoTicketScopeMode? Mode { get; set; }
        public string[]? CanaryClientIds { get; set; }
        public string[]? CanarySiteIds { get; set; }
        public int? MinConsecutiveFailures { get; set; }
        public int? MinDurationSeconds { get; set; }
        public AutoTicketImpact? MinImpactToOpen { get; set; }
        public AutoTicketImpact? MinImpactToEscalate { get; set; }
        public bool? OpenOnWarning { get; set; }
        public bool? OpenOnPredictive { get; set; }
        public int? MaintenanceGraceMinutes { get; set; }
        public bool? ReNotifyAfterMaintenanceEnds { get; set; }
        public bool? RespectAgentMaintenance { get; set; }
        public bool? RespectMaintenanceWindow { get; set; }
        public bool? SuppressWhenHostNotReachable { get; set; }
        public bool? DependencySuppressionEnabled { get; set; }
        public int? DependencyMaxDepth { get; set; }
        public bool? CloseTicketResolvedDuringMaintenance { get; set; }
        public int? FlapWindowMinutes { get; set; }
        public int? FlapTransitionsThreshold { get; set; }
        public int? DedupWindowMinutes { get; set; }
        public int? DedupWindowMaxHours { get; set; }
        public int? ReopenWindowMinutes { get; set; }
        public int? MaxCreatedTicketsPerHourPerAlertCode { get; set; }
        public bool? IncrementOccurrenceCounter { get; set; }
        public string[]? AllowedSensorKeys { get; set; }
        public string[]? NeverTicketSensorKeys { get; set; }
        public Guid? DefaultDepartmentId { get; set; }
        public string? DefaultCategory { get; set; }
        public AutoTicketAiMode? AiMode { get; set; }
        public double? AiMinConfidence { get; set; }
        public string[]? AiSuggestibleFields { get; set; }
        public string[]? AiContentGateFields { get; set; }
        public bool? AiAllowEquipmentContext { get; set; }
        public string[]? AiRedactFields { get; set; }
        public bool? AiAnonymizeAssetNames { get; set; }
    }
}
