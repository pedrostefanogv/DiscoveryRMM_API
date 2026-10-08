using Discovery.Core.Entities;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Contrato A2UI para AÇÕES DE NAVEGAÇÃO (stepper/abas): clicar em "Avançar"
/// precisa fazer o modelo reemitir updateComponents na MESMA surface. Sem esse
/// contrato o modelo respondia apenas em prosa ("Funcionou! ... veja ao vivo")
/// e a interface ficava presa no passo anterior (caso do stepper, 2026-10-08).
/// </summary>
public class AiChatA2uiNavigationPromptTests
{
    private static Agent BuildAgent() => new()
    {
        Id = Guid.NewGuid(),
        Hostname = "DESKTOP-TEST",
        OperatingSystem = "Windows 11",
        SiteId = Guid.NewGuid()
    };

    [Test]
    public void DefaultPrompt_ExplainsInPlaceSurfaceUpdateOnNavigation()
    {
        var prompt = AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(BuildAgent());

        Assert.That(prompt, Does.Contain("AÇÃO DE NAVEGAÇÃO"));
        Assert.That(prompt, Does.Contain("updateComponents"));
        Assert.That(prompt, Does.Contain("NUNCA um novo `createSurface`"));
    }

    [Test]
    public void DefaultPrompt_ForbidsClaimingUiChangeWithoutBlock()
    {
        var prompt = AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(BuildAgent());

        Assert.That(prompt, Does.Contain("veja ao vivo"));
        Assert.That(prompt, Does.Contain("SEM emitir o bloco `a2ui`"));
    }

    [Test]
    public void DefaultPrompt_ExplainsChoicePickerIsNotDropdown()
    {
        var prompt = AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(BuildAgent());

        Assert.That(prompt, Does.Contain("NÃO é dropdown"));
        Assert.That(prompt, Does.Contain("ChoicePicker"));
    }

    [Test]
    public void DefaultPrompt_ListsSelectAsTheDropdownComponent()
    {
        var prompt = AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(BuildAgent());

        Assert.That(prompt, Does.Contain("`Select`"));
        Assert.That(prompt, Does.Contain("é o dropdown de verdade"));
        Assert.That(prompt, Does.Not.Contain("o catálogo não tem esse componente"));
    }

    [Test]
    public void DefaultPrompt_RequiresDataBindingSoSelectionsReachTheAgent()
    {
        var prompt = AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(BuildAgent());

        Assert.That(prompt, Does.Contain("BINDING OBRIGATÓRIO EM FORMULÁRIOS"));
        Assert.That(prompt, Does.Contain("value"));
        Assert.That(prompt, Does.Contain("{\"path\":\"/printer\"}"));
        Assert.That(prompt, Does.Contain("escolha é PERDIDA"));
    }

    [Test]
    public void DefaultPrompt_ForbidsIconWithoutBundledFont()
    {
        var prompt = AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(BuildAgent());

        Assert.That(prompt, Does.Contain("NÃO USE"));
        Assert.That(prompt, Does.Contain("Material Symbols"));
    }
}
