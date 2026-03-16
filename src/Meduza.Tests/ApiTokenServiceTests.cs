using Meduza.Core.Entities.Security;
using Meduza.Core.Interfaces.Security;
using Meduza.Infrastructure.Services;

namespace Meduza.Tests;

public class ApiTokenServiceTests
{
    [Test]
    public async Task CreateTokenAsync_ShouldApplyDefaultExpirationOfOneYear_WhenExpiresAtIsNull()
    {
        var repo = new InMemoryApiTokenRepository();
        var service = new ApiTokenService(repo);
        var userId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        var result = await service.CreateTokenAsync(userId, "Integracao ERP", null);

        Assert.That(result.ExpiresAt, Is.Not.Null);

        var expected = before.AddYears(1);
        var diff = (result.ExpiresAt!.Value - expected).Duration();
        Assert.That(diff, Is.LessThan(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task CreateTokenAsync_ShouldRespectCustomExpiration_WhenProvided()
    {
        var repo = new InMemoryApiTokenRepository();
        var service = new ApiTokenService(repo);
        var userId = Guid.NewGuid();
        var customExpiresAt = DateTime.UtcNow.AddDays(30);

        var result = await service.CreateTokenAsync(userId, "Webhook", customExpiresAt);

        Assert.That(result.ExpiresAt, Is.EqualTo(customExpiresAt).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public async Task AuthenticateAsync_ShouldReturnUserId_WhenTokenIsValid()
    {
        var repo = new InMemoryApiTokenRepository();
        var service = new ApiTokenService(repo);
        var userId = Guid.NewGuid();

        var created = await service.CreateTokenAsync(userId, "CI", null);
        var rawCredential = $"{created.TokenIdPublic}.{created.AccessKey}";

        var authenticatedUserId = await service.AuthenticateAsync(rawCredential);

        Assert.That(authenticatedUserId, Is.EqualTo(userId));
    }

    [Test]
    public async Task AuthenticateAsync_ShouldReturnNull_WhenCredentialIsMalformed()
    {
        var repo = new InMemoryApiTokenRepository();
        var service = new ApiTokenService(repo);

        var authenticatedUserId = await service.AuthenticateAsync("invalid-format");

        Assert.That(authenticatedUserId, Is.Null);
    }

    private sealed class InMemoryApiTokenRepository : IApiTokenRepository
    {
        private readonly List<ApiToken> _tokens = new();

        public Task<ApiToken?> GetByIdAsync(Guid id)
            => Task.FromResult(_tokens.SingleOrDefault(t => t.Id == id));

        public Task<ApiToken?> GetByTokenIdPublicAsync(string tokenIdPublic)
            => Task.FromResult(_tokens.SingleOrDefault(t => t.TokenIdPublic == tokenIdPublic));

        public Task<IEnumerable<ApiToken>> GetByUserIdAsync(Guid userId)
            => Task.FromResult<IEnumerable<ApiToken>>(_tokens.Where(t => t.UserId == userId).ToList());

        public Task<ApiToken> CreateAsync(ApiToken token)
        {
            _tokens.Add(token);
            return Task.FromResult(token);
        }

        public Task<bool> RevokeAsync(Guid id, Guid userId)
        {
            var token = _tokens.SingleOrDefault(t => t.Id == id && t.UserId == userId && t.IsActive);
            if (token is null)
            {
                return Task.FromResult(false);
            }

            token.IsActive = false;
            return Task.FromResult(true);
        }

        public Task UpdateLastUsedAsync(Guid id)
        {
            var token = _tokens.SingleOrDefault(t => t.Id == id);
            if (token is not null)
            {
                token.LastUsedAt = DateTime.UtcNow;
            }

            return Task.CompletedTask;
        }
    }
}
