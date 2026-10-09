using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Nota de reemissão do M2 (interface criada sem definição). Precisa citar os
/// surfaceIds: no reparo o assistant anterior volta ao contexto, mas o id
/// explícito impede que o modelo invente outra surface.
/// </summary>
public class AiChatA2uiRepairNoteTests
{
    [Test]
    public void BuildNote_WithoutSurfaces_ReturnsBaseInstruction()
    {
        Assert.That(AiChatHelpers.BuildA2uiIncompleteDefinitionNote(Array.Empty<string>()),
            Is.EqualTo(AiChatHelpers.A2uiIncompleteDefinitionNote));
    }

    [Test]
    public void BuildNote_WithSurfaces_ListsEverySurfaceId()
    {
        var note = AiChatHelpers.BuildA2uiIncompleteDefinitionNote(new[] { "paper_jam_wizard6", "printer_steps" });

        Assert.That(note, Does.Contain("paper_jam_wizard6"));
        Assert.That(note, Does.Contain("printer_steps"));
        Assert.That(note, Does.Contain("updateComponents"));
    }
}
