# NATS e Object Storage Backlog

Backlog operacional para acompanhar o que ainda precisa ser concluído nas fases abaixo:

- Fase 4: endurecimento NATS multi-tenant e remoção de legacy subjects.
- Fase 5: hardening object storage multi-vendor e migração de fluxos legados remanescentes.

Status sugerido por item:

- Pendente
- Em andamento
- Bloqueado
- Concluído

## Fase 4

### F4.1 Migrar mensageria principal dos agents para subjects canônicos

- Status: Concluído
- Prioridade: Alta
- Objetivo: fazer comando, heartbeat, result, hardware e sync.ping trafegarem somente em subjects tenant.site.agent.
- Dependências: alinhamento do contrato do agent e validação ponta a ponta do roteamento NATS.
- Critério de pronto: não existe mais publish ou subscribe operacional em agent.{id}.* fora de compatibilidade temporária explicitamente controlada.
- Progresso (2026-04-16):
  - `NatsAgentMessaging` publica/assina apenas `tenant.{client}.site.{site}.agent.{agent}.{messageType}` para `command`, `heartbeat`, `result`, `hardware` e `sync.ping`.
  - `NatsCredentialsService` emite ACLs somente para os 5 subjects canônicos do agent.
  - `NatsIsolationTests` e `NatsSubjectBuilderTests` cobrem ausência de prefixo legado `agent.{id}.*` e presença exata dos message types canônicos.
- Arquivos-alvo:
  - src/Discovery.Infrastructure/Messaging/NatsAgentMessaging.cs
  - src/Discovery.Infrastructure/Services/NatsCredentialsService.cs
  - src/Discovery.Core/Helpers/NatsSubjectBuilder.cs
  - docs/MESSAGING_NATS.md

### F4.2 Remover legacy subjects da configuração e da emissão de credenciais

- Status: Concluído
- Prioridade: Alta
- Objetivo: encerrar o uso de NatsIncludeLegacySubjects como caminho operacional.
- Dependências: F4.1 concluída.
- Critério de pronto: backend não expõe mais a flag de legado; JWTs não incluem subjects agent.{id}.*.
- Progresso (2026-04-13):
  - removida a propriedade `NatsIncludeLegacySubjects` do `ServerConfiguration` (runtime).
  - removido o mapeamento EF da coluna `nats_include_legacy_subjects` no `DiscoveryDbContext`.
  - adicionada migração `M093_RemoveLegacyNatsIncludeLegacySubjects` para drop da coluna legada.
- Progresso (2026-04-16):
  - a API deixou de expor `NatsUseScopedSubjects` como campo configurável no payload público de NATS.
  - `ServerConfiguration.NatsUseScopedSubjects` ficou oculto da serialização pública, permanecendo apenas como proteção interna de consistência.
  - `ConfigurationFieldCatalog` deixou de anunciar o campo como metadata configurável.
- Arquivos-alvo:
  - src/Discovery.Core/Entities/ServerConfiguration.cs
  - src/Discovery.Api/Controllers/ConfigurationsController.cs
  - src/Discovery.Infrastructure/Services/NatsCredentialsService.cs
  - src/Discovery.Infrastructure/Data/DiscoveryDbContext.cs
  - docs/MESSAGING_NATS.md

### F4.3 Restabelecer paridade NATS e SignalR no remote debug

- Status: Concluído
- Prioridade: Média
- Objetivo: manter o subject canônico tenant-scoped no NATS sem perder a paridade funcional com o fallback via SignalR.
- Dependências: F4.1 concluída.
- Critério de pronto: sessão, payloads e relay preservam NATS como preferencial e SignalR como fallback real, com o mesmo payload final entregue ao frontend.
- Progresso (2026-04-17):
  - `RemoteDebugSessionManager` voltou a expor `PreferredTransport`, `FallbackTransport`, `NatsSubject` e `SignalRMethod` como metadados canônicos da sessão.
  - `POST /agents/{id}/remote-debug/start` voltou a anunciar a politica dual-channel para o agent e para o cliente consumidor da sessão.
  - `RemoteDebugNatsBridgeService` passou a delegar para um relay comum, e `AgentHub` voltou a aceitar push de log por SignalR autenticado.
  - ACLs NATS do agent foram ampliadas para incluir `tenant.{clientId}.site.{siteId}.agent.{agentId}.remote-debug.log`.
  - suíte `RemoteDebug*` e `NatsIsolationTests` foram ajustadas para a politica dual-channel.
