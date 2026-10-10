using System;
using System.Collections.Generic;
using System.Text.Json;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// O cache do mapa "usuário logado ao vivo" (SearchService) serializa
/// Dictionary&lt;Guid,string&gt; em Redis e o relê. Se o round-trip não for
/// suportado, a desserialização lançaria e o SearchService cairia sempre na
/// varredura completa do keyspace de heartbeat (custo por busca).
/// </summary>
[TestFixture]
public class LiveLoggedUserCacheSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void DictionaryWithGuidKeys_RoundTrips()
    {
        var original = new Dictionary<Guid, string>
        {
            [Guid.Parse("019faa6f-8eaa-7c38-9878-36cc96f9be3e")] = "pedro",
            [Guid.Parse("019fc514-155f-7394-a79a-ff5f9144b6b2")] = @"CORP\maria",
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var restored = JsonSerializer.Deserialize<Dictionary<Guid, string>>(json, JsonOptions);

        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.Count, Is.EqualTo(original.Count));
        foreach (var (id, user) in original)
            Assert.That(restored[id], Is.EqualTo(user), $"usuário de {id} deveria sobreviver ao cache");
    }

    [Test]
    public void EmptyMap_RoundTrips()
    {
        var json = JsonSerializer.Serialize(new Dictionary<Guid, string>(), JsonOptions);
        var restored = JsonSerializer.Deserialize<Dictionary<Guid, string>>(json, JsonOptions);
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!, Is.Empty);
    }
}
