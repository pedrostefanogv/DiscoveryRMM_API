using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Validacao do NOME do componente Icon do A2UI (achado R7).
///
/// CONTEXTO: com a webfont Material Symbols empacotada localmente, o Icon do
/// catalogo vendorado volta a emitir <span class="material-symbols-outlined
/// a2ui-icon">NAME</span> e o glifo e resolvido por LIGADURA. Um nome fora do
/// catalogo nao vira mais texto (o renderer cai no fallback), mas entrega outro
/// icone - entao o validador descarta a surface ANTES de enviar, com o mesmo
/// criterio dos demais erros.
///
/// Contrato verificado (AiChatA2uiValidator.cs, CheckIconName + IsSafeSvgPathData):
///   - 59 nomes do catalogo, comparacao case-INSENSITIVE;
///   - alias snake_case (22, ex. arrow_back -> arrowBack);
///   - forma alternativa { "path": "<d de SVG>" } com charset de desenho.
/// O valor de { path } vem do LLM e termina no atributo d de um <svg>: o charset
/// e defesa em profundidade contra injecao de markup.
///
/// NAO confundir com AiChatA2uiCatalogParityTests (paridade de COMPONENTES) nem
/// com AiChatA2uiNavigationPromptTests (texto do prompt).
/// </summary>
public class AiChatA2uiIconValidationTests
{
    private static List<string> Lines(string content) =>
        content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Monta uma surface minima com um Icon cujo name e o JSON informado.</summary>
    private static List<string> SurfaceWithIcon(string nameJson)
    {
        const string createSurface = "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"icon_card\",\"catalogId\":\"https://a2ui.org/specification/v0_9/basic_catalog.json\"}}";
        var update = "{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"icon_card\",\"components\":[{\"id\":\"root\",\"component\":\"Column\",\"children\":[\"icone\"]},{\"id\":\"icone\",\"component\":\"Icon\",\"name\":" + nameJson + "}]}}";
        return Lines(createSurface + "\n" + update);
    }

    // ------------------------- Nomes validos -------------------------

    [TestCase("warning")]
    [TestCase("folder")]
    [TestCase("shoppingCart")]
    [TestCase("accountCircle")]
    [TestCase("help")]
    public void Validate_WhenIconNameIsInTheCatalog_KeepsMessages(string name)
    {
        var (valid, errors) = AiChatA2uiValidator.Validate(SurfaceWithIcon(Quote(name)));

        Assert.That(errors, Is.Empty, string.Join(" | ", errors));
        Assert.That(valid, Has.Count.EqualTo(2));
    }

    /// <summary>Envolve um valor em aspas JSON.</summary>
    private static string Quote(string value) => "\"" + value + "\"";

    [Test]
    public void Validate_WhenIconNameDiffersOnlyInCase_KeepsMessages()
    {
        // O renderer compara sem distinguir caixa; o validador nao pode descartar
        // uma surface que o renderer aceita.
        var (valid, errors) = AiChatA2uiValidator.Validate(SurfaceWithIcon(Quote("WARNING")));

        Assert.That(errors, Is.Empty, string.Join(" | ", errors));
        Assert.That(valid, Has.Count.EqualTo(2));
    }

    [TestCase("arrow_back", "arrowBack")]
    [TestCase("shopping_cart", "shoppingCart")]
    [TestCase("account_circle", "accountCircle")]
    [TestCase("volume_up", "volumeUp")]
    public void Validate_WhenIconNameIsSnakeCaseAlias_KeepsMessages(string snake, string camel)
    {
        // O LLM as vezes escreve o nome com underscore (e a ligadura da fonte).
        var (valid, errors) = AiChatA2uiValidator.Validate(SurfaceWithIcon(Quote(snake)));

        Assert.That(errors, Is.Empty, snake + " (alias de " + camel + ") foi rejeitado: " + string.Join(" | ", errors));
        Assert.That(valid, Has.Count.EqualTo(2));
    }

    // ------------------------- Rejeicoes R1-R4 (nome) -------------------------

    [Test]
    public void Validate_WhenIconHasNoName_DropsSurface()
    {
        var content = Lines(
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"icon_card\",\"catalogId\":\"https://a2ui.org/specification/v0_9/basic_catalog.json\"}}" + "\n" +
            "{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"icon_card\",\"components\":[{\"id\":\"root\",\"component\":\"Column\",\"children\":[\"icone\"]},{\"id\":\"icone\",\"component\":\"Icon\"}]}}");

        var (valid, errors) = AiChatA2uiValidator.Validate(content);

        Assert.That(valid, Is.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("name"));
    }

    [Test]
    public void Validate_WhenIconNameIsEmpty_DropsSurface()
    {
        var (valid, errors) = AiChatA2uiValidator.Validate(SurfaceWithIcon(Quote("   ")));

        Assert.That(valid, Is.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("vazio"));
    }

    [TestCase("42")]
    [TestCase("true")]
    [TestCase("null")]
    [TestCase("[\"warning\"]")]
    public void Validate_WhenIconNameIsNotStringNorPathObject_DropsSurface(string nameJson)
    {
        var (valid, errors) = AiChatA2uiValidator.Validate(SurfaceWithIcon(nameJson));

        Assert.That(valid, Is.Empty, "name=" + nameJson + " deveria ser rejeitado");
        Assert.That(string.Join(" | ", errors), Does.Contain("tipo inválido"));
    }

