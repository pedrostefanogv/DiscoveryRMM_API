# Plano de Implementacao - Relatorios Dinamicos (XLS/PDF)

Data: 2026-03-04
Status: planejamento aprovado para inicio de implementacao

## Objetivo
Implementar um modulo de relatorios dinamicos com templates configuraveis (layout + filtros + dataset), gerando XLSX e PDF de forma hibrida:
- Sincrona para consultas pequenas (download imediato)
- Assincrona para consultas grandes (job + status + download posterior)

## Diretrizes de produto
- Cobrir todos os dominios de dados solicitados:
  - Inventario de software
  - Logs
  - Auditoria de configuracao
  - Tickets
  - Hardware de agentes
- Fonte de dados dinamica com seguranca:
  - Usar somente datasets pre-definidos (whitelist)
  - Nao permitir SQL livre de usuario
- Layout MVP:
  - Intermediario (colunas, ordem, agrupamento, subtotais, cabecalho/rodape)
- Licenciamento:
  - Preferir bibliotecas livres/comercial-friendly
  - Evitar AGPL

## Bibliotecas recomendadas (livres de uso)
- XLSX: ClosedXML (MIT)
- PDF: PdfSharpCore (MIT) + MigraDocCore (MIT)

Alternativas:
- CsvHelper (MIT) para export CSV opcional
- QuestPDF (licenca comercial em alguns cenarios; nao recomendada como padrao aqui)

## Arquitetura proposta (aderente ao projeto)
Seguir padrao atual do Meduza:
- Meduza.Core: entidades, enums, interfaces
- Meduza.Infrastructure: repositorios Dapper, servicos de query/renderizacao
- Meduza.Api: controllers, validators, hosted services
- Meduza.Migrations: migrations FluentMigrator

## Modelo de dominio (fase inicial)
Criar em `src/Meduza.Core/Entities`:
- `ReportTemplate`
  - `Id`, `ClientId` (nullable para global), `Name`, `Description`
  - `DatasetType`, `DefaultFormat`
  - `LayoutJson`, `FiltersJson`, `IsActive`
  - `CreatedAt`, `UpdatedAt`
- `ReportExecution`
  - `Id`, `TemplateId`, `ClientId`
  - `Format`, `FiltersJson`, `Status`
  - `ResultPath` ou `ResultBlobKey`, `ErrorMessage`
  - `CreatedAt`, `StartedAt`, `FinishedAt`

Criar em `src/Meduza.Core/Enums`:
- `ReportDatasetType`:
  - `SoftwareInventory`, `Logs`, `ConfigurationAudit`, `Tickets`, `AgentHardware`
- `ReportFormat`:
  - `Xlsx`, `Pdf`
- `ReportExecutionStatus`:
  - `Pending`, `Running`, `Completed`, `Failed`, `Cancelled`

## Persistencia e migration
Criar migration nova (`M024_...`) em `src/Meduza.Migrations/Migrations` com:
- Tabela `report_templates`
  - colunas principais + JSON (`layout_json`, `filters_json`)
  - indice por (`client_id`, `dataset_type`, `is_active`)
  - indice por `created_at`
- Tabela `report_executions`
  - FK para `report_templates`
  - colunas de status, timestamps, metadata
  - indice por (`client_id`, `status`, `created_at`)
  - indice por `template_id`

## Repositorios e servicos
Criar interfaces em `src/Meduza.Core/Interfaces`:
- `IReportTemplateRepository`
- `IReportExecutionRepository`
- `IReportDatasetQueryService`
- `IReportRenderer`
- `IReportService` (orquestracao)

Implementar em `src/Meduza.Infrastructure`:
- Repositorios Dapper no padrao existente (filtros com `DynamicParameters`)
- Dataset query service com whitelist de filtros/ordenacao por dataset
- Renderers:
  - `XlsxReportRenderer` (ClosedXML)
  - `PdfReportRenderer` (PdfSharpCore + MigraDocCore)

## API e validacao
Criar controller `ReportsController` em `src/Meduza.Api/Controllers`:
- `POST /api/reports/templates`
- `GET /api/reports/templates`
- `GET /api/reports/templates/{id}`
- `PUT /api/reports/templates/{id}`
- `DELETE /api/reports/templates/{id}`
- `GET /api/reports/datasets` (catalogo de datasets e campos permitidos)
- `POST /api/reports/run` (hibrido: sync/async)
- `GET /api/reports/executions/{id}` (status)
- `GET /api/reports/executions/{id}/download`

Criar validators em `src/Meduza.Api/Validators`:
- `CreateReportTemplateRequestValidator`
- `RunReportRequestValidator`

Validacoes criticas:
- Dataset obrigatorio e valido
- Colunas/ordenacao/agrupamento apenas de whitelist
- Limites maximos de range de datas, linhas e tamanho de payload

## Estrategia hibrida (sync + async)
Sugestao inicial:
- Sync quando estimativa <= 10.000 linhas e tempo esperado curto
- Async acima desse limite

Async:
- Persistir execucao como `Pending`
- Worker processa fila e marca `Running/Completed/Failed`
- Download disponivel por ID

## Worker de geracao
Criar `ReportGenerationBackgroundService` em `src/Meduza.Api/Services`:
- Reusar padrao de `LogPurgeBackgroundService`
- Loop com `PeriodicTimer`
- `IServiceScopeFactory` por ciclo
- Retry e log estruturado

## Seguranca e governanca
- Isolamento por `ClientId` em todas as consultas e downloads
- Templates globais (`ClientId = null`) somente leitura para tenants
- Auditoria de operacoes de relatorio:
  - criar/editar template
  - executar
  - download
- Adicionar rate limit e timeout por execucao

## Roadmap por fases
1. Fase 1: entidades, migration, repositorios basicos
2. Fase 2: datasets whitelist + validacao
3. Fase 3: renderers XLSX/PDF + endpoint run sync
4. Fase 4: execucao async + worker + status/download
5. Fase 5: hardening (rate limit, auditoria detalhada, performance)

## Sequencia de entrega recomendada por dataset
1. SoftwareInventory
2. ConfigurationAudit
3. Logs
4. Tickets
5. AgentHardware

## Criterios de aceite (MVP)
- Criar template dinamico para dataset de software
- Executar e baixar XLSX e PDF com layout basico/intermediario
- Execucao async funcional para payload grande
- Validacoes bloqueando campos/filtros fora da whitelist
- Isolamento por tenant validado

## Comandos de verificacao
- `dotnet build Meduza.slnx`
- Subir API e testar endpoints no fluxo completo

## Riscos e mitigacoes
- Risco: consultas pesadas em logs e tickets
  - Mitigacao: limites de periodo/paginacao e modo async por padrao em volume alto
- Risco: deriva de layout entre XLSX e PDF
  - Mitigacao: contrato de layout comum + degradacao controlada por formato
- Risco: falta de auth de usuario final consolidada
  - Mitigacao: aplicar escopo por client em servicos e preparar integracao com auth quando pronta

## Observacao para implementacao futura
Este documento e o baseline tecnico. Em cada fase, atualizar:
- Decisoes de schema
- Limites operacionais
- Contratos de API
- Resultados de benchmark