- Arquivos-alvo:
  - src/Discovery.Api/Services/RemoteDebugLogRelayService.cs
  - src/Discovery.Api/Services/RemoteDebugNatsBridgeService.cs
  - src/Discovery.Api/Hubs/AgentHub.cs
  - src/Discovery.Api/Controllers/AgentsController.cs
  - src/Discovery.Api/Services/IRemoteDebugSessionManager.cs
  - src/Discovery.Infrastructure/Services/NatsCredentialsService.cs
  - docs/MESSAGING_NATS.md

### F4.4 Endurecer isolamento multi-tenant no dashboard e auth callout

- Status: Em andamento
- Prioridade: Alta
- Objetivo: garantir que subjects de dashboard e JWTs de usuário respeitem escopo explícito por tenant/client/site.
- Dependências: F4.1 parcialmente concluída.
- Critério de pronto: nenhum subject unscoped permanece fora dos casos documentados; cenários cross-tenant são negados e auditáveis.
- Progresso (2026-04-13):
  - removido subject global `dashboard.events` das ACLs emitidas para usuários com acesso global.
  - bridge SignalR deixou de assinar `dashboard.events` e passou a aceitar apenas subjects tenant-scoped.
  - `NatsSubjectBuilder.DashboardSubject` passou a exigir escopo de cliente; sem escopo explícito agora falha.
  - publicação de evento de dashboard em `NatsAgentMessaging` agora exige `ClientId` explícito.
- Arquivos-alvo:
  - src/Discovery.Infrastructure/Services/NatsCredentialsService.cs
  - src/Discovery.Infrastructure/Messaging/NatsAgentMessaging.cs
  - src/Discovery.Api/Services/NatsAuthCalloutBackgroundService.cs
  - src/Discovery.Core/Helpers/NatsSubjectBuilder.cs
  - docs/MESSAGING_NATS.md

### F4.5 Expandir testes negativos de isolamento multi-tenant

- Status: Em andamento
- Prioridade: Média
- Objetivo: cobrir rejeição cross-tenant, legado desligado e roteamento correto em subjects canônicos.
- Dependências: F4.1 a F4.4 estabilizadas.
- Critério de pronto: suíte automatizada cobre emissão de credenciais, ACLs e remote debug tenant-scoped.
- Progresso (2026-04-13):
  - criado `NatsIsolationTests.cs` com 14 testes cobrindo 3 grupos:
    - Grupo 1 (NatsSubjectBuilder): cross-tenant non-overlap, sem legado, contagem exata de subjects
    - Grupo 2 (NatsCredentialsService com fakes): subjects exclusivos por agente, sem contaminação cross-tenant, wildcard-only para global, multi-client correto
    - Grupo 3 (RemoteDebugSessionManager): rejeição de agente/usuário errado, subject tenant-scoped, sessions distintas
  - corrigido bug em `NatsCredentialsService.IssueForUserAsync`: `AllowedSiteIds.FirstOrDefault()` em lista vazia retornava `Guid.Empty` (não null) causando `Guid?` hasValue=true e omissão de clientes adicionais.
- Arquivos-alvo:
  - src/Discovery.Tests/NatsIsolationTests.cs (novo)
  - src/Discovery.Infrastructure/Services/NatsCredentialsService.cs

## Fase 5

### F5.1 Alinhar factory e contrato do object storage ao modelo S3-compatible real

- Status: Em andamento
- Prioridade: Alta
- Objetivo: remover ambiguidade entre provider type exposto e comportamento efetivo da factory.
- Dependências: decisão arquitetural explícita sobre manter contrato único S3-compatible.
- Critério de pronto: a factory, a validação e a documentação refletem exatamente o contrato suportado.
- Progresso (2026-04-13):
  - removido overload `CreateObjectStorageService(ObjectStorageProviderType)` de `IObjectStorageProviderFactory` e da implementação. Todos os callers externos usam apenas o overload sem parâmetros.
  - removido `using Discovery.Core.Enums` da factory (não mais referenciado).
- Arquivos-alvo:
  - src/Discovery.Core/Interfaces/IObjectStorageProviderFactory.cs ✓
  - src/Discovery.Infrastructure/Services/ObjectStorageProviderFactory.cs ✓

### F5.2 Endurecer compatibilidade multi-vendor do provider S3-compatible

