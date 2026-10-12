using Discovery.Api.Controllers;

namespace Discovery.Tests;

/// <summary>
/// Testes do nome do instalador custom servido ao agent. Um pacote sem
/// <c>file_name</c> cadastrado caía no fallback pelo packageId, gerando um
/// arquivo SEM extensão — que o agent não consegue executar, porque o `cmd /C`
/// recusa iniciar um binário sem extensão (caso real do app "foxitedito").
/// </summary>
public class CustomInstallerFileNameTests
{
    [Test]
    [TestCase("FoxitPDFEditor.exe", "app-store/custom/installers/x/outro.zip", "FoxitPDFEditor.exe")]
    [TestCase(@"C:\upload\sub\setup.exe", null, "setup.exe")]
    [TestCase("SETUP.MSI", null, "SETUP.MSI")]
    public void Nome_com_extensao_e_preservado(string fileName, string? objectKey, string expected)
    {
        Assert.That(AgentAuthController.ResolveCustomInstallerFileName(fileName, objectKey, "foxitedito"),
            Is.EqualTo(expected));
    }

    [Test]
    [TestCase(null, "app-store/custom/installers/guid/FoxitPDFEditor.exe", "foxitedito.exe")]
    [TestCase("", "app-store/custom/installers/guid/setup.x64.exe", "foxitedito.exe")]
    [TestCase("   ", "app-store/custom/installers/guid/pacote.zip", "foxitedito.zip")]
    public void Sem_file_name_usa_a_extensao_do_objeto_no_storage(string? fileName, string? objectKey, string expected)
    {
        Assert.That(AgentAuthController.ResolveCustomInstallerFileName(fileName, objectKey, "foxitedito"),
            Is.EqualTo(expected));
    }

    [Test]
    [TestCase(null, null)]
    [TestCase(null, "app-store/custom/installers/guid/foxitedito")]
    [TestCase("foxitedito", null)]
    public void Sem_nenhuma_pista_mantem_o_package_id(string? fileName, string? objectKey)
    {
        Assert.That(AgentAuthController.ResolveCustomInstallerFileName(fileName, objectKey, "foxitedito"),
            Is.EqualTo("foxitedito"));
    }

    [Test]
    public void Nome_sem_extensao_mas_objeto_com_extensao_recebe_a_extensao()
    {
        Assert.That(
            AgentAuthController.ResolveCustomInstallerFileName("foxitedito", "app-store/custom/installers/guid/x.exe", "foxitedito"),
            Is.EqualTo("foxitedito.exe"));
    }
}
