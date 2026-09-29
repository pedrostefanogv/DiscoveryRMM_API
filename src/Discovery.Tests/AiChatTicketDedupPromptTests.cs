using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Anti-duplicidade de chamados no chat IA: o prompt e as descrições das
/// ferramentas devem instruir a IA a consultar os chamados existentes
/// (list_tickets) antes de abrir um novo chamado (create_ticket).
/// </summary>
public class AiChatTicketDedupPromptTests
{
    private static Agent BuildAgent() => new()
    {
        Id = Guid.NewGuid(),
        Hostname = "DESKTOP-TEST",
        OperatingSystem = "Windows 11",
        SiteId = Guid.NewGuid()
    };

    [Test]
    public void DefaultPrompt_RequiresDuplicateCheckBeforeOpeningTicket()
    {
        var prompt = AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(BuildAgent());

        Assert.That(prompt, Does.Contain("DEDUPLICAÇÃO DE CHAMADOS"));
        Assert.That(prompt, Does.Contain("list_tickets"));
        Assert.That(prompt, Does.Contain("ClosedAt"));
        Assert.That(prompt, Does.Contain("NÃO chame `create_ticket`"));
    }

    [Test]
    public void DefaultPrompt_RequiresDuplicateCheckOnTemplateFlow()
    {
        var prompt = AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(BuildAgent());

        // A abertura por template (A2UI) não pode pular a verificação de duplicidade.
        Assert.That(prompt, Does.Contain("create_ticket_from_template"));
        Assert.That(prompt, Does.Contain("cumpra a verificação de duplicidade"));
    }

    [Test]
    public void EnsureTicketDedupSection_AppendsToCustomTemplatePrompt()
    {
        var custom = "Você é um assistente de suporte. {{AGENT_TOOLS_SECTION}}";

        var result = AiChatSystemPromptBuilder.EnsureTicketDedupSection(custom);

        Assert.That(result, Does.Contain("DEDUPLICAÇÃO DE CHAMADOS"));
        Assert.That(result, Does.Contain("list_tickets"));
    }

    [Test]
    public void EnsureTicketDedupSection_DoesNotDuplicateWhenAlreadyPresent()
    {
        var prompt = AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(BuildAgent());

        var result = AiChatSystemPromptBuilder.EnsureTicketDedupSection(prompt);

        Assert.That(result, Is.EqualTo(prompt));
    }

    [Test]
    public void EnsureTicketDedupSection_KeepsNullAndEmptyPrompts()
    {
        Assert.That(AiChatSystemPromptBuilder.EnsureTicketDedupSection(""), Is.EqualTo(""));
        Assert.That(AiChatSystemPromptBuilder.EnsureTicketDedupSection("   "), Is.EqualTo("   "));
    }

    [Test]
    public void CreateTicketDescription_RequiresDuplicateCheck()
    {
        var description = AiChatToolOrchestrator.EnrichAgentToolDescription("create_ticket", "descricao original");

        Assert.That(description, Does.Contain("list_tickets"));
        Assert.That(description, Does.Contain("duplic").IgnoreCase);
        Assert.That(description, Does.Contain("ClosedAt"));
    }

    [Test]
    public void ListTicketsDescription_MentionsOpenTicketsAndClosedAt()
    {
        var description = AiChatToolOrchestrator.EnrichAgentToolDescription("list_tickets", "descricao original");

        Assert.That(description, Does.Contain("ClosedAt"));
        Assert.That(description, Does.Contain("add_ticket_comment"));
    }

    [Test]
    public void FormatAgentToolsDescription_IncludesAntiDuplicationGuidance()
    {
        var tools = new List<LlmTool>
        {
            new("list_tickets", "Lista os chamados.", new { type = "object" }),
            new("create_ticket", "Abre um chamado.", new { type = "object" })
        };

        var text = AiChatToolOrchestrator.FormatAgentToolsDescription(tools);

        Assert.That(text, Does.Contain("list_tickets"));
        Assert.That(text, Does.Contain("ClosedAt"));
        Assert.That(text, Does.Contain("NÃO abra duplicata"));
    }
}
