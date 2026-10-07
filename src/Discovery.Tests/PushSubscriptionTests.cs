using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Push;
using Discovery.Core.Cqrs.Push.Commands;
using Discovery.Core.Cqrs.Push.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Push;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

/// <summary>
/// Web Push (notificacoes do navegador): inscricoes, deep-link e resolucao do
/// par VAPID. O envio real depende do provedor de push e nao e exercitado aqui.
/// </summary>
[TestFixture]
public class PushSubscriptionTests
{
    private const string ValidEndpoint = "https://fcm.googleapis.com/fcm/send/abc123";
    private const string ValidP256dh = "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM";
    private const string ValidAuth = "tBHItJI5svbpez7KI4CCXg";

    private static IOptions<PushOptions> BuildOptions(int maxSubscriptions = 20) =>
        Microsoft.Extensions.Options.Options.Create(new PushOptions
        {
            MaxSubscriptionsPerUser = maxSubscriptions
        });

    [Test]
    public async Task Register_ShouldPersistAndReturnDto()
    {
        var repository = new FakePushSubscriptionRepository();
        var handler = new RegisterPushSubscriptionCommandHandler(repository, BuildOptions());
        var userId = Guid.NewGuid();

        var result = await handler.Handle(
            new RegisterPushSubscriptionCommand(userId, ValidEndpoint, ValidP256dh, ValidAuth, "test-agent"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(repository.Items, Has.Count.EqualTo(1));
        Assert.That(repository.Items[0].UserId, Is.EqualTo(userId));
        Assert.That(repository.Items[0].Endpoint, Is.EqualTo(ValidEndpoint));
        Assert.That(result.Value!.Endpoint, Is.EqualTo(ValidEndpoint));
    }

    [Test]
    public async Task Register_ShouldRejectNonHttpsEndpoint()
    {
        var handler = new RegisterPushSubscriptionCommandHandler(new FakePushSubscriptionRepository(), BuildOptions());

        var result = await handler.Handle(
            new RegisterPushSubscriptionCommand(Guid.NewGuid(), "http://insecure.example/push", ValidP256dh, ValidAuth),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    [Test]
    public async Task Register_ShouldRejectMissingKeys()
    {
        var handler = new RegisterPushSubscriptionCommandHandler(new FakePushSubscriptionRepository(), BuildOptions());

        var result = await handler.Handle(
            new RegisterPushSubscriptionCommand(Guid.NewGuid(), ValidEndpoint, "", ""),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    [Test]
    public async Task Register_ShouldReassignEndpointFromAnotherUser()
    {
        var repository = new FakePushSubscriptionRepository();
        var handler = new RegisterPushSubscriptionCommandHandler(repository, BuildOptions());
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await handler.Handle(new RegisterPushSubscriptionCommand(first, ValidEndpoint, ValidP256dh, ValidAuth), CancellationToken.None);
        await handler.Handle(new RegisterPushSubscriptionCommand(second, ValidEndpoint, ValidP256dh, ValidAuth), CancellationToken.None);

        // O navegador pertence a quem esta logado nele: reaponta, nao duplica.
        Assert.That(repository.Items, Has.Count.EqualTo(1));
        Assert.That(repository.Items[0].UserId, Is.EqualTo(second));
    }

    [Test]
    public async Task Register_ShouldTrimExcessSubscriptions()
    {
        var repository = new FakePushSubscriptionRepository();
        var handler = new RegisterPushSubscriptionCommandHandler(repository, BuildOptions(maxSubscriptions: 2));
        var userId = Guid.NewGuid();

        for (var index = 0; index < 4; index++)
        {
            await handler.Handle(
                new RegisterPushSubscriptionCommand(
                    userId,
                    "https://fcm.googleapis.com/fcm/send/" + index,
                    ValidP256dh,
                    ValidAuth),
                CancellationToken.None);
        }

        Assert.That(repository.Items, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Delete_ShouldBeIdempotent()
    {
        var repository = new FakePushSubscriptionRepository();
        var register = new RegisterPushSubscriptionCommandHandler(repository, BuildOptions());
        var delete = new DeletePushSubscriptionCommandHandler(repository);
        var userId = Guid.NewGuid();

        await register.Handle(new RegisterPushSubscriptionCommand(userId, ValidEndpoint, ValidP256dh, ValidAuth), CancellationToken.None);

        var first = await delete.Handle(new DeletePushSubscriptionCommand(userId, ValidEndpoint), CancellationToken.None);
        var second = await delete.Handle(new DeletePushSubscriptionCommand(userId, ValidEndpoint), CancellationToken.None);

        Assert.That(first.IsSuccess, Is.True);
        Assert.That(second.IsSuccess, Is.True);
        Assert.That(repository.Items, Is.Empty);
    }

    [Test]
    public async Task Delete_ShouldRejectEmptyEndpoint()
    {
        var handler = new DeletePushSubscriptionCommandHandler(new FakePushSubscriptionRepository());

        var result = await handler.Handle(new DeletePushSubscriptionCommand(Guid.NewGuid(), "  "), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    [Test]
    public async Task Status_ShouldReportDisabledWhenKeysAreAbsentOutsideDevelopment()
    {
        var sender = BuildSender(new FakePushSubscriptionRepository(), "Production", new PushOptions());
        var handler = new GetPushStatusQueryHandler(sender, new FakePushSubscriptionRepository());

        var result = await handler.Handle(new GetPushStatusQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Enabled, Is.False);
        Assert.That(result.Value.VapidPublicKey, Is.Empty);
    }

    [Test]
    public void Sender_ShouldGenerateUsableDevelopmentKeys()
    {
        var sender = BuildSender(new FakePushSubscriptionRepository(), "Development", new PushOptions());

        Assert.That(sender.IsConfigured, Is.True);
        Assert.That(sender.PublicKey, Is.Not.Empty);
        // Ponto nao comprimido P-256 (65 bytes) em base64url sem padding = 87 chars.
        Assert.That(sender.PublicKey, Has.Length.EqualTo(87));

        // VapidDetails decodifica/valida as chaves: garante que o par gerado e
        // aceito pela biblioteca que fala com os provedores de push.
        var generated = VapidKeyGenerator.Generate();
        Assert.That(generated.PrivateKey, Has.Length.EqualTo(43));
        Assert.DoesNotThrow(() => new WebPush.VapidDetails("mailto:teste@discovery.local", generated.PublicKey, generated.PrivateKey));
    }

    [Test]
    public async Task Sender_ShouldBeNoopWhenNotConfigured()
    {
        var sender = BuildSender(new FakePushSubscriptionRepository(), "Production", new PushOptions());

        var dispatch = await sender.SendToUserAsync(Guid.NewGuid(), new WebPushMessage("t", "b"));

        Assert.That(dispatch.Attempted, Is.Zero);
        Assert.That(dispatch.Delivered, Is.Zero);
    }

    [TestCase("{\"ticketId\":\"11111111-1111-1111-1111-111111111111\"}", "/tickets/11111111-1111-1111-1111-111111111111")]
    [TestCase("{\"ticket_id\":\"22222222-2222-2222-2222-222222222222\"}", "/tickets/22222222-2222-2222-2222-222222222222")]
    [TestCase("{\"agentId\":\"33333333-3333-3333-3333-333333333333\"}", "/agents/33333333-3333-3333-3333-333333333333")]
    [TestCase("{\"clientId\":\"44444444-4444-4444-4444-444444444444\"}", "/clients/44444444-4444-4444-4444-444444444444")]
    [TestCase("{}", null)]
    [TestCase("nao-e-json", null)]
    [TestCase(null, null)]
    public void DeepLink_ShouldMirrorTheConsoleNavigation(string? payloadJson, string? expected)
    {
        Assert.That(NotificationDeepLink.Build(payloadJson), Is.EqualTo(expected));
    }

    [Test]
    public void DeepLink_ShouldPreferTicketOverOtherTargets()
    {
        const string payload = "{\"ticketId\":\"11111111-1111-1111-1111-111111111111\",\"agentId\":\"33333333-3333-3333-3333-333333333333\"}";

        Assert.That(NotificationDeepLink.Build(payload), Is.EqualTo("/tickets/11111111-1111-1111-1111-111111111111"));
    }

    [Test]
    public void BuildPayload_ShouldStayUnderProviderLimit()
    {
        // AppNotification.Message aceita 2000 chars; o provedor recusa > ~4096 bytes.
        var payload = WebPushSender.BuildPayload(new WebPushMessage(
            new string('t', 400),
            new string('b', 2000),
            "Critical",
            "tickets",
            "ticket.assigned"));

        Assert.That(System.Text.Encoding.UTF8.GetByteCount(payload), Is.LessThan(4096));
        Assert.That(payload, Does.Contain("..."));
    }

    [Test]
    public void BuildPayload_ShouldKeepShortMessagesIntact()
    {
        var payload = WebPushSender.BuildPayload(new WebPushMessage(
            "Chamado atribuido",
            "Notebook nao liga."));

        Assert.That(payload, Does.Contain("Chamado atribuido"));
        Assert.That(payload, Does.Contain("Notebook nao liga."));
        Assert.That(payload, Does.Not.Contain("..."));
    }

    [Test]
    public void Truncate_ShouldNotSplitUtf8OrSurrogatePairs()
    {
        var text = new string('a', 499) + "\uD83D\uDE00\uD83D\uDE00\uD83D\uDE00";

        var truncated = WebPushSender.Truncate(text, 500, 510);

        Assert.That(System.Text.Encoding.UTF8.GetByteCount(truncated), Is.LessThanOrEqualTo(510));
        Assert.That(truncated, Does.Not.Contain("\uFFFD"), "surgiu caractere de substituicao (par substituto partido)");
        Assert.That(char.IsHighSurrogate(truncated[^1]), Is.False);
    }

    [Test]
    public void Truncate_ShouldKeepTextThatFits()
    {
        Assert.That(WebPushSender.Truncate("curto", 100, 100), Is.EqualTo("curto"));
        Assert.That(WebPushSender.Truncate(null, 100, 100), Is.Empty);
        Assert.That(WebPushSender.Truncate("   ", 100, 100), Is.Empty);
    }

    [Test]
    public async Task Register_ShouldRejectOversizedEndpoint()
    {
        var handler = new RegisterPushSubscriptionCommandHandler(new FakePushSubscriptionRepository(), BuildOptions());
        var endpoint = "https://fcm.googleapis.com/fcm/send/" + new string('x', 1200);

        var result = await handler.Handle(
            new RegisterPushSubscriptionCommand(Guid.NewGuid(), endpoint, ValidP256dh, ValidAuth),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    private static WebPushSender BuildSender(
        IPushSubscriptionRepository repository,
        string environmentName,
        PushOptions options)
    {
        return new WebPushSender(
            repository,
            new FakeHttpClientFactory(),
            Microsoft.Extensions.Options.Options.Create(options),
            new FakeHostEnvironment(environmentName),
            NullLogger<WebPushSender>.Instance);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(5) };
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Discovery.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakePushSubscriptionRepository : IPushSubscriptionRepository
    {
        public List<PushSubscription> Items { get; } = [];

        public Task<IReadOnlyList<PushSubscription>> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PushSubscription>>(
                Items.Where(item => item.UserId == userId).ToList());

        public Task<PushSubscription> UpsertAsync(PushSubscription subscription, CancellationToken ct = default)
        {
            var existing = Items.FirstOrDefault(item => item.Endpoint == subscription.Endpoint);
            if (existing is null)
            {
                Items.Add(subscription);
                return Task.FromResult(subscription);
            }

            existing.UserId = subscription.UserId;
            existing.P256dh = subscription.P256dh;
            existing.Auth = subscription.Auth;
            existing.UserAgent = subscription.UserAgent;
            existing.LastSeenAt = subscription.LastSeenAt;
            return Task.FromResult(existing);
        }

        public Task<bool> DeleteByEndpointAsync(Guid userId, string endpoint, CancellationToken ct = default)
        {
            var removed = Items.RemoveAll(item => item.UserId == userId && item.Endpoint == endpoint);
            return Task.FromResult(removed > 0);
        }

        public Task<int> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
            => Task.FromResult(Items.RemoveAll(item => ids.Contains(item.Id)));

        public Task<int> TrimAsync(Guid userId, int maxSubscriptions, CancellationToken ct = default)
        {
            if (maxSubscriptions <= 0)
                return Task.FromResult(0);

            var stale = Items
                .Where(item => item.UserId == userId)
                .OrderByDescending(item => item.LastSeenAt)
                .Skip(maxSubscriptions)
                .Select(item => item.Id)
                .ToList();

            return Task.FromResult(Items.RemoveAll(item => stale.Contains(item.Id)));
        }

        public Task<int> CountByUserIdAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(Items.Count(item => item.UserId == userId));
    }
}
