# Configuration Policies - Integracao com Site de Gerenciamento

## Objetivo

Documentar a integracao do painel de gerenciamento com o modulo de configuracoes hierarquicas (Server -> Client -> Site), incluindo os novos recursos:

- RecoveryEnabled
- DiscoveryEnabled
- P2PFilesEnabled
- ChatAIEnabled
- KnowledgeBaseEnabled
- SupportEnabled
- AppStorePolicy

---

## Base URL

```
/api/configurations
```

---

## Modelo de Heranca

A API segue heranca por escopo:

1. Server (Global)
2. Client
3. Site

Regra de resolucao:

- Se Site tiver valor local, usa Site.
- Senao, se Client tiver valor local, usa Client.
- Senao, usa Server.

Campos bloqueados:

- Lock no pai impede override no filho.
- Locks sao definidos em `locked_fields_json`.
- A API de metadata informa se pode editar em cada nivel.

---

## Campos de Politica (nomes oficiais para UI)

Use estes nomes no painel:

- `RecoveryEnabled`: recuperacao de dispositivo (reuso de identidade do agent)
- `DiscoveryEnabled`: descoberta de agents via rede
- `P2PFilesEnabled`: transferencia P2P entre agents da mesma rede
- `ChatAIEnabled`: chat IA para suporte
- `KnowledgeBaseEnabled`: base de conhecimento
- `SupportEnabled`: suporte remoto via MeshCentral
- `AppStorePolicy`: politica da loja de aplicativos
  - `0 = Disabled`
  - `1 = PreApproved`
  - `2 = All`

Campos relacionados no mesmo fluxo:

- `AppStorePolicy`
- `InventoryIntervalHours`
- `TokenExpirationDays`
- `MaxTokensPerAgent`
- `AgentHeartbeatIntervalSeconds`
- `AgentOfflineThresholdSeconds`
- `AutoUpdateSettingsJson`
- `AIIntegrationSettingsJson`

---

## Endpoints para o Painel

### 1) Server

- `GET /api/configurations/server`
- `PUT /api/configurations/server`
- `PATCH /api/configurations/server`
- `POST /api/configurations/server/reset`
- `GET /api/configurations/server/metadata`

### 2) Client

- `GET /api/configurations/clients/{clientId}`
- `GET /api/configurations/clients/{clientId}/effective`
- `GET /api/configurations/clients/{clientId}/metadata`
- `PUT /api/configurations/clients/{clientId}`
- `PATCH /api/configurations/clients/{clientId}`
- `DELETE /api/configurations/clients/{clientId}`
- `POST /api/configurations/clients/{clientId}/reset/{propertyName}`

### 3) Site

- `GET /api/configurations/sites/{siteId}`
- `GET /api/configurations/sites/{siteId}/effective`
- `GET /api/configurations/sites/{siteId}/metadata`
- `PUT /api/configurations/sites/{siteId}`
- `PATCH /api/configurations/sites/{siteId}`
- `DELETE /api/configurations/sites/{siteId}`
- `POST /api/configurations/sites/{siteId}/reset/{propertyName}`

---

## Fluxo recomendado no Frontend

1. Carregar metadata do nivel antes de renderizar formulario.
2. Desabilitar controles onde `CanEditAtX = false`.
3. Exibir badge de origem pelo `SourceType`:
   - `2` = Global
   - `3` = Client
   - `4` = Site
   - `0` = Bloqueado
4. Em Patch, enviar apenas campos alterados.
5. Para "voltar a herdar", usar endpoint `reset/{propertyName}`.

---

## Exemplos de Integracao

### A) Ler configuracao efetiva de um Client

```bash
curl -X GET "http://localhost:5001/api/configurations/clients/{clientId}/effective"
```

Resposta (resumo):

```json
{
  "clientId": "...",
  "recoveryEnabled": true,
  "discoveryEnabled": false,
  "p2pFilesEnabled": true,
  "supportEnabled": false,
  "chatAIEnabled": true,
  "knowledgeBaseEnabled": true,
  "appStorePolicy": 1,
  "blockedFields": ["AppStorePolicy"],
  "inheritance": {
    "RecoveryEnabled": 3,
    "DiscoveryEnabled": 2,
    "P2PFilesEnabled": 3,
    "SupportEnabled": 2,
    "ChatAIEnabled": 3,
    "KnowledgeBaseEnabled": 2,
    "AppStorePolicy": 2
  }
}
```

### B) Metadata para montar permissao de edicao (Client)

```bash
curl -X GET "http://localhost:5001/api/configurations/clients/{clientId}/metadata"
```

Resposta (resumo):

```json
{
  "level": "Client",
  "globalLockedFields": ["AppStorePolicy"],
  "clientLockedFields": ["ChatAIEnabled"],
  "fields": {
    "ChatAIEnabled": {
      "field": "ChatAIEnabled",
      "sourceType": 3,
      "isLockedByGlobal": false,
      "isLockedByClient": true,
      "canEditAtClient": true,
      "canEditAtSite": false
    }
  }
}
```

### C) Atualizar parcialmente Client (PATCH)

```bash
curl -X PATCH "http://localhost:5001/api/configurations/clients/{clientId}" \
  -H "Content-Type: application/json" \
  -d '{
    "ChatAIEnabled": true,
    "KnowledgeBaseEnabled": true,
    "AppStorePolicy": 1,
    "DiscoveryEnabled": true,
    "P2PFilesEnabled": false,
    "SupportEnabled": true,
    "RecoveryEnabled": true
  }'
```

### D) Voltar campo para heranca

```bash
curl -X POST "http://localhost:5001/api/configurations/clients/{clientId}/reset/ChatAIEnabled"
```

### E) Atualizar Site com override local

```bash
curl -X PATCH "http://localhost:5001/api/configurations/sites/{siteId}" \
  -H "Content-Type: application/json" \
  -d '{
    "SupportEnabled": true,
    "P2PFilesEnabled": true
  }'
```

---

## Regras de UX recomendadas

- Mostrar 3 estados por campo:
  - Herdado
  - Configurado localmente
  - Bloqueado por nivel superior
- Mostrar origem efetiva ao lado do toggle/select.
- Exibir acao "Resetar para heranca" quando campo estiver local.
- Em erro 400 por campo bloqueado, mostrar mensagem direta no controle.

---

## Compatibilidade de nomes

A API usa apenas nomes canônicos e nao aceita aliases legados.

- RecoveryEnabled
- DiscoveryEnabled
- P2PFilesEnabled
- SupportEnabled
- ChatAIEnabled
- KnowledgeBaseEnabled

---

## Checklist de integracao

1. Implementar tela de metadata por nivel.
2. Renderizar formulario com base em `fields` e `canEdit...`.
3. Consumir `/effective` para visualizacao consolidada.
4. Usar `PATCH` para alteracoes incrementais.
5. Usar `reset/{propertyName}` para heranca.
6. Tratar erro de bloqueio (400) no frontend.

---

**Ultima atualizacao:** 11 de marco de 2026