    [TestCase("notAnIcon")]
    [TestCase("favorite_off_typo")]
    [TestCase("StatusBar")]
    public void Validate_WhenIconNameIsNotInTheCatalog_DropsSurface(string name)
    {
        var (valid, errors) = AiChatA2uiValidator.Validate(SurfaceWithIcon(Quote(name)));

        Assert.That(valid, Is.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("nome de glifo desconhecido"));
    }

    // ------------------------- Forma { path } (R5-R8) -------------------------

    [TestCase("M12 2L2 7v10l10 5 10-5V7z")]
    [TestCase("M 4.5,3.2 C 1,4 0,8 2,12 L3,13")]
    [TestCase("M1.5e-3 2A 3 4 0 0 1 5 6Z")]
    public void Validate_WhenIconPathIsSafeSvgData_KeepsMessages(string path)
    {
        var (valid, errors) = AiChatA2uiValidator.Validate(SurfaceWithIcon(PathJson(path)));

        Assert.That(errors, Is.Empty, "path " + path + " foi rejeitado: " + string.Join(" | ", errors));
        Assert.That(valid, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// Monta o objeto JSON { "path": "<valor>" } com o valor ESCAPADO como JSON.
    /// O escape e obrigatorio: varios payloads de injecao contem aspas, e sem
    /// escapar o JSON ficaria invalido — o validador descartaria a surface por
    /// ERRO DE PARSE, nao por charset, e o teste passaria pelo motivo errado
    /// (provaria nada sobre a defesa do path).
    /// </summary>
    private static string PathJson(string value) => "{\"path\":" + Quote(value) + "}";

    /// <summary>Escapa um valor como string JSON (aspas e barra invertida).</summary>
    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    [Test]
    public void Validate_WhenIconPathObjectHasNoPathKey_DropsSurface()
    {
        var (valid, errors) = AiChatA2uiValidator.Validate(SurfaceWithIcon("{\"d\":\"M0 0\"}"));

        Assert.That(valid, Is.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("path"));
    }

    [Test]
    public void Validate_WhenIconPathIsNotTextual_DropsSurface()
    {
        var (valid, errors) = AiChatA2uiValidator.Validate(SurfaceWithIcon("{\"path\":123}"));

        Assert.That(valid, Is.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("não textual"));
    }

    [Test]
    public void Validate_WhenIconPathIsEmpty_DropsSurface()
    {
        var (valid, errors) = AiChatA2uiValidator.Validate(SurfaceWithIcon(PathJson("  ")));

        Assert.That(valid, Is.Empty);
        Assert.That(string.Join(" | ", errors), Does.Contain("vazio"));
    }

    // ------------- Injecao: o { path } vai para o atributo d do <svg> -------------
    // Defesa em profundidade: mesmo que o componente monte o atributo por binding
    // (sem interpolacao de HTML), o path nunca deve carregar markup.

    [TestCase("\"><script>alert(1)</script>")]
    [TestCase("\" onload=\"alert(1)")]
    [TestCase(";DROP TABLE x")]
    [TestCase("M0 0\" /><script>x</script>")]
    [TestCase("M0 0;evil")]
    [TestCase("M0 0/../etc")]
    [TestCase("M0 0%00")]
    [TestCase("M0 0`whoami`")]
    [TestCase("M0 0$(id)")]
    public void Validate_WhenIconPathCarriesMarkupOrSql_DropsSurface(string malicious)
    {
        // O valor vai ESCAPADO para o JSON, para que o unico motivo de rejeicao
        // possivel seja o charset do path (e nao um JSON invalido).
        var (valid, errors) = AiChatA2uiValidator.Validate(SurfaceWithIcon(PathJson(JsonEscape(malicious))));

        Assert.That(valid, Is.Empty, "path malicioso aceito: " + malicious);
        Assert.That(string.Join(" | ", errors), Does.Contain("charset permitido de path SVG"),
            "o payload foi rejeitado por outro motivo (esperado: charset do path): " + malicious);
    }

    [Test]
    public void Validate_WhenIconPathIsSafeWithExtraKeys_NoErrorIsRaised_ObservedAsymmetry()
    {
        // COMPORTAMENTO OBSERVADO (nao e contrato garantido): o validador C# so
        // procura a chave path e NAO rejeita chaves extras, enquanto o schema zod
        // do renderer e .strict() e recusaria o componente. E uma assimetria
        // conhecida entre servidor e renderer - registrada aqui para o verifier
        // decidir. Este teste trava o comportamento ATUAL, nao o desejado.
        var (_, errors) = AiChatA2uiValidator.Validate(SurfaceWithIcon("{\"path\":\"M0 0Z\",\"extra\":1}"));

        Assert.That(errors, Is.Empty,
            "o validador passou a rejeitar chaves extras - atualize este teste e a documentacao da assimetria");
    }

    // ------------------------- Catalogo exposto -------------------------

    [Test]
    public void IconNames_ExposesTheFullCatalogOf59Names()
    {
        // Trava o numero: se o catalogo vendorado mudar, o teste de paridade do
        // lado do agente (tools/a2ui/test/icon.test.mjs) precisa ser revisto junto.
        Assert.That(AiChatA2uiValidator.IconNames, Has.Count.EqualTo(59));
    }

    [Test]
    public void IconNames_ContainsTheNamesUsedInTheSystemPromptExamples()
    {
        // O prompt cita warning e folder como exemplos: se sumirem do catalogo,
        // o exemplo do prompt passaria a ser descartado pelo servidor.
        Assert.That(AiChatA2uiValidator.IconNames, Does.Contain("warning"));
        Assert.That(AiChatA2uiValidator.IconNames, Does.Contain("folder"));
    }
}
