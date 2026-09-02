using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Testes de integração do parser contra manifests REAIS do winget-pkgs
/// (usa o clone shallow em TEMP se disponível; pula o teste caso contrário).
/// </summary>
public class WingetManifestRealDataTests
{
    private static string? FindClone()
    {
        var temp = Path.Combine(Path.GetTempPath(), "winget-pkgs-test", "manifests");
        return Directory.Exists(temp) ? temp : null;
    }

    [Test]
    public void Parse_RealClone_SamplePackages_ProduceValidPackages()
    {
        var manifestsRoot = FindClone();
        if (manifestsRoot is null)
        {
            Assert.Ignore("Clone do winget-pkgs não encontrado em TEMP (winget-pkgs-test). Clone shallow para rodar este teste.");
            return;
        }

        var parser = new WingetManifestParser();
        var tested = 0;
        var ok = 0;
        var failures = new List<string>();

        // Amostra: primeiros N diretórios de versão encontrados na varredura.
        var versionDirs = Directory
            .EnumerateDirectories(manifestsRoot, "*", SearchOption.AllDirectories)
            .Where(d => Directory.EnumerateFiles(d, "*.installer.yaml").Any()
                        || File.Exists(Path.Combine(d, "Installer.yaml")))
            .Take(200)
            .ToList();

        Assert.That(versionDirs, Is.Not.Empty, "Nenhum diretório de versão encontrado no clone.");

        foreach (var dir in versionDirs)
        {
            var version = Path.GetFileName(dir);

            // O nome do diretório do pacote pode diferir do PackageIdentifier real
            // (ex. publisher "0-don" → dir "clippy" p/ id "0-don.clippy").
            // Deriva o PackageIdentifier do nome do arquivo .installer.yaml.
            var installerFile = Directory.EnumerateFiles(dir, "*.installer.yaml").FirstOrDefault()
                                ?? (File.Exists(Path.Combine(dir, "Installer.yaml"))
                                    ? Path.Combine(dir, "Installer.yaml") : null);
            if (installerFile is null)
                continue;

            var fileName = Path.GetFileName(installerFile);
            var packageId = fileName.EndsWith(".installer.yaml", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^".installer.yaml".Length]
                : Path.GetFileName(Path.GetDirectoryName(dir))!; // genérico Installer.yaml

            if (string.IsNullOrEmpty(packageId))
                continue;

            tested++;
            var result = parser.Parse(packageId, version, dir);

            if (result is not null)
            {
                ok++;
                if (string.IsNullOrWhiteSpace(result.MetadataJson))
                    failures.Add($"{packageId}: MetadataJson vazio");
            }
            else
            {
                failures.Add($"{packageId} {version}: parse retornou null");
            }
        }

        TestContext.Out.WriteLine($"Testados: {tested}, OK: {ok}, Falhas: {failures.Count}");
        foreach (var f in failures.Take(10))
            TestContext.Out.WriteLine(f);

        // Parser tolerante: aceitamos pequena taxa de falha (manifests malformados existem),
        // mas a grande maioria deve parsear.
        Assert.That(ok, Is.GreaterThanOrEqualTo((int)(tested * 0.9)),
            $"Taxa de sucesso abaixo de 90%. Falhas: {string.Join("; ", failures.Take(10))}");
    }
}
