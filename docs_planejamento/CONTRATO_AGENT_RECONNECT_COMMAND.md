# Contrato — Comando `nats.reconnect` (Fase 3: Auto-recuperação do Agent)

**Data:** 2026-09-02
**Relacionado:** `AGENT_TRANSFER_SYNC_FIX_PLAN.md` (Fases 2 e 3)
**Status:** Backend implementado; aguardando implementação no agent

---

## 1. Quando o servidor envia

Após uma transferência de site (`AgentTransferService.TransferAsync`), o servidor publica no **subject antigo** do agent (o único que ele ainda consegue receber, pois o JWT NATS atual tem ACL do site antigo):

```
tenant.{clientId-antigo}.site.{siteId-antigo}.agent.{agentId}.command
```

## 2. Payload do comando

```json
{
  "CommandId": "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
  "CommandType": "nats.reconnect",
  "Payload": {
    "version": 1,
    "reason": "agent-transferred",
    "newSiteId": "c56ab8e2-....",
    "newClientId": "9d3f21aa-....",
    "revision": "transfer:2026-09-02T12:00:00.0000000Z"
  }
}
```

| Campo         | Tipo   | Descrição                                                                                 |
| ------------- | ------ | ----------------------------------------------------------------------------------------- |
| `version`     | int    | Versão do contrato (atualmente `1`). Agent deve ignorar comandos com versão desconhecida. |
| `reason`      | string | Motivo do reconnect (`agent-transferred` por enquanto).                                   |
| `newSiteId`   | guid   | Novo site do agent (informativo; a verdade é a config HTTP).                              |
| `newClientId` | guid   | Novo cliente do agent (informativo).                                                      |
| `revision`    | string | Revisão da mudança, para correlacionar com o sync ping recebido.                          |

## 3. Comportamento esperado do agent

Ao receber `CommandType = "nats.reconnect"`:

1. **Ignorar silenciosamente** se a versão não for suportada (backward compatibility — versões antigas do agent não devem quebrar).
2. Re-buscar `GET /api/v1/agent-auth/me/configuration` (obtém os novos `siteId`/`clientId` e todas as policies resolvidas site > cliente > servidor).
3. Reconectar ao NATS imediatamente (fechar conexão atual e reconectar). O auth callout emitirá novo JWT com os subjects do site novo.
4. Após reconectar, re-sincronizar recursos com revision inferior à indicada:
   - `/agent-auth/me/automation/policy-sync` (policies de automação);
   - `/agent-auth/me/app-store/effective`;
   - `/agent-auth/me/update/manifest`.
5. Persistir os novos `siteId`/`clientId` localmente (estado do agent/config).

## 4. Auto-recuperação (defesa em profundidade)

Independentemente de receber o comando, o agent deve, **a cada ciclo de polling de config** (manifest recomenda 5 min):

- Comparar `siteId`/`clientId` retornados pela config HTTP com os IDs em uso localmente (ou derivados dos subjects do JWT NATS ativo).
- Em divergência → executar o mesmo fluxo dos passos 2–5 acima.

Isso garante recuperação mesmo se:

- O agent estava offline no momento da transferência (publish no subject antigo cai no vazio);
- O publish se perdeu (NATS indisposto);
- O comando `nats.reconnect` foi ignorado por versão antiga.

## 5. Sync ping acompanhante

Junto do comando, o servidor também publica um `SyncInvalidationPing` (Resource = `Configuration`, mesma `revision`) em **ambos** os subjects (antigo e novo). O agent pode tratá-lo como gatilho alternativo de re-sync, com o mesmo comportamento do item 3.

## 6. Observabilidade

- O resultado da API de transferência inclui `agentNotified: bool` — se `false`, o frontend pode exibir "Agent será atualizado em instantes" em vez de "atualizado".
- O delivery do ping é registrado na tabela `sync_ping_deliveries` para auditoria.
- Log no servidor: `"Agent {AgentId} transferred: sync ping dual-published (old site {X}, new site {Y}) and nats.reconnect sent."`
