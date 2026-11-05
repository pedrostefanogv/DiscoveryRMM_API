using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Tests;

/// <summary>
/// Cobre o VÍNCULO POR SENSOR introduzido na Fase 3: quais sensores podem ou
/// não abrir chamado, a duração mínima própria e o impacto como piso.
///
/// A precedência é o que garante o comportamento pedido:
///   Never do sensor  >  NeverTicketSensorKeys (política)  >  AllowedSensorKeys
/// O mais específico vence.
/// </summary>
[TestFixture]
public class AutoTicketSensorPolicyTests
{
    [Test]
    public void SensorPolicy_ShouldDefaultToOnEscalation_PreservingPreviousBehaviour()
    {
        var sensor = new SensorDefinition();

        Assert.Multiple(() =>
        {
            Assert.That(sensor.AutoTicketPolicy, Is.EqualTo(SensorAutoTicketPolicy.OnEscalation),
                "Default preserva o comportamento anterior (só abre na abertura/escala).");
            Assert.That(sensor.AutoTicketMinDurationSeconds, Is.EqualTo(0),
                "0 = herda a duração da política hierárquica.");
            Assert.That(sensor.AutoTicketImpact, Is.EqualTo(AutoTicketImpact.Medium));
        });
    }

    [TestCase(SensorAutoTicketPolicy.Never)]
    [TestCase(SensorAutoTicketPolicy.OnEscalation)]
    [TestCase(SensorAutoTicketPolicy.Always)]
    public void SensorPolicy_ShouldRoundTripThroughEnum(SensorAutoTicketPolicy policy)
    {
        var sensor = new SensorDefinition { AutoTicketPolicy = policy };
        Assert.That(sensor.AutoTicketPolicy, Is.EqualTo(policy));
    }

    [TestCase(AutoTicketImpact.Low)]
    [TestCase(AutoTicketImpact.Medium)]
    [TestCase(AutoTicketImpact.High)]
    [TestCase(AutoTicketImpact.Critical)]
    public void SensorImpact_ShouldRoundTripThroughEnum(AutoTicketImpact impact)
    {
        var sensor = new SensorDefinition { AutoTicketImpact = impact };
        Assert.That(sensor.AutoTicketImpact, Is.EqualTo(impact));
    }

    [Test]
    public void SensorImpact_ShouldBeOrderable_SoItCanActAsAPriorityFloor()
    {
        // O impacto do sensor funciona como PISO da prioridade sugerida pela IA:
        // a IA nunca rebaixa abaixo dele. Por isso a ordenação importa.
        Assert.Multiple(() =>
        {
            Assert.That(AutoTicketImpact.Low, Is.LessThan(AutoTicketImpact.Medium));
            Assert.That(AutoTicketImpact.Medium, Is.LessThan(AutoTicketImpact.High));
            Assert.That(AutoTicketImpact.High, Is.LessThan(AutoTicketImpact.Critical));
        });
    }

    [Test]
    public void SensorPolicy_Never_ShouldBeTheLowestValue_SoItIsTheExplicitOptOut()
    {
        Assert.That((int)SensorAutoTicketPolicy.Never, Is.EqualTo(0));
    }

    [Test]
    public void SensorPolicy_OnEscalation_ShouldBeTheDefaultValueWrittenByTheMigration()
    {
        // A migration M268 grava 1 como default. Se o enum mudar, o default do
        // banco passa a apontar para outra política silenciosamente.
        Assert.That((int)SensorAutoTicketPolicy.OnEscalation, Is.EqualTo(1));
    }

    [Test]
    public void SensorImpact_Medium_ShouldBeTheDefaultValueWrittenByTheMigration()
    {
        Assert.That((int)AutoTicketImpact.Medium, Is.EqualTo(1));
    }
}
