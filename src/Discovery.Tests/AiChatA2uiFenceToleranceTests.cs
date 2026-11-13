using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/**
 * Tolerancia a CERCA QUEBRADA do bloco A2UI (sexta onda).
 *
 * O DEFEITO REAL (chat.db, turno 1025): o modelo emitiu ="a2ui" no lugar de ```a2ui.
 * O extrator so reconhecia a cerca oficial, entao o bloco inteiro caiu no texto visivel
 * e o usuario viu o JSON CRU em vez do card. O conteudo estava INTEGRO (createSurface +
 * updateComponents com 17 componentes e o root) - nao era truncamento.
 *
 * LIMITE DE TOLERANCIA: aceitar SOMENTE cerca curta + "a2ui" ocupando a LINHA INTEIRA e,
 * para as formas toleradas, exigir que a linha IMEDIATAMENTE seguinte seja uma mensagem
 * A2UI valida (condicao 2). Sem essa trava, prosa com "a2ui" abriria bloco e ENGOLIRIA o
 * texto do usuario - pior que o defeito original.
 *
 * O caminho OFICIAL (```a2ui) mantem o comportamento ANTERIOR byte a byte, SEM a trava:
 * aplica-la ali seria regressao (abertura oficial seguida de linha em branco deixaria de abrir).
 */
[TestFixture]
public class AiChatA2uiFenceToleranceTests
{
    // Fixture REAL do turno 1025 (chat.db): cerca e as DUAS linhas JSON sao literais
    // do banco. A prosa e ASCII sintetica - o foco aqui e a CERCA, e assim o teste
    // nao depende de acentuacao.
    private const string BrokenFence = "=\"a2ui\"";

    private const string RealCreateSurface =
        "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"health_check\",\"catalogId\":\"https://a2ui.org/specification/v0_9/basic_catalog.json\"}}";

    private const string RealUpdateComponents =
        "{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"health_check\",\"components\":[" +
        "{\"id\":\"root\",\"component\":\"Column\",\"children\":[\"title\",\"sub\",\"div1\",\"perf\",\"div2\",\"alerts\",\"div3\",\"note\"]}," +
        "{\"id\":\"title\",\"component\":\"Text\",\"text\":\"Diagnostico do computador\",\"variant\":\"h3\"}," +
        "{\"id\":\"sub\",\"component\":\"Text\",\"text\":\"AORUSAXV2 - Windows 11 Pro\",\"variant\":\"caption\"}," +
        "{\"id\":\"div1\",\"component\":\"Divider\"}," +
        "{\"id\":\"perf\",\"component\":\"Column\",\"children\":[\"p1\",\"p2\",\"p3\",\"p4\"]}," +
        "{\"id\":\"p1\",\"component\":\"Text\",\"text\":\"CPU: 10% de uso\"}," +
        "{\"id\":\"p2\",\"component\":\"Text\",\"text\":\"Memoria: 30%\"}," +
        "{\"id\":\"p3\",\"component\":\"Text\",\"text\":\"Disco C: 550 GB livres\"}," +
        "{\"id\":\"p4\",\"component\":\"Text\",\"text\":\"Discos fisicos OK\"}," +
        "{\"id\":\"div2\",\"component\":\"Divider\"}," +
        "{\"id\":\"alerts\",\"component\":\"Column\",\"children\":[\"a1\",\"a2\",\"a3\",\"a4\"]}," +
        "{\"id\":\"a1\",\"component\":\"Text\",\"text\":\"Antivirus desligado.\"}," +
        "{\"id\":\"a2\",\"component\":\"Text\",\"text\":\"Agente reiniciou sozinho.\"}," +
        "{\"id\":\"a3\",\"component\":\"Text\",\"text\":\"Inventario travou hoje.\"}," +
        "{\"id\":\"a4\",\"component\":\"Text\",\"text\":\"Falhas antigas da Store.\"}," +
        "{\"id\":\"div3\",\"component\":\"Divider\"}," +
        "{\"id\":\"note\",\"component\":\"Text\",\"text\":\"Nao ha reinicializacao pendente.\"}]}}";

    private const string ProseAfter =
        "Fiz uma varredura completa na sua maquina. O computador esta saudavel e rapido.";

