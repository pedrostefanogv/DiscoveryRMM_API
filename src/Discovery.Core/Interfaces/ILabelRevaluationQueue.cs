namespace Discovery.Core.Interfaces;

/// <summary>
/// Agenda a reavaliacao de labels de um agente logo apos o sync de inventario.
///
/// Complementa a reconciliacao periodica (incremental): sem este gatilho, um agente
/// recem-inventariado espera ate o proximo ciclo para ganhar/perder labels. As
/// chamadas sao coalescidas por um intervalo curto (debounce), o que evita avaliar
/// varias vezes durante o mesmo sync e evita avalanches quando a frota sincroniza
/// em massa. Desabilitado por padrao (ver AgentLabeling:TriggerOnInventorySync).
/// </summary>
public interface ILabelRevaluationQueue
{
    /// <summary>Agenda a reavaliacao do agente. No-op quando o recurso esta desabilitado.</summary>
    void Schedule(Guid agentId);
}
