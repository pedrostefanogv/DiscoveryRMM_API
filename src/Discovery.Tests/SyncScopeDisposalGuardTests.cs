using System.Runtime.CompilerServices;

namespace Discovery.Tests;

/// <summary>
/// Guarda arquitetural: escopos manuais criados em código de produção precisam
/// ser assíncronos.
///
/// Motivo: o DI por convenção registra tudo como scoped
/// (Discovery.Api/DependencyInjection/ServiceCollectionExtensions.cs) e
/// `IAgentMessaging` é `NatsAgentMessaging`, que só implementa
/// `IAsyncDisposable`. Descartar um escopo sincronamente lança
/// `InvalidOperationException` e **aborta a limpeza** — os demais serviços do
/// escopo (ex.: DbContext) vazam.
///
/// Use `await using var scope = factory.CreateAsyncScope();`. Casos realmente
/// síncronos podem ser marcados com `allow-sync-scope` na própria linha.
/// </summary>
public class SyncScopeDisposalGuardTests
{
    private static string RepoRoot([CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    [Test]
    public void ProductionCodeDoesNotCreateScopesSynchronously()
    {
        var root = RepoRoot();
        var src = Path.Combine(root, "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            // Guarda vale para o código de produção; testes podem montar escopos próprios.
            if (relative.StartsWith($"src{Path.DirectorySeparatorChar}Discovery.Tests", StringComparison.Ordinal))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(".CreateScope()", StringComparison.Ordinal))
                    continue;
                if (lines[i].Contains("allow-sync-scope", StringComparison.Ordinal))
                    continue;

                offenders.Add($"{relative}:{i + 1}");
            }
        }

        Assert.That(
            offenders,
            Is.Empty,
            "Use CreateAsyncScope() + await using (ou marque a linha com 'allow-sync-scope'):\n"
            + string.Join("\n", offenders));
    }
}
