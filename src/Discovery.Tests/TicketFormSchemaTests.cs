using Discovery.Api.DependencyInjection;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Tests;

/// <summary>
/// O formulário de abertura de chamado do agent é montado pelo servidor
/// (departamento -> schema). Estes testes fixam o contrato: opções de categoria
/// variam por departamento (modelos + histórico), sem duplicatas, e o campo é
/// devolvido invisível quando o servidor não conhece nenhuma categoria.
/// </summary>
public class TicketFormSchemaTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"ticket-form-schema-{Guid.NewGuid():N}")
            .Options;
        return new FormSchemaTestDbContext(options);
    }

    private static IConfiguration BuildConfig(params string[] categoryOptions)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < categoryOptions.Length; i++)
            values[$"Tickets:CategoryOptions:{i}"] = categoryOptions[i];
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static TicketTemplate NewTemplate(Guid? departmentId, string? category, bool isActive = true, Guid? clientId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            DepartmentId = departmentId,
            Name = $"template-{Guid.NewGuid():N}",
            // Título determinístico para fixar a ordem das opções no teste.
            Title = category ?? "Modelo",
            Description = "Descrição",
            Priority = TicketPriority.Medium,
            Category = category,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static Ticket NewTicket(Guid clientId, Guid departmentId, string? category, DateTime createdAt)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            DepartmentId = departmentId,
            Title = "Chamado",
            Description = "Descrição",
            Category = category,
            Priority = TicketPriority.Medium,
            WorkflowStateId = Guid.NewGuid(),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

    [Test]
    public async Task DepartmentSchema_MergesTemplatesUsedCategoriesAndServerCatalog()
    {
        var clientId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.AddRange(
            NewTemplate(departmentId, "Rede"),
            // Modelo sem departamento vale para qualquer departamento: a
            // categoria dele não pode sumir da lista.
            NewTemplate(null, "Global"),
            NewTemplate(departmentId, "Ignorada", isActive: false),
            NewTemplate(departmentId, "DeOutroCliente", clientId: Guid.NewGuid()),
            NewTicket(clientId, departmentId, "Rede", now.AddMinutes(-5)),
            NewTicket(clientId, departmentId, "Impressora", now),
            NewTicket(clientId, departmentId, "   ", now.AddMinutes(-10)),
            NewTicket(clientId, Guid.NewGuid(), "DeOutroDepartamento", now));
        await db.SaveChangesAsync();

        var service = new TicketFormSchemaService(db, BuildConfig("Hardware", "Outro"));
        var schema = await service.GetForDepartmentAsync(departmentId, clientId);

        var category = schema.Fields.Single(f => f.Key == "category");
        Assert.That(category.Visible, Is.True);
        Assert.That(
            category.Options.Select(o => o.Value),
            Is.EqualTo(new[] { "Global", "Rede", "Impressora", "Hardware", "Outro" }));
        Assert.That(schema.DepartmentId, Is.EqualTo(departmentId.ToString()));
    }

    [Test]
    public async Task DepartmentSchema_HidesCategoryField_WhenServerHasNoCategory()
    {
        var clientId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        await using var db = CreateDb();

        var service = new TicketFormSchemaService(db, BuildConfig());
        var schema = await service.GetForDepartmentAsync(departmentId, clientId);

        var category = schema.Fields.Single(f => f.Key == "category");
        Assert.That(category.Visible, Is.False);
        Assert.That(category.Options, Is.Empty);
    }

    [Test]
    public async Task DepartmentSchema_AlwaysShipsPriorityFromServerWithDefault()
    {
        await using var db = CreateDb();
        var service = new TicketFormSchemaService(db, BuildConfig());

        var schema = await service.GetForDepartmentAsync(Guid.NewGuid(), null);
        var priority = schema.Fields.Single(f => f.Key == "priority");

        Assert.That(priority.Visible, Is.True);
        Assert.That(priority.DefaultValue, Is.EqualTo("2"));
        Assert.That(priority.Options.Select(o => o.Value), Is.EqualTo(new[] { "1", "2", "3", "4" }));
    }

    /// <summary>
    /// O catálogo base do servidor aceita item com rótulo próprio. O rótulo é
    /// apresentação: o valor gravado no chamado continua o mesmo (não fragmenta
    /// histórico/relatórios por causa de acento ou texto mais amigável).
    /// </summary>
    [Test]
    public async Task ServerCatalog_AcceptsLabelWithoutChangingStoredValue()
    {
        var clientId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        await using var db = CreateDb();
        db.Add(NewTicket(clientId, departmentId, "Acesso", DateTime.UtcNow));
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tickets:CategoryOptions:0:value"] = "Acesso",
            ["Tickets:CategoryOptions:0:label"] = "Acesso / Permissões",
            ["Tickets:CategoryOptions:1"] = "Hardware",
        }).Build();

        var service = new TicketFormSchemaService(db, config);
        var schema = await service.GetForDepartmentAsync(departmentId, clientId);
        var category = schema.Fields.Single(f => f.Key == "category");

        Assert.That(category.Options.Select(o => o.Value), Is.EqualTo(new[] { "Acesso", "Hardware" }));
        Assert.That(category.Options[0].Label, Is.EqualTo("Acesso / Permissões"));
        Assert.That(category.Options[1].Label, Is.EqualTo("Hardware"));
    }

    /// <summary>
    /// O endpoint do agent depende de ITicketFormSchemaService: se o
    /// auto-registro deixar de enxergá-lo, o host só falha em runtime.
    /// </summary>
    [Test]
    public void TicketFormSchemaService_is_auto_registered()
    {
        var services = new ServiceCollection();

        var registrations = services.AddDiscoveryAutoRegisteredServices();

        Assert.That(
            registrations.Any(r => r.InterfaceType == typeof(ITicketFormSchemaService)
                                   && r.ImplementationType == typeof(TicketFormSchemaService)),
            Is.True);
    }

    private sealed class FormSchemaTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client), typeof(Ticket), typeof(TicketTemplate)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => { e.HasKey(c => c.Id); e.Property(c => c.Name).IsRequired(); });
            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
            modelBuilder.Entity<TicketTemplate>(e => e.HasKey(t => t.Id));
        }
    }
}
