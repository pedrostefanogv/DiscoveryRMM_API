using Discovery.Core.Configuration;

namespace Discovery.Tests;

/// <summary>
/// Guarda do catálogo: features novas precisam entrar em ManagedFields para serem
/// reconhecidas em locks/herança.
/// </summary>
public class ConfigurationFieldCatalogTests
{
    [Test]
    public void ZeroTouch_esta_no_catalogo_de_campos_gerenciados()
    {
        Assert.That(ConfigurationFieldCatalog.ManagedFields, Does.Contain("ZeroTouchEnabled"));
    }
}
