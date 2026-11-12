using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Cqrs.Departments;
using Discovery.Infrastructure.Services;
using Discovery.Infrastructure.Services.Ai;

namespace Discovery.Tests;

/// <summary>
/// Orçamento de tokens AUTOMÁTICO por modelo (quinta onda).
///
/// Contexto: o card A2UI truncava porque o pedido era 2000 e o teto do produto,
/// 32768. Três decisões de produto estão sob teste aqui:
///   • DECISÃO 1 — <c>MaxTokensPerRequest = 0</c> significa AUTOMÁTICO ("use a
///     capacidade real do modelo selecionado"), NÃO um default silencioso;
///   • DECISÃO 2 — o teto do produto subiu de 32.768 para 450.000 (o p90 medido
///     no catálogo real do OpenRouter com 475 modelos);
///   • DECISÃO 3 — o teto de prompt deixou de ser 60.000 fixo e passou a ser
///     DERIVADO do contexto do modelo.
///
/// FIXTURE MEDIDA (fonte de verdade, não estimativa): o Lead mediu na API real do
/// OpenRouter — deepseek/deepseek-v4.1-flash-1017 (modelo em PRODUÇÃO):
/// context_length = 1.048.576 | max_completion_tokens = 393.216.
/// Mediana de max_completion = 128.000 | p90 = 450.000 | máximo = 1.800.000.
/// </summary>
[TestFixture]
public class AiTokenBudgetAutomaticTests
{
    // ── Fixture medida ──────────────────────────────────────────────────────

    private const string ProductionModelId = "deepseek/deepseek-v4.1-flash-1017";
    private const int ProductionContextLength = 1_048_576;
    private const int ProductionMaxCompletion = 393_216;

    /// <summary>Modelo de produção, exatamente como o catálogo real o devolve.</summary>
    private static AiModelInfo ProductionModel() => new(
        ProductionModelId, "DeepSeek V4.1 Flash", null, "openrouter",
        ["tools"], [], [], ["tools"],
        ProductionContextLength, ProductionMaxCompletion,
        null, false, true, false, null, null);

    private static AiModelInfo ModelWith(int? context, int? maxCompletion, string id = "algum/modelo") => new(
        id, id, null, "openrouter", [], [], [], [],
        context, maxCompletion, null, false, true, false, null, null);

    // ── ITEM 1: "ZERO = AUTOMÁTICO" ─────────────────────────────────────────

    [Test]
    public void ResolveOutputCap_ZeroMeansAutomatic_AndUsesTheModelCapacity()
    {
        // O ponto central da DECISÃO 1: pedido 0 NÃO é um pedido de 0 tokens.
        // Quem decide é a capacidade real do modelo (393.216), que está ABAIXO do
        // teto do produto (450.000) — então o modelo manda.
        var (cap, source) = AiTokenLimits.ResolveOutputCap(
            requested: 0, modelCap: ProductionMaxCompletion, departmentCap: null, modelId: ProductionModelId);

        Assert.Multiple(() =>
        {
            Assert.That(cap, Is.EqualTo(ProductionMaxCompletion),
                "0 = AUTOMÁTICO: o cap precisa ser a capacidade real do modelo (393.216)");
            Assert.That(cap, Is.Not.EqualTo(AiChatConstants.DefaultMaxTokens),
                "REGRESSÃO CRÍTICA: 'automático' não pode regredir para o default 2048");
            Assert.That(source, Is.EqualTo("catalog"));
        });
    }

    [Test]
    public void BuildBudget_AutomaticMode_EndToEndWithTheProductionModel()
    {
        // End-to-end do caminho que o card A2UI percorre: settings com 0 (o novo
        // default) + modelo de produção => orçamento na capacidade do modelo.
        var budget = AiTokenLimits.BuildBudget(ProductionModel(), requested: 0, departmentCap: null, modelId: ProductionModelId);

        Assert.Multiple(() =>
        {
            Assert.That(budget.MaxOutputTokens, Is.EqualTo(ProductionMaxCompletion));
            Assert.That(budget.MaxOutputTokens, Is.GreaterThan(32_768),
                "o teto antigo (32768) era a causa do truncamento — o automático precisa passar dele");
            Assert.That(budget.ContextLength, Is.EqualTo(ProductionContextLength));
            Assert.That(budget.MaxCompletionTokens, Is.EqualTo(ProductionMaxCompletion));
            Assert.That(budget.Source, Is.EqualTo("catalog"));
        });
    }

