using System;
using System.Collections.Generic;
using System.Linq;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// Contrato wire dos tipos de comando: o valor enviado ao agent precisa ser
/// estável (é o que o router do agent compara). O descomissionamento remoto
/// usa "decommissionagent" e é um comando "special" (payload validado).
/// </summary>
[TestFixture]
public class CommandTypeWireMapperTests
{
    [Test]
    public void DecommissionAgent_MapsToWireValue()
    {
        Assert.That(
            CommandTypeWireMapper.ToWireValue(CommandType.DecommissionAgent),
            Is.EqualTo("decommissionagent"));
    }

    [Test]
    public void DecommissionAgent_IsSpecialCommand()
    {
        Assert.That(CommandTypeWireMapper.IsSpecialCommand(CommandType.DecommissionAgent), Is.True);
    }

    [Test]
    public void WireValues_AreUnique()
    {
        var values = Enum.GetValues<CommandType>()
            .Select(CommandTypeWireMapper.ToWireValue)
            .ToList();

        Assert.That(values, Is.Unique);
    }
}
