using Discovery.Core.Departments;
using Discovery.Core.Entities;

namespace Discovery.Tests;

/// <summary>
/// Validador único da herança de departamento (D1–D5): auto-herança, ciclo, pai
/// inexistente/inativo, cross-tenant e "enviar null" para desfazer.
/// </summary>
public class DepartmentInheritanceValidatorTests
{
    private static Department Dept(
        Guid id, Guid? clientId = null, bool active = true, Guid? parentId = null)
        => new()
        {
            Id = id,
            ClientId = clientId,
            Name = "Depto",
            IsActive = active,
            InheritFromGlobalId = parentId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static Func<Guid, CancellationToken, Task<Department?>> Lookup(
        params Department[] departments)
    {
        var map = departments.ToDictionary(d => d.Id);
        return (id, _) => Task.FromResult(map.TryGetValue(id, out var dept) ? dept : null);
    }

    [Test]
    public async Task Null_caminho_desfaz_a_heranca()
    {
        var result = await DepartmentInheritanceValidator.ValidateAsync(
            Guid.NewGuid(), null, Lookup(), CancellationToken.None);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Rejeita_auto_heranca()
    {
        var id = Guid.NewGuid();
        var result = await DepartmentInheritanceValidator.ValidateAsync(
            id, id, Lookup(Dept(id, clientId: Guid.NewGuid())), CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Code, Is.EqualTo("Validation"));
        Assert.That(result.Field, Is.EqualTo("InheritFromGlobalId"));
        Assert.That(result.Message, Does.Contain("si mesmo"));
    }

    [Test]
    public async Task Rejeita_pai_inexistente()
    {
        var result = await DepartmentInheritanceValidator.ValidateAsync(
            Guid.NewGuid(), Guid.NewGuid(), Lookup(), CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Message, Does.Contain("não encontrado"));
    }

    [Test]
    public async Task Rejeita_pai_inativo()
    {
        var parentId = Guid.NewGuid();
        var result = await DepartmentInheritanceValidator.ValidateAsync(
            Guid.NewGuid(), parentId, Lookup(Dept(parentId, active: false)), CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Message, Does.Contain("inativo"));
    }

    [Test]
    public async Task Rejeita_pai_de_outro_cliente_cross_tenant()
    {
        var parentId = Guid.NewGuid();
        var result = await DepartmentInheritanceValidator.ValidateAsync(
            Guid.NewGuid(), parentId,
            Lookup(Dept(parentId, clientId: Guid.NewGuid())), CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Message, Does.Contain("globais"));
    }

    [Test]
    public async Task Rejeita_ciclo_com_cap_de_caminhada()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        // A (cliente) -> B; B -> C; C -> A  => ciclo legado A->B->C->A.
        var departments = new[]
        {
            Dept(a, clientId: Guid.NewGuid(), parentId: b),
            Dept(b, parentId: c),
            Dept(c, parentId: a)
        };

        var result = await DepartmentInheritanceValidator.ValidateAsync(
            a, b, Lookup(departments), CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Message, Does.Contain("Ciclo"));
    }

    [Test]
    public async Task Aceita_pai_global_ativo()
    {
        var parentId = Guid.NewGuid();
        var result = await DepartmentInheritanceValidator.ValidateAsync(
            Guid.NewGuid(), parentId, Lookup(Dept(parentId)), CancellationToken.None);

        Assert.That(result, Is.Null);
    }
}