    [Test]
    public void ResolveOutputCap_ExplicitRequest_StillWinsOverAutomatic()
    {
        // O operador manda: um pedido explícito de 8000 vence a capacidade do
        // modelo (393.216). Automático só vale quando NÃO há pedido.
        var (cap, _) = AiTokenLimits.ResolveOutputCap(8000, ProductionMaxCompletion, null, ProductionModelId);

        Assert.That(cap, Is.EqualTo(8000),
            "pedido explícito de 8000 precisa ser respeitado, não promovido a 393.216");
    }

    [Test]
    public void ClampMaxTokens_ZeroIsAutomatic_NotTheOldDefault()
    {
        // Os DOIS pontos que devolviam default silencioso: AiChatHelpers (chat) e
        // AiChatSettingsResolver (triagem). Ambos precisam propagar 0.
        var zero = new AIIntegrationSettings { MaxTokensPerRequest = 0 };

        Assert.Multiple(() =>
        {
            Assert.That(AiChatHelpers.ClampMaxTokens(zero), Is.EqualTo(0),
                "0 precisa chegar como 0 até ResolveOutputCap (o 2048 era o bug)");
            Assert.That(AiChatSettingsResolver.ClampMaxTokens(zero), Is.EqualTo(0));
            Assert.That(AiChatHelpers.ClampMaxTokens(zero), Is.Not.EqualTo(AiChatConstants.DefaultMaxTokens));
        });
    }

    [Test]
    public void ClampMaxTokens_TreatsInvalidValuesAsAutomatic()
    {
        // Valores abaixo do mínimo explícito não são um "pedido" válido: precisam
        // virar AUTOMÁTICO, não um 2048 silencioso nem um 200 do piso.
        Assert.Multiple(() =>
        {
            foreach (var invalido in new[] { 0, 1, 50, 99, -1, -500 })
            {
                Assert.That(AiChatHelpers.ClampMaxTokens(new AIIntegrationSettings { MaxTokensPerRequest = invalido }),
                    Is.EqualTo(0), $"MaxTokensPerRequest={invalido} é inválido como pedido => AUTOMÁTICO (0)");
            }

            // O limite exato da faixa: 100 é o primeiro pedido EXPLÍCITO válido.
            Assert.That(AiChatHelpers.ClampMaxTokens(new AIIntegrationSettings { MaxTokensPerRequest = 100 }),
                Is.EqualTo(100), "100 é o menor pedido explícito válido");
            Assert.That(AiChatHelpers.ClampMaxTokens(new AIIntegrationSettings { MaxTokensPerRequest = 99 }),
                Is.EqualTo(0), "99 ainda é automático");
        });
    }

    [Test]
    public void AutomaticMode_NeverCollapsesToTheMinimumOrToZero()
    {
        // O piso de 200 (MinimumOutputTokens) é do RESULTADO, nunca do pedido.
        // Com 0 = automático, o resultado precisa ser a capacidade do modelo — não
        // 200 (se o 0 fosse tratado como pedido literal) e não 0.
        var budget = AiTokenLimits.BuildBudget(ProductionModel(), 0, null, ProductionModelId);

        Assert.Multiple(() =>
        {
            Assert.That(budget.MaxOutputTokens, Is.Not.EqualTo(AiTokenLimits.MinimumOutputTokens),
                "0 virando o piso de 200 é exatamente o bug que a DECISÃO 1 elimina");
            Assert.That(budget.MaxOutputTokens, Is.Not.EqualTo(0), "nunca 0 tokens");
            Assert.That(budget.MaxOutputTokens, Is.GreaterThanOrEqualTo(AiTokenLimits.MinimumOutputTokens));
        });
    }

