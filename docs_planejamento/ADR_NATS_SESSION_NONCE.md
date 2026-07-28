# ADR 004: NATS Session Nonce (Fase 3 — Planejamento Futuro)

**Status**: Proposed  
**Date**: 2026-06-06  
**Author**: Pedro Stefanogv  

---

## Contexto

Após implementar as fases 1 (detecção de conexão duplicada via Redis SET NX) e 2 (auditoria `last_nats_connected_at`), permanece uma janela de vulnerabilidade:

- Se um agente **desconecta** do NATS (ex: crash) e **reconecta** com o mesmo token `mdz_`, a trava Redis pode já ter expirado (TTL = 5 min), então a reconexão é aceita.
- Um atacante que capture o token pode esperar a trava expirar e conectar-se.

A **Fase 3** introduz um **nonce por conexão** para eliminar essa janela.

---

## Decisão

Adicionar um **claim personalizado `nonce`** ao JWT NATS emitido pelo auth callout.  
O nonce é armazenado no Redis com o mesmo TTL do JWT.  
Se o agente desconectar e reconectar, um **novo nonce** é gerado, e o nonce anterior é **invalidado** (deletado do Redis).  
O middleware/validador do lado do servidor pode opcionalmente verificar se o nonce do JWT ainda é o nonce ativo no Redis.

---

## Implementação Planejada

### 1. Nonce claim no JWT NATS

```csharp
// Em NatsCredentialsService.IssueUserJwtForAgentAsync()
var nonce = Guid.NewGuid().ToString("N");
claims.User.UserType = nonce; // ou claim personalizado via NatsJwt
```

### 2. Armazenamento do nonce no Redis

```csharp
// Chave: nats:session:nonce:{agentId}:{nonce}
// TTL = expiração do JWT NATS
await _redisService.SetAsync(
    $"nats:session:nonce:{agentId}:{nonce}",
    "active",
    jwtTtlSeconds);
```

### 3. Invalidação de nonce anterior na reconexão

```csharp
// Ao gerar novo nonce, buscar e deletar nonces antigos do mesmo agent
var oldKeys = await _redisService.GetKeysByPrefixAsync($"nats:session:nonce:{agentId}:");
foreach (var key in oldKeys)
    await _redisService.DeleteAsync(key);
```

### 4. Validação opcional no servidor (eventos/mensagens do agente)

```csharp
// Ao processar mensagem de um agente, verificar se o nonce no JWT ainda é válido
var nonce = ExtractNonceFromJwt(jwt);
var exists = await _redisService.GetAsync($"nats:session:nonce:{agentId}:{nonce}");
if (exists is null)
    return Unauthorized("Nonce expired — agent reconnected with new session.");
```

---

## Considerações de Segurança

| Aspecto | Detalhe |
|---------|---------|
| **Race condition** | Não crítica — se dois auth callouts concorrerem para o mesmo token, ambos recebem JWT inválido porque o Redis não garante atomicidade entre delete+create. A trava SET NX (Fase 1) já mitiga isso. |
| **TTL** | Nonce deve expirar junto com o JWT (ex: 720 min). Se o agente renovar o JWT, um novo nonce é gerado. |
| **Custo Redis** | ~100 bytes por nonce. Com 10.000 agents, ~1 MB. Desprezível. |

---

## Status da Fase 3

**Preparada para implementação** quando necessário. Código final não incluído neste commit — ver ADR para referência de assinaturas.
