using Discovery.Api.Services;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// A fila existe para que o provedor de push nunca bloqueie o request que
/// publicou a notificacao. O canal e limitado com descarte: enfileirar acima da
/// capacidade precisa continuar sendo instantaneo.
/// </summary>
[TestFixture]
public class WebPushDispatchQueueTests
{
    [Test]
    public async Task Enqueue_ShouldNotBlockWhenQueueIsFull()
    {
        var service = new WebPushDispatchBackgroundService(
            new FakeServiceProvider(),
            NullLogger<WebPushDispatchBackgroundService>.Instance);

        var startedAt = DateTime.UtcNow;

        for (var index = 0; index < 10_000; index++)
        {
            await service.EnqueueAsync(Guid.NewGuid(), new WebPushMessage("titulo", "corpo"));
        }

        Assert.That(
            DateTime.UtcNow - startedAt,
            Is.LessThan(TimeSpan.FromSeconds(5)),
            "Enfileirar nao pode bloquear quando a fila esta cheia (DropWrite).");
    }

    [Test]
    public async Task Enqueue_ShouldIgnoreAnonymousUser()
    {
        var service = new WebPushDispatchBackgroundService(
            new FakeServiceProvider(),
            NullLogger<WebPushDispatchBackgroundService>.Instance);

        Assert.DoesNotThrowAsync(async () =>
            await service.EnqueueAsync(Guid.Empty, new WebPushMessage("titulo", "corpo")));
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        // ExecuteAsync nunca e iniciado nestes testes: a fila e exercitada isolada.
        public object? GetService(Type serviceType) => null;
    }
}
