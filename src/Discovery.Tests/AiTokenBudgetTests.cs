using Discovery.Core.DTOs;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Discovery.Infrastructure.Services.Ai;

namespace Discovery.Tests;

/// <summary>
/// Orçamento de tokens derivado da capacidade real do modelo. Cobre o teto que
/// antes era fixo em 8000 e revertia silenciosamente a configuração.
/// </summary>
public class AiTokenBudgetTests
{
    [Test]
    public void ResolveOutputCap_UsesTheLowestLimit()
    {
        // modelo suporta 65535, pedido 4000, departamento 1200 => 1200
        var (cap, source) = AiTokenLimits.ResolveOutputCap(4000, 65535, 1200, "gemini-2.5-flash");
        Assert.That(cap, Is.EqualTo(1200));
        Assert.That(source, Is.EqualTo("catalog"));

        // sem teto de departamento: prevalece o pedido
        (cap, _) = AiTokenLimits.ResolveOutputCap(4000, 65535, null, "gemini-2.5-flash");
        Assert.That(cap, Is.EqualTo(4000));

        // o teto do produto limita mesmo com modelo maior
        (cap, _) = AiTokenLimits.ResolveOutputCap(200000, 262144, null, "z-ai/glm-5.3");
        Assert.That(cap, Is.EqualTo(AiTokenLimits.MaxOutputTokensCeiling));
    }

    [Test]
    public void ResolveOutputCap_HonorsSixteenThousand_ForGpt4o()
    {
        // Regressão: 16000 era revertido para o default pelo clamp de 8000.
        var (cap, source) = AiTokenLimits.ResolveOutputCap(16000, 16384, null, "gpt-4o-mini");
        Assert.That(cap, Is.EqualTo(16000));
        Assert.That(source, Is.EqualTo("catalog"));
    }

    [Test]
    public void ResolveOutputCap_FallsBackPerFamily_WhenCatalogHasNoCap()
    {
        var (cap, source) = AiTokenLimits.ResolveOutputCap(16000, null, null, "z-ai/glm-5.3-flash");
        Assert.That(cap, Is.EqualTo(16000));
        Assert.That(source, Is.EqualTo("fallback"));

        (cap, source) = AiTokenLimits.ResolveOutputCap(16000, null, null, "deepseek/deepseek-v4.1-flash");
        Assert.That(cap, Is.EqualTo(16000));

        (cap, source) = AiTokenLimits.ResolveOutputCap(16000, null, null, "xiaomi/mimo-v2.6-flash");
        Assert.That(cap, Is.EqualTo(16000));
    }

    [Test]
    public void ResolveOutputCap_UnknownModel_UsesConservativeCap()
    {
        var (cap, source) = AiTokenLimits.ResolveOutputCap(16000, null, null, "modelo-inexistente");
        Assert.That(cap, Is.EqualTo(AiTokenLimits.UnknownModelOutputCap));
        Assert.That(source, Is.EqualTo("fallback"));

        (cap, source) = AiTokenLimits.ResolveOutputCap(16000, null, null, null);
        Assert.That(cap, Is.EqualTo(AiTokenLimits.UnknownModelOutputCap));
        Assert.That(source, Is.EqualTo("unknown"));
    }

    [Test]
    public void ResolveOutputCap_NeverGoesBelowMinimum()
    {
        var (cap, _) = AiTokenLimits.ResolveOutputCap(10, 100, 50, "x");
        Assert.That(cap, Is.EqualTo(AiTokenLimits.MinimumOutputTokens));
    }

    [Test]
    public void ResolveMaxPromptChars_SizesToTheContextWindow()
    {
        var large = AiTokenLimits.ResolveMaxPromptChars(1048576, 16000);
        var medium = AiTokenLimits.ResolveMaxPromptChars(32000, 16000);
        var unknown = AiTokenLimits.ResolveMaxPromptChars(null, 1200);
        var tiny = AiTokenLimits.ResolveMaxPromptChars(1000, 1200);

        Assert.That(large, Is.GreaterThanOrEqualTo(medium));
        Assert.That(medium, Is.GreaterThan(unknown));
        Assert.That(unknown, Is.EqualTo(AiTokenLimits.DefaultPromptCharsWhenUnknownContext));
        Assert.That(tiny, Is.GreaterThanOrEqualTo(AiTokenLimits.MinPromptChars));
        Assert.That(large, Is.LessThanOrEqualTo(AiTokenLimits.MaxPromptChars));
    }

    [Test]
    public void ChatTenantCeiling_HonorsLargeValues_WithoutTheOldEightThousandTrap()
    {
        // Regressão: o chat (síncrono e streaming) revertia qualquer valor acima de
        // 8000 para o default, ignorando a configuração do administrador.
        var settings = new AIIntegrationSettings { MaxTokensPerRequest = 16000 };
        Assert.That(AiChatSettingsResolver.ClampMaxTokens(settings), Is.EqualTo(16000));

        // Acima do teto do produto, cai no default em vez de aceitar o valor inválido.
        settings.MaxTokensPerRequest = 40000;
        var fallback = AiChatSettingsResolver.ClampMaxTokens(settings);
        Assert.That(fallback, Is.Not.EqualTo(40000));
        Assert.That(fallback, Is.LessThanOrEqualTo(AiTokenLimits.MaxOutputTokensCeiling));
    }

    [Test]
    public void ChatBudget_TenantValueIsLimitedByTheModelCapacity()
    {
        // Tenant pede 30000; o modelo suporta 16384 => 16384 (não estoura o provedor).
        var (cap, source) = AiTokenLimits.ResolveOutputCap(30000, 16384, null, "gpt-4o-mini");

        Assert.That(cap, Is.EqualTo(16384));
        Assert.That(source, Is.EqualTo("catalog"));
    }

    [Test]
    public void BuildBudget_ComposesTheResult()
    {
        var model = new AiModelInfo("z-ai/glm-5.3", "GLM 5.3", null, "openrouter", [], [], [], [],
            1048576, 131072, null, false, true, false, null, null);

        var budget = AiTokenLimits.BuildBudget(model, 2000, 1500, model.Id);

        Assert.That(budget.MaxOutputTokens, Is.EqualTo(1500));
        Assert.That(budget.MaxCompletionTokens, Is.EqualTo(131072));
        Assert.That(budget.ContextLength, Is.EqualTo(1048576));
        Assert.That(budget.MaxPromptChars, Is.GreaterThan(10000));
        Assert.That(budget.Model, Is.EqualTo("z-ai/glm-5.3"));
    }
}
