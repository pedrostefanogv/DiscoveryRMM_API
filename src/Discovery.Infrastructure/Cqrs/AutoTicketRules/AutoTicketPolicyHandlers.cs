using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutoTicketRules.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.AutoTicketRules;

/// <summary>
/// Leitura da política: devolve o blob CRU do nível pedido, a política EFETIVA
/// resolvida (com herança) e a PROVENIÊNCIA de cada campo.
/// </summary>
public sealed class GetAutoTicketPolicyQueryHandler(
    IAutoTicketPolicyResolver resolver,
    DiscoveryDbContext db) : IRequestHandler<GetAutoTicketPolicyQuery, Result<AutoTicketPolicyDto>>
{
    public async Task<Result<AutoTicketPolicyDto>> Handle(GetAutoTicketPolicyQuery q, CancellationToken ct)
    {
        var level = q.SiteId.HasValue
            ? AutoTicketPolicyLevel.Site
            : q.ClientId.HasValue ? AutoTicketPolicyLevel.Client : AutoTicketPolicyLevel.Server;

        var targetId = q.SiteId ?? q.ClientId;

        var rawJson = await resolver.GetRawPolicyJsonAsync(level, targetId, ct);
        var resolved = await resolver.ResolveAsync(q.ClientId, q.SiteId, ct);

        return Result<AutoTicketPolicyDto>.Success(new AutoTicketPolicyDto
        {
            Level = level,
            TargetId = targetId,
            Own = string.IsNullOrWhiteSpace(rawJson) ? null : AutoTicketPolicy.Parse(rawJson),
            Effective = ResolvedAutoTicketPolicyView.From(resolved),
            Provenance = resolved.Provenance
        });
    }
}

/// <summary>
/// Grava a política de UM nível. Serializa apenas os campos não-nulos, de modo
/// que o blob registre exatamente o que aquele nível sobrescreve.
/// </summary>
public sealed class SaveAutoTicketPolicyCommandHandler(
    DiscoveryDbContext db,
    IAutoTicketPolicyResolver resolver) : IRequestHandler<SaveAutoTicketPolicyCommand, Result<AutoTicketPolicyDto>>
{
    public async Task<Result<AutoTicketPolicyDto>> Handle(SaveAutoTicketPolicyCommand cmd, CancellationToken ct)
    {
        var json = cmd.Policy.Serialize();

        if (cmd.Level.Equals(AutoTicketPolicyLevel.Server, StringComparison.OrdinalIgnoreCase))
        {
            var server = await db.ServerConfigurations.OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
            if (server is null) return Result<AutoTicketPolicyDto>.Failure(Error.NotFound("Server configuration not found."));

            server.AutoTicketPolicyJson = json;
            await db.SaveChangesAsync(ct);
            Services.AutoTicketPolicyResolver.InvalidateCache();
        }
        else if (cmd.Level.Equals(AutoTicketPolicyLevel.Client, StringComparison.OrdinalIgnoreCase))
        {
            if (cmd.TargetId is null) return Result<AutoTicketPolicyDto>.Failure(Error.Validation("targetId", "targetId is required for Client level."));

            var client = await db.ClientConfigurations.FirstOrDefaultAsync(c => c.ClientId == cmd.TargetId.Value, ct);
            if (client is null) return Result<AutoTicketPolicyDto>.Failure(Error.NotFound("Client configuration not found."));

            client.AutoTicketPolicyJson = json;
            await db.SaveChangesAsync(ct);
            Services.AutoTicketPolicyResolver.InvalidateCache();
        }
        else if (cmd.Level.Equals(AutoTicketPolicyLevel.Site, StringComparison.OrdinalIgnoreCase))
        {
            if (cmd.TargetId is null) return Result<AutoTicketPolicyDto>.Failure(Error.Validation("targetId", "targetId is required for Site level."));

            var site = await db.SiteConfigurations.FirstOrDefaultAsync(s => s.SiteId == cmd.TargetId.Value, ct);
            if (site is null) return Result<AutoTicketPolicyDto>.Failure(Error.NotFound("Site configuration not found."));

            site.AutoTicketPolicyJson = json;
            await db.SaveChangesAsync(ct);
            Services.AutoTicketPolicyResolver.InvalidateCache();
        }
        else
        {
            return Result<AutoTicketPolicyDto>.Failure(Error.Validation("level", $"Unknown level '{cmd.Level}'."));
        }

        var resolved = await resolver.ResolveAsync(
            cmd.Level.Equals(AutoTicketPolicyLevel.Client, StringComparison.OrdinalIgnoreCase) ? cmd.TargetId : null,
            cmd.Level.Equals(AutoTicketPolicyLevel.Site, StringComparison.OrdinalIgnoreCase) ? cmd.TargetId : null,
            ct);

        return Result<AutoTicketPolicyDto>.Success(new AutoTicketPolicyDto
        {
            Level = cmd.Level,
            TargetId = cmd.TargetId,
            Own = cmd.Policy,
            Effective = ResolvedAutoTicketPolicyView.From(resolved),
            Provenance = resolved.Provenance
        });
    }

}
