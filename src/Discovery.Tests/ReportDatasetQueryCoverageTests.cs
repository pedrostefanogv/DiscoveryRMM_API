using System.Text.RegularExpressions;
using Discovery.Core.Enums;

namespace Discovery.Tests;

/// <summary>
/// Guarda contra FALHA SILENCIOSA no switch de datasets.
///
/// Antes, um ReportDatasetType sem case caia no fallback "_ =>" e o relatorio era
/// gerado COM SUCESSO contendo uma unica linha "Dataset not supported." — arquivo
/// inutil, status Completed e notificacao de sucesso. Como adicionar um dataset ao
/// enum e o caminho normal de evolucao, o defeito voltaria na primeira vez que
/// alguem esquecesse o case.
///
/// Estes testes leem o CODIGO-FONTE do metodo (teste de arquitetura): nao ha como
/// provar por reflexao que um tipo de dataset tem consulta implementada, mas da
/// para provar que todo valor do enum tem um case e que o fallback nao devolve
/// mais um resultado de sucesso.
/// </summary>
[TestFixture]
public class ReportDatasetQueryCoverageTests
{
    private static string QueryServiceSource()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "Discovery.Infrastructure")))
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "raiz do repositorio nao encontrada");

        var path = Path.Combine(
            directory!.FullName, "src", "Discovery.Infrastructure", "Services",
            "ReportDatasetQueryService.cs");

        Assert.That(File.Exists(path), Is.True, $"arquivo nao encontrado em {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Trecho do switch de QuerySingleDatasetAsync (do "switch" ate o "};").</summary>
    private static string QuerySingleDatasetSwitch()
    {
        var source = QueryServiceSource();
        var start = source.IndexOf("private async Task<ReportQueryResult> QuerySingleDatasetAsync", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), "metodo QuerySingleDatasetAsync nao encontrado");

        var body = source[start..];
        var switchStart = body.IndexOf("datasetType switch", StringComparison.Ordinal);
        Assert.That(switchStart, Is.GreaterThanOrEqualTo(0), "switch de dataset nao encontrado");

        var switchEnd = body.IndexOf("};", switchStart, StringComparison.Ordinal);
        Assert.That(switchEnd, Is.GreaterThan(switchStart), "fim do switch nao encontrado");

        return body[switchStart..switchEnd];
    }

    [Test]
    public void EveryReportDatasetType_HasACaseInTheQuerySwitch()
    {
        var switchBody = QuerySingleDatasetSwitch();

        var covered = Regex.Matches(switchBody, @"ReportDatasetType\.(\w+)\s*=>")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var declared = Enum.GetValues<ReportDatasetType>()
            .Select(value => value.ToString())
            .ToList();

        var missing = declared.Where(name => !covered.Contains(name)).ToList();

        Assert.That(missing, Is.Empty,
            "todo ReportDatasetType precisa de um case em QuerySingleDatasetAsync. " +
            "Faltando: " + string.Join(", ", missing) +
            ". Sem o case o relatorio cairia no fallback e falharia (ou, antes, 'daria certo' vazio).");

        // E o inverso: case apontando para um valor que nao existe mais no enum.
        var stale = covered.Where(name => !declared.Contains(name)).ToList();
        Assert.That(stale, Is.Empty,
            "case apontando para ReportDatasetType inexistente: " + string.Join(", ", stale));
    }

    [Test]
    public void QuerySwitch_FallbackThrowsInsteadOfReturningASuccessResult()
    {
        var switchBody = QuerySingleDatasetSwitch();

        // Isola o braco de fallback ("_ =>" ate o fim do switch): o texto antigo
        // ainda aparece nos COMENTARIOS que explicam a correcao, entao a guarda
        // precisa olhar o codigo executavel, nao o arquivo inteiro.
        var fallbackIndex = switchBody.LastIndexOf("_ =>", StringComparison.Ordinal);
        Assert.That(fallbackIndex, Is.GreaterThanOrEqualTo(0), "o switch precisa ter um braco de fallback '_ =>'");

        var fallback = switchBody[fallbackIndex..];

        // O fallback nao pode mais MONTAR um ReportQueryResult de sucesso.
        Assert.That(fallback, Does.Not.Contain("new ReportQueryResult"),
            "o fallback nao pode devolver um resultado de sucesso; precisa lancar");
        Assert.That(fallback, Does.Not.Contain("Dataset not supported."),
            "o fallback nao pode devolver texto de erro como conteudo de relatorio");

        Assert.That(fallback, Does.Contain("throw new InvalidOperationException"),
            "o fallback precisa lancar excecao explicita");

        // A mensagem precisa identificar o dataset para o usuario agir.
        Assert.That(fallback, Does.Contain("datasetType"),
            "a mensagem do fallback precisa citar o dataset");
    }

    [Test]
    public void QueryAsync_DoesNotReturnAFakeSuccessWhenDatasetIsUnsupported()
    {
        // Garante que o caminho de execucao nao tem NENHUM outro retorno de
        // "sucesso falso" do tipo colunas=["message"] com texto de erro.
        var source = QueryServiceSource();

        var fakeSuccesses = Regex.Matches(
                source,
                @"Columns\s*=\s*\[\s*""message""\s*\]")
            .Count;

        Assert.That(fakeSuccesses, Is.Zero,
            "nenhum caminho pode devolver um 'sucesso' cujo conteudo e uma mensagem de erro");
    }
}