- Status: Em andamento
- Prioridade: Alta
- Objetivo: validar endpoint, path-style, SSL verify, metadata e presigned URLs nos vendors suportados.
- Dependências: F5.1 definida.
- Critério de pronto: matriz mínima validada para MinIO, AWS S3, Cloudflare R2 e Oracle S3-compatible.
- Progresso (2026-04-13):
  - `MinioObjectStorageProvider`: quando `SslVerify = false`, cria `HttpClient` com `DangerousAcceptAnyServerCertificateValidator` e o injeta via `.WithHttpClient()`. Log de aviso emitido.
  - `UsePathStyle`: documentado que o SDK MinIO usa path-style automaticamente para endpoints não-Amazon (MinIO, R2, Oracle, etc.). Sem mudança de código necessária.
- Arquivos-alvo:
  - src/Discovery.Infrastructure/Services/MinioObjectStorageProvider.cs ✓

### F5.3 Reduzir LocalObjectStorageProvider a uso estritamente de desenvolvimento

- Status: Em andamento
- Prioridade: Alta
- Objetivo: tirar o provider local do caminho principal de validação funcional.
- Dependências: F5.2 com cobertura mínima disponível.
- Critério de pronto: provider local fica restrito a dev/teste controlado e não é mais a referência de aceite principal.
- Progresso (2026-04-13):
  - `LocalObjectStorageProvider`: construtor agora lança `InvalidOperationException` se `ASPNETCORE_ENVIRONMENT=Production`. Testes que não setam a variável continuam funcionando.
  - `ObjectStorageTests`: anotado com `[Category("LocalStorage")]` explicitando que são testes locais de disco.
- Arquivos-alvo:
  - src/Discovery.Infrastructure/Services/LocalObjectStorageProvider.cs ✓
  - src/Discovery.Tests/ObjectStorageTests.cs ✓

### F5.4 Padronizar resolução do storage nos fluxos de anexos

- Status: Em andamento
- Prioridade: Média
- Objetivo: evitar inconsistência entre provider ativo e serviço injetado no ciclo de upload, complete-upload, download e limpeza.
- Dependências: F5.1 definida.
- Critério de pronto: anexos usam resolução consistente do provider ativo e metadados persistidos ficam coerentes.
- Progresso (2026-04-13):
  - corrigido fallback `ObjectStorageProviderType.Local` → `ObjectStorageProviderType.S3Compatible` em `AttachmentService.CompletePresignedUploadAsync`.
- Arquivos-alvo:
  - src/Discovery.Infrastructure/Services/AttachmentService.cs ✓

### F5.5 Migrar downloads remanescentes para metadata mais acesso assinado

- Status: Em andamento
- Prioridade: Alta
- Objetivo: reduzir streams de arquivo pelo backend quando o fluxo puder usar redirect ou URL assinada.
- Dependências: F5.2 e F5.4.
- Critério de pronto: artefatos de update e fluxos elegíveis usam acesso assinado seguro; compatibilidade antiga fica removida ou formalmente descontinuada.
- Progresso (2026-04-13):
  - removido `OpenDownloadAsync` de `IAgentUpdateService` e `AgentUpdateService` (nenhum controller chamava; era streaming legado via backend).
  - removido DTO `AgentUpdateDownloadPayload` (não mais referenciado).
  - único caminho de download de artefato agora é `GetPresignedDownloadUrlAsync` (URL assinada + redirect).
- Arquivos-alvo:
  - src/Discovery.Core/Interfaces/IAgentUpdateService.cs ✓
  - src/Discovery.Infrastructure/Services/AgentUpdateService.cs ✓
  - src/Discovery.Core/DTOs/AgentUpdateDtos.cs ✓

### F5.6 Fechar limpeza de compatibilidade em relatórios e anexos

- Status: Em andamento
- Prioridade: Média
- Objetivo: consolidar apenas o fluxo vigente de object-storage-backed metadata.
- Dependências: F5.4 e F5.5.
- Critério de pronto: não restam contratos legados relevantes de armazenamento local ou rotas temporárias sem plano de corte.
- Progresso (2026-04-13):
  - `download-stream` em `ReportsController` marcado `[Obsolete]` com mensagem clara de migração.
  - resposta inclui headers HTTP `Deprecation: true` e `Link: successor-version` apontando para `download`.
- Arquivos-alvo:
  - src/Discovery.Api/Controllers/ReportsController.cs ✓

## Ordem recomendada

1. F4.1
2. F4.2
3. F4.4
4. F4.3
5. F4.5
6. F5.1
7. F5.2
8. F5.3
9. F5.4
10. F5.5
11. F5.6

## Observações

- Esse backlog foi derivado do código atual e deve ser atualizado sempre que uma tarefa mudar de status.
- Se necessário, cada item pode ser desdobrado em subtarefas técnicas e testes específicos.