    [Test]
    public void ResolveOutputCap_StillAppliesTheMinimumAsTheFloorOfTheResult()
    {
        // Contraprova da regra acima: quando TODOS os limites são pequenos, o
        // resultado ainda respeita o piso de 200 (ele é do resultado).
        var (cap, _) = AiTokenLimits.ResolveOutputCap(10, 100, 50, "x");

        Assert.That(cap, Is.EqualTo(AiTokenLimits.MinimumOutputTokens),
            "o piso de 200 continua valendo para limites pequenos");
    }

    // ── ITEM 2: TETO DO PRODUTO 450.000 ─────────────────────────────────────

    [Test]
    public void ProductCeiling_IsTheMeasuredP90_NotTheOld32768()
    {
        // Asserção por NÚMERO de propósito: com a constante, o teste continuaria
        // verde se alguém revertesse o teto para 32.768 e a medicação fosse
        // perdida. O 450.000 é o p90 medido no catálogo real (475 modelos).
        Assert.Multiple(() =>
        {
            Assert.That(AiTokenLimits.MaxOutputTokensCeiling, Is.EqualTo(450_000),
                "teto do produto = p90 medido no catálogo OpenRouter");
            Assert.That(AiTokenLimits.MaxOutputTokensCeiling, Is.Not.EqualTo(32_768),
                "o teto antigo truncava o modelo de produção em ~12x");
            Assert.That(AiChatConstants.MaxOutputTokensCeiling, Is.EqualTo(450_000),
                "a constante é a fonte única — AiTokenLimits é apenas o alias");
        });
    }

    [Test]
    public void ResolveOutputCap_MillionRequest_IsLimitedByTheModelCapacity()
    {
        // Pedido de 1.000.000 com modelo de 393.216 => o MODELO manda (393.216),
        // porque é o menor entre pedido, modelo e teto do produto.
        var (cap, source) = AiTokenLimits.ResolveOutputCap(1_000_000, ProductionMaxCompletion, null, ProductionModelId);

        Assert.Multiple(() =>
        {
            Assert.That(cap, Is.EqualTo(ProductionMaxCompletion), "o modelo (393.216) é o menor limite");
            Assert.That(cap, Is.LessThan(1_000_000), "nunca entrega mais do que o pedido");
            Assert.That(source, Is.EqualTo("catalog"));
        });
    }

    [Test]
    public void ResolveOutputCap_RequestAboveTheCeiling_IsClampedByTheProduct()
    {
        // Modelo GRANDE (1.800.000, o máximo medido) + pedido acima do teto =>
        // quem manda é o teto do produto. Asserção por número: se alguém reverter
        // o teto para 32.768, este teste falha.
        var (cap, _) = AiTokenLimits.ResolveOutputCap(1_000_000, 1_800_000, null, "modelo-gigante");

        Assert.That(cap, Is.EqualTo(450_000),
            "pedido e modelo acima do teto => o teto do produto (450.000) limita");
    }

