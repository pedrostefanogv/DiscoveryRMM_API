# Plano de implementacao - self-update do agent

## Problema

Os agents precisam descobrir quando existe uma nova versao disponivel, entender se o update e opcional ou obrigatorio, baixar o artefato a partir do proprio servidor e reportar o resultado do processo de update para o backend.

## Premissa adotada

- Fonte de update: **instalador hospedado pelo proprio servidor** (mesmo arquivo `.exe` usado na instalacao inicial).
- O fluxo e **hibrido**:
  - **pull** pelo agent para consultar manifest/politica;
  - **push de invalidacao** pelo servidor para acelerar o check quando houver nova release.
- O instalador e seguro para update porque o script NSIS detecta `config.json` existente e grava apenas `installer.json` auxiliar, preservando o `agentId` e `authToken` do agent registrado.

---

## Estado atual — o que ja esta implementado

### Entidades e persistencia

- `AgentRelease` — catalogo de releases (`version`, `channel`, `isActive`, `mandatory`, `minimumSupportedVersion`, `releaseNotes`, `publishedAtUtc`)
- `AgentReleaseArtifact` — artefatos por plataforma/arquitetura com `sha256`, `sizeBytes`, `storageObjectKey`
- `AgentUpdateEvent` — trilha de auditoria de cada etapa do ciclo de update por agent
- Migracao `M092_CreateAgentUpdateInfrastructure` ja aplicada com tabelas `agent_releases`, `agent_release_artifacts`, `agent_update_events` e colunas `agent_update_policy_json` nas tabelas de configuracao

### Configuracao de politica

- `AgentUpdatePolicy` — objeto herdavel em `Server → Client → Site` com campos:
  - `Enabled`, `Channel`, `CheckMode`, `CheckEveryHours`
  - `TargetVersion`, `MinimumRequiredVersion`
  - `AllowDeferral`, `MaxDeferralHours`
  - `PreferredArtifactType`, `RolloutPercentage`

### Endpoints do servidor (admin)

Todos em `AgentUpdatesController` (`/api/agent-updates`):

| Metodo | Rota | Descricao |
|--------|------|-----------|
| `GET`  | `/releases` | Lista releases |
| `GET`  | `/releases/{id}` | Detalha release |
| `POST` | `/releases` | Cria release |
| `PUT`  | `/releases/{id}` | Atualiza release |
| `DELETE` | `/releases/{id}` | Remove release e artefatos do storage |
| `POST` | `/releases/{id}/artifacts` | Upload manual de artefato (multipart) |
| `POST` | `/releases/{id}/build-artifact` | **Builda o installer e registra como artefato** |
| `POST` | `/releases/{id}/promote` | Promove uma release para outro canal com cópia dos artefatos |
| `POST` | `/agents/{agentId}/force-check` | Enfileira comando `Update` para check imediato/forçado no agent |
| `GET`  | `/dashboard/rollout` | Dashboard operacional de rollout com agregação por client/site/agent |
| `DELETE` | `/artifacts/{id}` | Remove artefato |
| `GET`  | `/agents/{agentId}/events` | Historico de eventos de update de um agent |

### Endpoints do agent (autenticados)

Todos em `AgentAuthController` (`/api/agent-auth/me`):

| Metodo | Rota | Descricao |
|--------|------|-----------|
| `GET`  | `/update/manifest` | Retorna manifest com versao atual, nova, sha256, obrigatoriedade |
| `GET`  | `/update/download` | Stream autenticado do artefato (installer .exe) |
| `POST` | `/update/report`   | Registra evento do ciclo de update |

### Logica de selecao de release (`AgentUpdateService`)

- Seleciona a release ativa mais recente por canal/plataforma/arquitetura
- Respeita `TargetVersion` da politica se definida
- Calcula `RolloutEligible` por hash deterministico do `AgentId` (evita thundering herd)
- Calcula `Mandatory` por `MinimumRequiredVersion` (politica) e `MinimumSupportedVersion` (release)
- Computa `Revision` para integracao com o sync-manifest
- Atualiza `Agent.AgentVersion` automaticamente ao receber evento `InstallSucceeded`

### Build do binario

- `AgentPackagePrebuildHostedService` executa `wails build -s -nopackage` no startup da API (`forceRebuild: true`)
- Binario compilado disponivel em `AgentPackage:BinaryPath` (ex: `/opt/Discovery/build/bin/discovery.exe`)
- `AgentPackageService.BuildInstallerAsync(deployToken)` chama `makensis` sobre o binario disponivel

### Integracao com sync-manifest

- `SyncResourceType.AgentUpdate` ja existe
- Recurso incluido em `GET /api/agent-auth/me/sync-manifest`
- Invalidacao publicada em criacao, atualizacao, remocao de release/artefato e apos build automatico

---

## Fluxo completo de publicacao de uma release