    /// <summary>Conteudo do turno 1025: cerca literal + JSON literal + prosa sintetica.</summary>
    private static string RealTurn1025(string fence) =>
        ProseAfter + "\n\n" + fence + "\n" + RealCreateSurface + "\n" + RealUpdateComponents +
        "\n```\n\nQuer que eu verifique as atualizacoes pendentes?";

    // ---- A) REGRESSAO DO CAMINHO OFICIAL (sem a trava) ----

    [Test]
    public void OfficialFence_StillOpensAndExtracts_WithoutRegression()
    {
        var content = "Card:\n\n```a2ui\n" + RealCreateSurface + "\n" + RealUpdateComponents + "\n```\n\nFim.";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.Count.EqualTo(2), "createSurface + updateComponents");
            Assert.That(clean, Does.Not.Contain("createSurface"), "o JSON nao pode sobrar no visivel");
            Assert.That(clean, Does.Contain("Card:").And.Contain("Fim."));
        });
    }

    [Test]
    public void OfficialFence_FollowedByBlankLine_StillOpens()
    {
        // GUARD DA DECISAO DE DESIGN: a trava da condicao 2 NAO se aplica ao oficial.
        // Se fosse aplicada, esta abertura (linha em branco logo apos a cerca) deixaria
        // de abrir e o card voltaria a nao renderizar - regressao silenciosa.
        var content = "```a2ui\n\n" + RealCreateSurface + "\n```\n";

        var (_, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(messages, Has.Count.EqualTo(1),
            "oficial + linha em branco DEVE continuar abrindo (comportamento anterior preservado)");
    }

    [Test]
    public void OfficialFence_FollowedByBlankLineAndNonJson_StillOpensAndRemovesTheBlock()
    {
        var content = "```a2ui\n\nisto nao e json\n```\n\nTexto final.";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Is.Empty);
            Assert.That(clean, Does.Not.Contain("isto nao e json"), "o bloco oficial e removido do visivel");
            Assert.That(clean, Does.Contain("Texto final."));
        });
    }

    // ---- B) CASOS ACEITOS (o bug e as variacoes plausiveis) ----

    [Test]
    public void BrokenFence_RealTurn1025_IsRecognizedAndExtractsTheCard()
    {
        // O TESTE DO BUG: cerca =\"a2ui\" literal do chat.db turno 1025.
        var content = RealTurn1025(BrokenFence);

        var diagnostics = new List<string>();
        var (clean, messages) = AiChatA2uiExtractor.Extract(content, diagnostics.Add);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.Count.EqualTo(2), "createSurface + updateComponents precisam ser extraidos");
            Assert.That(messages[0], Does.Contain("createSurface"));
            Assert.That(messages[0], Does.Contain("health_check"));
            Assert.That(messages[1], Does.Contain("updateComponents"));
            Assert.That(messages[1], Does.Contain("\"id\":\"root\""), "a definicao com root precisa vir");
            Assert.That(clean, Does.Not.Contain("createSurface"), "o JSON cru NAO pode sobrar no visivel");
            Assert.That(clean, Does.Not.Contain(BrokenFence), "a cerca quebrada nao pode sobrar");
            Assert.That(clean, Does.Contain(ProseAfter), "a prosa ao redor e preservada");
            Assert.That(clean, Does.Contain("atualizacoes pendentes"), "o texto depois do bloco e preservado");
            Assert.That(diagnostics, Has.Some.Contains("tolerada"), "a tolerancia precisa ser diagnosticada");
        });
    }

    [Test]
    public void RealTurn1025_ProducesAValidatableCard()
    {
        var (_, messages) = AiChatA2uiExtractor.Extract(RealTurn1025(BrokenFence));

        var (validated, errors) = AiChatA2uiValidator.Validate(messages);

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty, "o card do turno 1025 e valido - era so a cerca");
            Assert.That(validated, Has.Count.EqualTo(2));
            Assert.That(AiChatA2uiExtractor.SurfacesMissingDefinition(messages), Is.Empty,
                "health_check tem definicao com root");
        });
    }

    [Test]
    public void AcceptedVariations_AllOpenTheBlock([Values(
        "```a2ui", "=\"a2ui\"", "a2ui", "``a2ui", "`a2ui", "=a2ui", "```A2UI", "=\"A2UI\"")] string fence)
    {
        var content = RealTurn1025(fence);

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.Count.EqualTo(2), "a cerca " + fence + " precisa abrir o bloco");
            Assert.That(clean, Does.Not.Contain("createSurface"), "o JSON nao pode sobrar");
            Assert.That(clean, Does.Contain(ProseAfter), "a prosa sobrevive");
        });
    }

    [Test]
    public void AcceptedVariation_WithSurroundingSpaces_OpensTheBlock()
    {
        var content = RealTurn1025("  =\"a2ui\"  ");

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.Count.EqualTo(2), "espacos ao redor nao podem impedir a tolerancia");
            Assert.That(clean, Does.Not.Contain("createSurface"));
        });
    }
    // ---- C) FALSOS POSITIVOS - A PARTE MAIS IMPORTANTE ----

    [Test]
    public void ProseMentioningA2ui_DoesNotOpenABlock()
    {
        var content = "Vou usar o a2ui para montar o card\nSegue o resultado:\n\nAqui esta o texto.";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Is.Empty);
            Assert.That(clean, Is.EqualTo(content), "o texto precisa sair INTACTO - nada removido");
        });
    }

    [Test]
    public void ProseEndingWithA2ui_FollowedByValidA2uiJson_DoesNotOpenABlock()
    {
        // O CASO MAIS PERIGOSO: a prosa TERMINA com "a2ui" e a linha seguinte E JSON
        // A2UI VALIDO. Aqui a trava da condicao 2 NAO salva - ela passaria. Quem precisa
        // barrar e a CONDICAO 1: a linha tem prosa, entao nao e cerca. Este teste prova
        // que a gramatica da LINHA tem dentes; se falhar, o defeito esta nela.
        var content = "Vou usar o a2ui\n" + RealCreateSurface + "\nSegue o resultado:";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Is.Empty, "a prosa NAO pode abrir bloco mesmo com JSON valido abaixo");
            Assert.That(clean, Is.EqualTo(content), "a prosa e o JSON de origem saem INTACTOS");
        });
    }

    [Test]
    public void ProseEndingWithA2ui_FollowedByOfficialBlock_KeepsTheProseAndExtractsTheBlock()
    {
        var content = "Vou usar o a2ui\n```a2ui\n" + RealCreateSurface + "\n```\nFim.";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.Count.EqualTo(1));
            Assert.That(clean, Does.Contain("Vou usar o a2ui"), "a prosa do usuario nao pode ser engolida");
            Assert.That(clean, Does.Contain("Fim."));
            Assert.That(clean, Does.Not.Contain("createSurface"));
        });
    }

    [Test]
    public void PlausibleFence_FollowedByNonA2uiJson_DoesNotOpenABlock()
    {
        // E exatamente o que a TRAVA da condicao 2 barra: a cerca e plausivel, mas o JSON
        // nao e A2UI (sem version/verbo).
        var content = "=a2ui\n{\"foo\":1}\nTexto final.";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Is.Empty);
            Assert.That(clean, Is.EqualTo(content), "texto intacto: a cerca plausivel nao abriu nada");
        });
    }

    [Test]
    public void PlausibleFence_FollowedByBlankLine_DoesNotOpenABlock()
    {
        // A trava exige a linha IMEDIATAMENTE seguinte; nao pula brancas.
        var content = "=a2ui\n\n" + RealCreateSurface + "\nFim.";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Is.Empty, "linha em branco nao satisfaz a condicao 2");
            Assert.That(clean, Is.EqualTo(content));
        });
    }

    [Test]
    public void PlausibleFence_AtEndOfContent_DoesNotOpenABlock()
    {
        var content = "Texto final.\n=a2ui";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Is.Empty, "sem linha seguinte, a tolerancia nao se aplica (fail-safe)");
            Assert.That(clean, Is.EqualTo(content));
        });
    }

    [Test]
    public void A2uiInsideACommonCodeBlock_IsNotTreatedAsA2uiAndTheBlockStaysIntact()
    {
        // 'a2ui' dentro de um bloco js: markdown comum, conteudo NAO pode ser tocado.
        var content = "Exemplo de codigo:\n\n```js\nconst a2ui = require('a2ui');\nconsole.log(a2ui);\n```\n\nFim.";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Is.Empty, "bloco js nao e bloco A2UI");
            Assert.That(clean, Does.Contain("const a2ui = require('a2ui');"), "o conteudo do bloco js sai INTACTO");
            Assert.That(clean, Does.Contain("console.log(a2ui);"));
            Assert.That(clean, Does.Contain("```js"));
        });
    }

    [Test]
    public void A2uiInTheMiddleOfASentence_DoesNotOpenABlock()
    {
        var content = "o a2ui e legal\n" + RealCreateSurface + "\nFim.";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Is.Empty);
            Assert.That(clean, Is.EqualTo(content), "texto intacto");
        });
    }

    [Test]
    public void FourBackticks_AreNotAToleratedFence()
    {
        // Limite documentado: 4+ crases voltam a ser cerca de markdown comum.
        var content = "````a2ui\n" + RealCreateSurface + "\nFim.";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Is.Empty, "4 crases e cerca de markdown, nao abertura A2UI");
            Assert.That(clean, Does.Contain("````a2ui"));
        });
    }
    // ---- D) LIMITE CONHECIDO E ACEITO DA TOLERANCIA ----

    [Test]
    public void KnownLimit_AnIsolatedA2uiLineIsConsumedEvenInLegitimateProse()
    {
        // LIMITE CONHECIDO (reportado, nao "consertado"): a forma tolerada mais frouxa e
        // "a2ui" isolado. Se uma prosa legitima SOBRE o proprio recurso tiver uma linha so
        // com "a2ui" e a linha seguinte for JSON A2UI valido, a palavra "a2ui" e REMOVIDA
        // do texto visivel (vira abertura de bloco).
        //
        // E o custo aceito da tolerancia: a alternativa (exigir so a cerca oficial) era o
        // bug. A trava garante que o DANO e limitado - o texto seguinte NAO e engolido,
        // porque ele vira o conteudo do bloco.
        // A cerca de FECHAMENTO e necessaria: sem ela o bloco fica aberto e absorve TODO
        // o resto do conteudo (comportamento preexistente do extrator, igual ao do caminho
        // oficial - ver Extract_WhenUnclosedBlock_ProcessesRemainingLines).
        var content = "O recurso se chama\na2ui\n" + RealCreateSurface + "\n```\nFim.";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.Count.EqualTo(1), "a linha isolada a2ui abre o bloco (tolerancia)");
            Assert.That(clean, Does.Not.Contain("a2ui"), "a palavra a2ui e consumida como cerca - limite conhecido");
            Assert.That(clean, Does.Contain("O recurso se chama"), "a prosa anterior sobrevive");
            Assert.That(clean, Does.Contain("Fim."), "a prosa POSTERIOR sobrevive - o dano nao se espalha");
        });
    }

    // ---- E) INVARIANTE: nunca perder conteudo nao relacionado ----

    [Test]
    public void RejectedFences_AlwaysLeaveTheContentByteIdentical()
    {
        // Invariante: quando a abertura NAO e aceita, o conteudo tem que sair identico -
        // nem cerca, nem JSON, nem prosa podem ser removidos.
        string[] rejected =
        [
            "Vou usar o a2ui para montar o card\nSegue o resultado:\n\nAqui esta o texto.",
            "Vou usar o a2ui\n" + RealCreateSurface + "\nSegue o resultado:",
            "=a2ui\n{\"foo\":1}\nTexto final.",
            "=a2ui\n\n" + RealCreateSurface + "\nFim.",
            "Texto final.\n=a2ui",
            "o a2ui e legal\n" + RealCreateSurface + "\nFim.",
            "````a2ui\n" + RealCreateSurface + "\nFim.",
            "```js\nconst a2ui = 1;\n```\nFim.",
        ];

        foreach (var content in rejected)
        {
            var (clean, messages) = AiChatA2uiExtractor.Extract(content);
            Assert.Multiple(() =>
            {
                Assert.That(messages, Is.Empty, "nao pode extrair nada de: " + content.Split(char.Parse("\n"))[0]);
                Assert.That(clean, Is.EqualTo(content), "o conteudo precisa sair intacto: " + content.Split(char.Parse("\n"))[0]);
            });
        }
    }

    // ---- F) CONTRATO DA FUNCAO PURA (o coracao da trava) ----

    [Test]
    public void IsToleratedA2uiOpenLine_RequiresAValidA2uiLineRightAfter()
    {
        Assert.Multiple(() =>
        {
            // Cerca valida + JSON A2UI valido => true.
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("=\"a2ui\"", RealCreateSurface), Is.True);
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("a2ui", RealCreateSurface), Is.True);

            // Cerca valida + linha seguinte inutil => false.
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("=\"a2ui\"", null), Is.False, "ultima linha: fail-safe");
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("=\"a2ui\"", ""), Is.False);
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("=\"a2ui\"", "   "), Is.False);
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("=\"a2ui\"", "texto comum"), Is.False);
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("=\"a2ui\"", "{\"foo\":1}"), Is.False, "JSON nao-A2UI");
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("=\"a2ui\"", "{\"version\":\"\",\"createSurface\":{}}"),
                Is.False, "version vazia nao e mensagem A2UI");
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("=\"a2ui\"", "{\"version\":\"v0.9\"}"),
                Is.False, "sem verbo A2UI nao e mensagem A2UI");

            // Linha de abertura nao-tolerada => false, mesmo com JSON valido abaixo.
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("Vou usar o a2ui", RealCreateSurface), Is.False,
                "prosa nao abre - e a condicao 1");
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("````a2ui", RealCreateSurface), Is.False);
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("= a2ui =", RealCreateSurface), Is.False,
                "o = so vale como prefixo");
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine(null, RealCreateSurface), Is.False);
            Assert.That(AiChatA2uiExtractor.IsToleratedA2uiOpenLine("   ", RealCreateSurface), Is.False);
        });
    }

    // ---- G) DEFEITO CORRIGIDO: raiz JSON NAO-OBJECT nao pode lancar ----

    [Test]
    public void IsToleratedA2uiOpenLine_ReturnsFalseForNonObjectJsonRoots_WithoutThrowing()
    {
        // REGRESSAO DO DEFEITO (achado por esta task): IsValidA2uiMessage fazia
        // JsonDocument.Parse (que ACEITA array/escalar como raiz) e em seguida
        // TryGetProperty("version"), que LANCA InvalidOperationException em raiz nao-Object.
        // O catch do metodo so pegava JsonException, entao a excecao escapava do Extract e
        // QUEBRAVA O TURNO. Com a tolerancia, essa funcao passou a receber qualquer linha
        // seguinte de uma cerca quebrada — inclusive JSON de forma arbitraria.
        //
        // O predicado NAO PODE LANCAR: entrada invalida devolve false (fail-safe).
        string[] nonObjectRoots = ["[1,2,3]", "42", "\"texto\"", "true", "false", "null", "[]", "{}"];

        foreach (var json in nonObjectRoots)
        {
            Assert.DoesNotThrow(() =>
            {
                var accepted = AiChatA2uiExtractor.IsToleratedA2uiOpenLine("=\"a2ui\"", json);
                Assert.That(accepted, Is.False, $"raiz nao-Object nao e mensagem A2UI: {json}");
            }, $"a condicao 2 nao pode lancar para a entrada {json}");
        }
    }

    [Test]
    public void BrokenFence_FollowedByJsonArray_DoesNotThrowAndLeavesTheTextIntact()
    {
        // CENARIO DE PRODUCAO do defeito: cerca quebrada seguida de um array JSON.
        // Antes do fix, o Extract LANCAVA aqui (o chat quebrava em vez de apenas nao
        // renderizar o card). Agora: nada e extraido e o texto sai INTACTO.
        const string content = "Texto antes\n=\"a2ui\"\n[{\"foo\":1}]\nTexto depois.";

        (string Clean, List<string> Messages) result = default;
        Assert.DoesNotThrow(() => result = AiChatA2uiExtractor.Extract(content),
            "cerca quebrada + array JSON nao pode lancar — o turno do chat nao pode quebrar");

        Assert.Multiple(() =>
        {
            Assert.That(result.Messages, Is.Empty, "array JSON nao e mensagem A2UI");
            Assert.That(result.Clean, Is.EqualTo(content), "o texto precisa sair INTACTO");
        });
    }
}