    [Test]
    public void ResolveOutputCap_MillionRequest_WithoutModel_IsClampedByTheProduct()
    {
        // Sem modelo informado: o fallback é conservador, então o teto do produto
        // não é o limite efetivo aqui — o que importa é NUNCA passar do teto.
        var (cap, source) = AiTokenLimits.ResolveOutputCap(1_000_000, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(cap, Is.LessThanOrEqualTo(AiTokenLimits.MaxOutputTokensCeiling));
            Assert.That(cap, Is.EqualTo(AiTokenLimits.UnknownModelOutputCap),
                "sem modelo, vale o cap conservador de modelo desconhecido");
            Assert.That(source, Is.EqualTo("unknown"));
        });
    }

    // ── ITEM 3: MODELO DESCONHECIDO CONTINUA CONSERVADOR ────────────────────

    [Test]
    public void UnknownModel_StaysConservative_AndDoesNotJumpToTheProductCeiling()
    {
        // A subida do teto do produto NÃO pode fazer um modelo desconhecido
        // receber 450.000: sem informação de capacidade, o conservador é 4096.
        var (cap, source) = AiTokenLimits.ResolveOutputCap(1_000_000, null, null, "modelo-que-nao-existe");

        Assert.Multiple(() =>
        {
            Assert.That(cap, Is.EqualTo(AiTokenLimits.UnknownModelOutputCap));
            Assert.That(cap, Is.Not.EqualTo(AiTokenLimits.MaxOutputTokensCeiling),
                "modelo desconhecido não pode herdar o teto do produto");
            Assert.That(source, Is.EqualTo("fallback"));
        });
    }

    [Test]
    public void UnknownModelId_WithoutAnyId_UsesTheConservativeCap()
    {
        var (cap, source) = AiTokenLimits.ResolveOutputCap(1_000_000, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(cap, Is.EqualTo(AiTokenLimits.UnknownModelOutputCap));
            Assert.That(source, Is.EqualTo("unknown"));
        });
    }

    [Test]
    public void FallbackForProductionModel_MatchesTheMeasuredCapacity()
    {
        // O fallback por família foi ALINHADO à medição: sem o catálogo, o modelo
        // de produção recebe 393.216 (antes 32.768, que subestimava ~12x). Uma
        // indisponibilidade do catálogo não pode voltar a truncar o modelo em uso.
        Assert.Multiple(() =>
        {
            Assert.That(AiTokenLimits.ResolveFallbackOutputCap(ProductionModelId),
                Is.EqualTo(ProductionMaxCompletion),
                "deepseek sem catálogo => 393.216 medido, não o antigo 32768");
            // Modelo genuinamente desconhecido continua conservador.
            Assert.That(AiTokenLimits.ResolveFallbackOutputCap("familia-inexistente"),
                Is.EqualTo(AiTokenLimits.UnknownModelOutputCap));
        });
    }

    // ── ITEM 4: TETO DE PROMPT DERIVADO DO CONTEXTO ─────────────────────────

    [Test]
    public void MaxPromptChars_GrowsWithTheContext_AndBeatsTheOldSixtyThousand()
    {
        // DECISÃO 3: o teto era 60.000 FIXO e cortava o prompt de qualquer modelo
        // grande. Agora é derivado do contexto — num modelo de 1.048.576 o valor
        // efetivo passa de 2.7M de caracteres.
        var big = AiTokenLimits.ResolveMaxPromptChars(ProductionContextLength, ProductionMaxCompletion);

        Assert.Multiple(() =>
        {
            Assert.That(big, Is.GreaterThan(60_000),
                "o modelo de produção precisa passar do antigo teto fixo de 60.000 caracteres");
            Assert.That(big, Is.LessThanOrEqualTo(AiTokenLimits.MaxPromptChars),
                "ainda respeita a trava de sanidade absoluta");
        });
    }

    [Test]
    public void MaxPromptChars_IsMonotonicInTheContextWindow()
    {
        // Mais contexto => prompt maior ou igual (nunca menor).
        var huge = AiTokenLimits.ResolveMaxPromptChars(ProductionContextLength, 16_000);
        var medium = AiTokenLimits.ResolveMaxPromptChars(32_000, 16_000);
        var small = AiTokenLimits.ResolveMaxPromptChars(8_192, 16_000);

        Assert.Multiple(() =>
        {
            Assert.That(huge, Is.GreaterThanOrEqualTo(medium));
            Assert.That(medium, Is.GreaterThanOrEqualTo(small));
        });
    }

    [Test]
    public void MaxPromptChars_SmallContextWithHugeOutput_FallsToTheFloorWithoutGoingNegative()
    {
        // O bug mais provável desta conta: contexto pequeno + cap alto deixa
        // availableTokens NEGATIVO. O resultado precisa cair no piso, nunca
        // negativo/zero.
        var value = AiTokenLimits.ResolveMaxPromptChars(contextLength: 8_192, outputTokens: 450_000);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.EqualTo(AiTokenLimits.MinPromptChars),
                "saída maior que o contexto => prompt no mínimo, para sobrar contexto à resposta");
            Assert.That(value, Is.GreaterThan(0), "nunca <= 0 (o bug mais provável da conta)");
        });
    }

    [Test]
    public void MaxPromptChars_UnknownContext_UsesTheConservativeDefault()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AiTokenLimits.ResolveMaxPromptChars(null, 16_000),
                Is.EqualTo(AiTokenLimits.DefaultPromptCharsWhenUnknownContext));
            Assert.That(AiTokenLimits.ResolveMaxPromptChars(0, 16_000),
                Is.EqualTo(AiTokenLimits.DefaultPromptCharsWhenUnknownContext), "0 = sem informação");
            Assert.That(AiTokenLimits.ResolveMaxPromptChars(-5, 16_000),
                Is.EqualTo(AiTokenLimits.DefaultPromptCharsWhenUnknownContext), "negativo = sem informação");
        });
    }

    [Test]
    public void MaxPromptChars_IsNeverZeroOrNegative_AcrossTheWholeInputSpace()
    {
        // Invariante dura: nenhuma combinação pode devolver <= 0. Uma varredura
        // (não um caso escolhido a dedo) porque a conta envolve subtração.
        int?[] contexts = [null, -1, 0, 1, 100, 1_000, 8_192, 32_000, 131_072, ProductionContextLength];
        int[] outputs = [0, 1, 200, 1_200, 16_000, 393_216, 450_000, 1_000_000, int.MaxValue];

        foreach (var context in contexts)
        {
            foreach (var output in outputs)
            {
                var value = AiTokenLimits.ResolveMaxPromptChars(context, output);
                Assert.That(value, Is.GreaterThan(0),
                    $"contexto={context?.ToString() ?? "null"} saída={output} devolveu {value}");
            }
        }
    }

    // ── ITEM 5: VALIDAÇÃO DA TRIAGEM USA A CONSTANTE ────────────────────────

    [Test]
    public void TriageValidation_AcceptsUpToTheNewCeiling()
    {
        // 32000 (o valor do card truncado) e 450.000 (o teto novo) precisam ser
        // aceitos; o teto antigo (32.768) não é mais a fronteira.
        Assert.Multiple(() =>
        {
            Assert.That(Validate(AiTokenLimits.MaxOutputTokensCeiling), Is.Null, "o teto novo é aceito");
            Assert.That(Validate(450_000), Is.Null, "o teto novo é aceito (asserção por número)");
            Assert.That(Validate(32_000), Is.Null, "o valor que truncava o card é aceito");
            Assert.That(Validate(AiTokenLimits.MinimumOutputTokens), Is.Null, "o piso é aceito");
            Assert.That(Validate(450_001), Is.Not.Null, "acima do teto é rejeitado");
            Assert.That(Validate(199), Is.Not.Null, "abaixo do piso é rejeitado");
        });
    }

    [Test]
    public void TriageValidation_MessageUsesTheConstants_NotAHardcoded32768()
    {
        // O teste que falha se alguém voltar a escrever "entre 200 e 32768" fixo:
        // a mensagem precisa carregar os DOIS valores REAIS das constantes.
        var error = Validate(500_000);

        Assert.That(error, Is.Not.Null);
        var message = error!.Message;

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain(AiTokenLimits.MinimumOutputTokens.ToString()),
                "a mensagem usa o piso da constante");
            Assert.That(message, Does.Contain(AiTokenLimits.MaxOutputTokensCeiling.ToString()),
                "a mensagem usa o teto da constante (450000)");
            Assert.That(message, Does.Not.Contain("32768"),
                "nenhum 32768 solto: era o teto antigo, e a mensagem mentiria para o operador");
        });
    }

    private static Error? Validate(int maxOutputTokens) =>
        DepartmentAiSettingsValidator.ValidateLearning(
            maxOutputTokens, skillLearningMode: 0, skillMinEvidence: 1, skillMaxTags: 1,
            weightLearningMode: 0, weightMaxDelta: 0.01m, weightCycleDays: 1,
            weightMin: 0.01m, weightMax: 1.0m);
}