```
1. Admin cria a release
   POST /api/agent-updates/releases
   { "version": "1.2.0", "channel": "stable", "isActive": true, "mandatory": false }

2. Admin dispara o build e publicacao do artefato
   POST /api/agent-updates/releases/{releaseId}/build-artifact
   { "platform": "windows", "architecture": "amd64", "forceRebuild": false }

   Internamente:
     a. [se forceRebuild] wails build -s -nopackage (recompila o binario)
     b. makensis com ARG_DEFAULT_KEY="" → gera discovery-amd64-installer.exe
     c. SHA256 calculado; artefato enviado ao object storage
     d. AgentReleaseArtifact salvo no banco
     e. Invalidacao SyncResourceType.AgentUpdate publicada globalmente

3. Agents recebem invalidacao e antecipam o check (ou aguardam o proximo poll)
   GET /api/agent-auth/me/sync-manifest → revision de AgentUpdate mudou

4. Agent consulta o manifest
   GET /api/agent-auth/me/update/manifest
   ← { updateAvailable: true, latestVersion: "1.2.0", sha256: "...", mandatory: false, ... }

5. Agent baixa o artefato
   GET /api/agent-auth/me/update/download
   ← stream do discovery-amd64-installer.exe

6. Agent executa o instalador silenciosamente
   discovery-amd64-installer.exe /S
   - config.json existente e preservado (agentId/authToken mantidos)
   - binario substituido, servico Windows reiniciado

7. Agent reporta o resultado
   POST /api/agent-auth/me/update/report
   { "eventType": "InstallSucceeded", "targetVersion": "1.2.0" }
   → Agent.AgentVersion atualizado no banco automaticamente
```

---

## Comportamento do NSIS installer no cenario de update

O script `project.nsi` detecta se `config.json` ja existe em `$COMMONAPPDATA\Discovery\`:

```nsis
IfFileExists "$R0\Discovery\config.json" 0 +3
StrCpy $R1 "$R0\Discovery\installer.json"  ; instalacao existente → preserva config.json
Goto +2
StrCpy $R1 "$R0\Discovery\config.json"     ; instalacao nova → cria config.json
```

Quando rodado com `/S` (silencioso), o wizard de configuracao e pulado:

```nsis
${If} ${Silent}
   Abort  ; pula a pagina de configuracao
${EndIf}
```

O token vazio embutido na build do artefato de update e irrelevante para agents ja registrados.

---

## Regras recomendadas para o agent

- Nunca aceitar downgrade por padrao
- Usar backoff em falhas repetidas de download/install
- Se a release for obrigatoria (`mandatory: true`), executar update independente de janela de manutencao
- Tratar versao invalida do agent como caso especial e reportavel
- Validar `sha256` apos download antes de executar o installer

---

## Comparacao de versao

- Versao do agent padronizada como **SemVer**
- Comparacao feita por `SemanticVersion` (helper em `Discovery.Core.Helpers`)
- Versoes fora do padrao sao rejeitadas antes de qualquer decisao de update
- Prefixo `v` e normalizado automaticamente (`v1.2.0` → `1.2.0`)

---

## Fases de implementacao

### Fase 1 — Base funcional — CONCLUIDA

- [x] Modelagem `AgentUpdatePolicy` e hierarquia de configuracao
- [x] Catalogo de releases (`AgentRelease`, `AgentReleaseArtifact`)
- [x] Endpoints admin de CRUD de releases e upload de artefatos
- [x] Endpoints autenticados `me/update/manifest`, `me/update/download`, `me/update/report`
- [x] Logica de selecao de release, rollout percentual, obrigatoriedade
- [x] Integracao com `SyncResourceType.AgentUpdate`
- [x] Persistencia de eventos (`AgentUpdateEvent`) e atualizacao de `Agent.AgentVersion`
- [x] **Endpoint `POST /releases/{id}/build-artifact`** — build automatizado do installer e registro no catalogo

### Fase 2 — Integracao no agent (pendente)

- [ ] Agent consulta `me/update/manifest` no startup e periodicamente
- [ ] Agent reage a invalidacao `AgentUpdate` no sync-manifest
- [ ] Agent baixa, valida sha256 e executa `installer.exe /S`
- [ ] Agent reporta cada etapa via `me/update/report`
- [ ] Backoff e limite de tentativas em caso de falha

### Fase 3 — Operacao e controle fino (pendente)

- [x] Endpoints administrativos de promocao de canal (`beta` → `stable`)
- [x] Check imediato / forcado via infraestrutura de comandos existente
- [x] Dashboard de status operacional de rollout por tenant/site/agent

---

## Riscos e cuidados

- **Self-update do processo em execucao**: o agent nao pode sobrescrever o proprio binario enquanto em uso. O Windows Service deve ser parado pelo installer antes da substituicao do binario — o script NSIS ja faz isso via `sc.exe stop/delete/create`.
- **Seguranca**: validar sha256 apos download. Assinatura de codigo e recomendada em fase posterior.
- **Downgrade acidental**: bloqueado por padrao na logica de comparacao de versao.
- **Loops de falha**: backoff + limite de tentativas no agent.
- **Build no hot path**: o endpoint `build-artifact` e chamado por admin/CI, nao pelo agent. O download do agent serve artefato ja armazenado no object storage.
