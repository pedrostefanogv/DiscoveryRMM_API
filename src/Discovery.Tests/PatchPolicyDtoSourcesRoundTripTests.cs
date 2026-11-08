using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Round-trip fiel de <see cref="PatchPolicyDto.Sources"/>.
///
/// <see cref="PatchSourcePolicy.EffectiveSources"/> é a regra de RESOLUÇÃO:
/// lista vazia/ausente significa "só os". Usá-la no echo do DTO apagava a
/// diferença entre "o usuário escolheu só os" e "nunca configurou", então a UI
/// não conseguia distinguir os dois estados e uma política explicitamente
/// os-only voltava como se terceiros nunca tivessem sido desmarcados.
///
/// O DTO deve ecoar o valor NORMALIZADO armazenado; a resolução continua
/// usando <see cref="PatchSourcePolicy.EffectiveSources"/>.
/// </summary>
[TestFixture]
public class PatchPolicyDtoSourcesRoundTripTests
{
    private static PatchPolicy PolicyWithSources(string? sourcesJson, bool allowThirdPartyApps = false) => new()
    {
        Id = Guid.NewGuid(),
        ScopeType = PatchApprovalScopeType.Global,
        ScopeId = null,
        EnforcementMode = PatchEnforcementMode.Monitor,
        DeferralDays = 3,
        DeadlineDays = 9,
        Sources = sourcesJson,
        AllowThirdPartyApps = allowThirdPartyApps,
        MaxInstallsPerRun = 50,
        UpdatedBy = "tester",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Test]
    public void ExplicitOsOnlyIsEchoedAsOsOnly()
    {
        var dto = PatchDtoMapper.ToDto(PolicyWithSources("[\"os\"]"));

        Assert.That(dto.Sources, Is.Not.Null, "sources explicitamente definido não pode virar null");
        Assert.That(dto.Sources, Is.EquivalentTo(new[] { "os" }));
        Assert.That(dto.AllowThirdPartyApps, Is.False);
    }

    [Test]
    public void ThirdPartySelectionIsEchoedFaithfully()
    {
        var dto = PatchDtoMapper.ToDto(
            PolicyWithSources("[\"os\",\"third_party\"]", allowThirdPartyApps: true));

        Assert.That(dto.Sources, Is.EquivalentTo(new[] { "os", "third_party" }));
        Assert.That(dto.AllowThirdPartyApps, Is.True);
    }

    [Test]
    public void SourcesEchoDoesNotInventValuesTheRowDoesNotHave()
    {
        // Antes do fix o DTO devolvia EffectiveSources(...) == ["os"] mesmo com a
        // linha vazia, inventando uma escolha que o usuário nunca fez.
        var dto = PatchDtoMapper.ToDto(PolicyWithSources(null));

        Assert.That(dto.Sources is null || dto.Sources.Count == 0, Is.True,
            "linha sem sources não pode ecoar [\"os\"] como se fosse escolha do usuário");
        Assert.That(PatchJson.SerializeStringArray(dto.Sources), Is.Null,
            "serialização de sources vazio deve ser null (coluna ausente)");
    }

    [Test]
    public void ResolutionStillFallsBackToOsOnlyForUnsetSources()
    {
        // O fallback continua valendo para RESOLUÇÃO (não para o echo do DTO).
        Assert.That(PatchSourcePolicy.EffectiveSources(null), Is.EquivalentTo(new[] { "os" }));
        Assert.That(PatchSourcePolicy.AllowsOs(null), Is.True);
        Assert.That(PatchSourcePolicy.ThirdPartyConsented(null, allowThirdPartyApps: true), Is.False);
    }

    [Test]
    public void DtoRoundTripsThroughSerializeAndParse()
    {
        // Simula o que o cliente faz: grava o DTO, lê de volta e compara.
        var saved = PatchDtoMapper.ToDto(PolicyWithSources("[\"os\"]"));
        var column = PatchJson.SerializeStringArray(saved.Sources);
        var reparsed = PatchDtoMapper.ToDto(PolicyWithSources(column));

        Assert.That(reparsed.Sources, Is.EquivalentTo(saved.Sources!));
        Assert.That(reparsed.DeferralDays, Is.EqualTo(saved.DeferralDays));
        Assert.That(reparsed.DeadlineDays, Is.EqualTo(saved.DeadlineDays));
        Assert.That(reparsed.AllowThirdPartyApps, Is.EqualTo(saved.AllowThirdPartyApps));
    }
}
