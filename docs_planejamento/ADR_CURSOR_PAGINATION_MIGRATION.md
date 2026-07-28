# ADR: Migração de Paginação Offset para Cursor (Keyset)

**Data:** 2026-05-13
**Status:** Aceito

## Contexto

O sistema Discovery RMM possui diversos endpoints de listagem que utilizam paginação baseada em `OFFSET` + `LIMIT` no banco de dados. Essa abordagem apresenta problemas de performance à medida que o volume de dados cresce:

1. **Custo crescente do OFFSET** — O banco precisa escanear e descartar linhas anteriores ao offset.
2. **Inconsistência em tabelas voláteis** — Inserções/remoções no topo deslocam a página.
3. **Sem suporte a ordenação composta estável** — OFFSET + LIMIT não garante uma âncora imutável.

Já existe implementação de referência no repositório:
- `LogRepository.QueryPageAsync` (cursor por CreatedAt desc + Id desc)
- `AppApprovalAuditRepository.GetHistoryAsync` (cursor por Guid/Id decrescente)
- `SoftwareInventoryController` (cursor composto por item específico)

## Decisão

Migrar **todos os endpoints de listagem** do padrão offset para **keyset/cursor-based pagination**, usando dois padrões conforme o tipo de ordenação primária disponível:

| Padrão | Ordenação | Cursor |
|--------|-----------|--------|
| **Type A — Coluna temporal** | `CreatedAt desc + Id desc` | `base64(ticks\|guid)` |
| **Type B — Guid descendente** | `Id desc` | `guid` |
| **Type C — Offset seguros** | Casos sem chave natural | Migrar para Type A sempre que possível |

## Padrão de Cursor

### Type A (temporal): Cursor por CreatedAt + Id

Usado para entidades com `CreatedAt` ordenável de forma estável.

```
cursor = base64($"{createdAtTicks}|{id:N}")
```

Consulta:
```sql
WHERE (created_at < @cursorCreatedAt OR
      (created_at = @cursorCreatedAt AND id < @cursorId))
ORDER BY created_at DESC, id DESC
LIMIT @limit + 1
```

### Type B (Guid descendente): Cursor por Id

Usado para tabelas onde a ordenação temporal não é relevante ou quando o Guid sequential (NewId) é suficiente.

```
cursor = guid   ("N" format)
```

Consulta:
```sql
WHERE id < @cursor
ORDER BY id DESC
LIMIT @limit + 1
```

## Contrato de resposta padronizado

```csharp
public sealed record CursorPageDto<T>(
    IReadOnlyList<T> Items,
    int ReturnedItems,
    string? Cursor,
    string? NextCursor,
    bool HasMore,
    int Limit);
```

## Estratégia de migração

1. **Novo método no repositório** — adicionar `QueryPageAsync` mantendo o `QueryAsync` original para compatibilidade.
2. **Novo endpoint no controller** — adicionar `GET /{resource}/page` mantendo o endpoint legado.
3. **Hook/função no frontend** — usar `useInfiniteQuery` com `getNextPageParam`.
4. **Depreciação** — marcar endpoint offset como obsoleto e remover na versão major seguinte.

## Impacto

### Positivo
- Performance O(log n) vs O(n) para páginas profundas
- Consistência de leitura entre requisições
- Padrão único para toda a codebase
- Reuso do helper de encode/decode já validado nos testes de Logs

### Negativo
- Duplicação temporária (endpoint antigo + novo)
- Frontend precisa de adaptação para usar cursor em vez de offset
- Ordenação não pode ser alterada sem mudar o cursor
- Tabelas sem CreatedAt ou sem Id sequential exigem adaptação

## Plano de Implementação

| Prioridade | Alvo | Padrão | Status |
|-----------|------|--------|--------|
| P0 | Logs (LogRepository) | Type A | ✅ Concluído |
| P0 | Tickets (TicketRepository) | Type A | ✅ Concluído |
| P1 | Agent Alerts (AgentAlertRepository) | Type A | ✅ Concluído |
| P2 | Automation Scripts (AutomationScriptRepository) | Type A (UpdatedAt) | ✅ Concluído |
| P3 | Automation Tasks (AutomationTaskRepository) | Type A (UpdatedAt) | ✅ Concluído |
| P4 | Winget/Chocolatey Packages | Type B | 🔜 Pendente |
| P5 | AppPackage | Type B | 🔜 Pendente |
| P6 | P2P distribution/status | Type A | 🔜 Pendente |
| P7 | Users | Type A | 🔜 Pendente |
| P8 | AppStore (migrar offset-encoded para cursor real) | Type A | 🔜 Pendente |

## Artefatos criados

| Artefato | Caminho | Propósito |
|----------|---------|-----------|
| ADR (este arquivo) | `docs_planeijamento/ADR_CURSOR_PAGINATION_MIGRATION.md` | Decisão arquitetural |
| Helper compartilhado | `src/Discovery.Core/Helpers/CursorPaginationHelper.cs` | Encode/decode + slice + apply cursor (Type A e B) |
| DTO genérico | `src/Discovery.Core/DTOs/CursorPageDto.cs` | Contrato de página único |
| Endpoint de exemplo | `GET /api/v*/logs/page` | Já validado nos testes e integrado na UI |
| Endpoint | `GET /api/v*/tickets/page` | ✅ |
| Endpoint | `GET /api/v*/agent-alerts/page` | ✅ |
| Endpoint | `GET /api/v*/automation/scripts/page` | ✅ |
| Endpoint | `GET /api/v*/automation/tasks/page` | ✅ |

## Como consumir (exemplo front-end)

```ts
// Hook com useInfiniteQuery
const logs = useInfiniteQuery({
  queryKey: ["logs", filters],
  initialPageParam: null,
  queryFn: ({ pageParam }) => api.get(`/logs/page`, {
    ...filters,
    cursor: pageParam,
  }),
  getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
});

// Carregar próxima página
logs.fetchNextPage();
```
