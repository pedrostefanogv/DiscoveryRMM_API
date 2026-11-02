using System.IO;
using Discovery.Core.Cqrs;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Cobertura da classificação de falha por runtime .NET atualizado sob o processo
/// (incidente de 2026-11-02: apt upgrade 10.0.13 -> 10.0.14 removeu o diretório do
/// framework antigo e a Fido2NetLib não conseguiu carregar
/// System.Runtime.Serialization.Primitives).
/// Sem Redis/DB — apenas classificação pura de exceção e parsing de /proc/self/maps.
/// </summary>
using Discovery.Api.DependencyInjection;

public class RuntimeStaleFailureTests
{
    [Test]
    public void IsRuntimeStale_FileNotFoundException_Direto_ClassificaComoStale()
    {
        var ex = new FileNotFoundException(
            "Could not load file or assembly 'System.Runtime.Serialization.Primitives, Version=10.0.0.0'.");

        Assert.That(RuntimeStaleFailure.IsRuntimeStale(ex), Is.True);
    }

    [TestCase(typeof(FileLoadException))]
    [TestCase(typeof(TypeLoadException))]
    [TestCase(typeof(BadImageFormatException))]
    public void IsRuntimeStale_OutrasExcecoesDeLoader_ClassificaComoStale(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.That(RuntimeStaleFailure.IsRuntimeStale(ex), Is.True);
    }

    [Test]
    public void IsRuntimeStale_ExceptionEncapsulada_NoInnerException_ClassificaComoStale()
    {
        // Fido2NetLib encapsula a falha de load do serializer.
        var inner = new FileNotFoundException(
            "Could not load file or assembly 'System.Runtime.Serialization.Primitives'.");
        var ex = new InvalidOperationException("Falha na serializacao da opcao", inner);

        Assert.That(RuntimeStaleFailure.IsRuntimeStale(ex), Is.True);
    }

    [Test]
    public void IsRuntimeStale_ErroComum_NaoClassificaComoStale()
    {
        Assert.That(RuntimeStaleFailure.IsRuntimeStale(new InvalidOperationException("x")), Is.False);
        Assert.That(RuntimeStaleFailure.IsRuntimeStale(new UnauthorizedAccessException("x")), Is.False);
        Assert.That(RuntimeStaleFailure.IsRuntimeStale(new KeyNotFoundException()), Is.False);
        Assert.That(RuntimeStaleFailure.IsRuntimeStale(null), Is.False);
    }

    [Test]
    public void IsRuntimeStale_ArquivoDeDadosAusente_NaoClassificaComoStale()
    {
        // FileNotFoundException de arquivo de dados (tem FileName e não é carga de
        // assembly) NÃO deve virar 503 "manutenção": é defeito, precisa de log de erro.
        var ex = new FileNotFoundException("Could not find file '/etc/discovery/plantilla.json'.", "/etc/discovery/plantilla.json");

        Assert.That(RuntimeStaleFailure.IsRuntimeStale(ex), Is.False);
    }

    [Test]
    public void IsRuntimeStale_FileNotFoundExceptionSemMarcador_NaoClassificaComoStale()
    {
        var ex = new FileNotFoundException("Arquivo de layout nao encontrado.");

        Assert.That(RuntimeStaleFailure.IsRuntimeStale(ex), Is.False);
    }

    [Test]
    public void CreateError_DevolveCodigoRuntimeStaleComMensagemPtBr()
    {
        var error = RuntimeStaleFailure.CreateError();

        Assert.Multiple(() =>
        {
            Assert.That(error.Code, Is.EqualTo("RuntimeStale"));
            Assert.That(error.Message, Does.Contain("temporariamente indisponível"));
        });
    }

    [Test]
    public void FindDeletedFrameworkLibraries_MapsComFrameworkDeletado_RetornaCaminhos()
    {
        const string maps =
            "7f0e00000000-7f0e00100000 r--p 00000000 08:02 123 " +
            "/usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.13/System.Private.CoreLib.dll (deleted)\n" +
            "7f0e00100000-7f0e00200000 r--p 00000000 08:02 124 " +
            "/usr/lib/dotnet/shared/Microsoft.AspNetCore.App/10.0.13/Microsoft.AspNetCore.Mvc.Core.dll (deleted)\n" +
            "7f0e00200000-7f0e00300000 r--p 00000000 08:02 125 " +
            "/opt/discovery-api/current/Fido2NetLib.dll\n" +
            "7f0e00300000-7f0e00400000 r--p 00000000 08:02 126 " +
            "/usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll";

        var deleted = RuntimeStaleInspector.FindDeletedFrameworkLibraries(maps);

        Assert.That(deleted, Is.EqualTo(new[]
        {
            "/usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.13/System.Private.CoreLib.dll",
            "/usr/lib/dotnet/shared/Microsoft.AspNetCore.App/10.0.13/Microsoft.AspNetCore.Mvc.Core.dll"
        }));
    }

    [Test]
    public void FindDeletedFrameworkLibraries_MapsSaudavel_RetornaVazio()
    {
        const string maps =
            "7f0e00000000-7f0e00100000 r--p 00000000 08:02 123 " +
            "/usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll\n" +
            "7f0e00100000-7f0e00200000 r--p 00000000 08:02 124 " +
            "/opt/discovery-api/current/Discovery.Api.dll (deleted)";

        var deleted = RuntimeStaleInspector.FindDeletedFrameworkLibraries(maps);

        Assert.That(deleted, Is.Empty);
    }
}