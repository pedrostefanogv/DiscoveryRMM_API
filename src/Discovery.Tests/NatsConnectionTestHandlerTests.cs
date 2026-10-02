using Discovery.Core.Cqrs.Configurations.Commands;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Configurations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// O teste de NATS da tela envia apenas a URL (credenciais ficam no servidor).
/// O handler precisa aceitar isso e usar as credenciais configuradas — senão
/// (a) o model binding devolvia 400 e (b) uma conexão anônima falharia no NATS
/// com auth callout.
/// </summary>
public class NatsConnectionTestHandlerTests
{
    private static IConfiguration Config(string? user, string? password)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nats:AuthUser"] = user,
                ["Nats:AuthPassword"] = password,
            })
            .Build();

    [Test]
    public async Task UsesConfiguredCredentials_WhenRequestOmitsThem()
    {
        var validator = new CapturingValidator();
        var handler = new TestNatsConnectionCommandHandler(validator, Config("svc-user", "svc-pass"));

        await handler.Handle(new TestNatsConnectionCommand("nats://127.0.0.1:4222", null, null), CancellationToken.None);

        Assert.That(validator.User, Is.EqualTo("svc-user"));
        Assert.That(validator.Password, Is.EqualTo("svc-pass"));
    }

    [Test]
    public async Task KeepsExplicitCredentials_WhenProvided()
    {
        var validator = new CapturingValidator();
        var handler = new TestNatsConnectionCommandHandler(validator, Config("svc-user", "svc-pass"));

        await handler.Handle(new TestNatsConnectionCommand("nats://host:4222", "other", "secret"), CancellationToken.None);

        Assert.That(validator.User, Is.EqualTo("other"));
        Assert.That(validator.Password, Is.EqualTo("secret"));
    }

    [Test]
    public async Task EmptyUrl_IsReportedAsValidationError_NotHttp400()
    {
        var validator = new CapturingValidator();
        var handler = new TestNatsConnectionCommandHandler(validator, Config(null, null));

        var result = await handler.Handle(new TestNatsConnectionCommand(string.Empty, null, null), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Ok, Is.False);
    }

    private sealed class CapturingValidator : INatsConnectionValidator
    {
        public string? User { get; private set; }
        public string? Password { get; private set; }

        public Task<(bool IsValid, string[] Errors)> ValidateConnectionAsync(
            string hostOrUrl, string? user, string? password, CancellationToken cancellationToken)
        {
            User = user;
            Password = password;
            return Task.FromResult((!string.IsNullOrWhiteSpace(hostOrUrl), Array.Empty<string>()));
        }
    }
}
