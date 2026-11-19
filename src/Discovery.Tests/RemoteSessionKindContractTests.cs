using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;

namespace Discovery.Tests;

/// <summary>
/// Contrato dos valores numéricos de <see cref="RemoteSessionKind"/> e
/// <see cref="RemoteSessionRights"/>.
///
/// Estes valores atravessam três linguagens (C# na API, Go no agente, TypeScript
/// no console) e são PERSISTIDOS em <c>remote_sessions.kind</c>. Renumerar um
/// membro existente reinterpretaria sessões gravadas e faria o agente abrir o
/// tipo errado de stream — uma falha silenciosa e difícil de diagnosticar.
///
/// Estes testes travam os valores: qualquer renumeração quebra o build de CI e
/// obriga a decisão a ser consciente.
/// </summary>
public sealed class RemoteSessionKindContractTests
{
    [Test]
    public void RemoteSessionKind_ValoresNumericosSaoEstaveis()
    {
        Assert.Multiple(() =>
        {
            Assert.That((int)RemoteSessionKind.Screen, Is.EqualTo(0));
            Assert.That((int)RemoteSessionKind.Terminal, Is.EqualTo(1));
            Assert.That((int)RemoteSessionKind.Files, Is.EqualTo(2));
            Assert.That((int)RemoteSessionKind.Proxy, Is.EqualTo(3));
            Assert.That((int)RemoteSessionKind.All, Is.EqualTo(4));
            Assert.That((int)RemoteSessionKind.Processes, Is.EqualTo(5));
            Assert.That((int)RemoteSessionKind.Rdp, Is.EqualTo(6));
            // ADITIVO: novo kind sempre no fim, nunca no meio.
            Assert.That((int)RemoteSessionKind.Registry, Is.EqualTo(7));
        });
    }

    [Test]
    public void RemoteSessionKind_RegistrySerializaComoRegistry()
    {
        // O agente recebe o kind em minúsculas no payload
        // (kind = cmd.Kind.ToString().ToLowerInvariant()) e o compara com a
        // constante KindRegistry = "registry" em Go. Se a grafia mudar aqui, o
        // agente recusa o start com "kind não suportado".
        Assert.That(RemoteSessionKind.Registry.ToString().ToLowerInvariant(), Is.EqualTo("registry"));
    }

    [Test]
    public void RemoteSessionRights_ValoresNumericosSaoEstaveis()
    {
        Assert.Multiple(() =>
        {
            Assert.That((int)RemoteSessionRights.None, Is.EqualTo(0));
            Assert.That((int)RemoteSessionRights.ViewOnly, Is.EqualTo(1));
            Assert.That((int)RemoteSessionRights.NoTerminal, Is.EqualTo(2));
            Assert.That((int)RemoteSessionRights.NoFiles, Is.EqualTo(4));
            Assert.That((int)RemoteSessionRights.LimitedInput, Is.EqualTo(8));
            Assert.That((int)RemoteSessionRights.NoDesktop, Is.EqualTo(16));
            Assert.That((int)RemoteSessionRights.NoEvents, Is.EqualTo(32));
            Assert.That((int)RemoteSessionRights.NoProxy, Is.EqualTo(64));
            Assert.That((int)RemoteSessionRights.NoRdp, Is.EqualTo(128));
            // ADITIVO: bit seguinte livre.
            Assert.That((int)RemoteSessionRights.NoRegistry, Is.EqualTo(256));
        });
    }

    [Test]
    public void RemoteSessionRights_NoRegistryNaoColideComOutrosBits()
    {
        // O bitmask é interpretado pelo agente Go (RightsNoRegistry = 256) e é
        // persistido em remote_sessions.rights. Uma colisão faria uma restrição
        // ativar outra em silêncio.
        Assert.That((int)RemoteSessionRights.NoRegistry & (int)RemoteSessionRights.NoRdp, Is.EqualTo(0));
        Assert.That((int)RemoteSessionRights.NoRegistry & (int)RemoteSessionRights.NoProxy, Is.EqualTo(0));
        Assert.That((int)RemoteSessionRights.NoRegistry & (int)RemoteSessionRights.ViewOnly, Is.EqualTo(0));
        Assert.That((int)RemoteSessionRights.NoRegistry & (int)RemoteSessionRights.NoFiles, Is.EqualTo(0));
    }

    [Test]
    public void RemoteConsentPolicy_NaoSeAplicaAoRegistro()
    {
        // DECISÃO DE PRODUTO (confirmada com o usuário): escrita no registro
        // exige confirmação explícita na UI do console + auditoria, SEM prompt no
        // endpoint. Um editor de registro faz dezenas de operações por sessão e
        // o operador não está na frente da máquina. Este teste documenta a
        // decisão para que uma mudança futura seja consciente.
        Assert.That(RemoteConsentPolicy.AppliesTo(RemoteSessionKind.Registry), Is.False);
    }
}
