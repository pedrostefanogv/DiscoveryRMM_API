# ADR — Visibilidade de campos internos do departamento (leitura)

- **Status:** Adiado (registrado para implementação futura)
- **Data:** 2026-09-27
- **Contexto:** campos customizados de departamento com `IsInternal = true`

## Contexto

Campos de departamento têm o flag `IsInternal` ("Campo Interno — visível apenas
para atendentes"). Hoje esse flag é aplicado **apenas no schema/formulário**:

- `GetPublicSchemaForDepartmentAsync` filtra `IsActive && !IsInternal` — é o que
  alimenta o formulário de abertura de chamado e a tela de templates. Portanto
  um campo interno **não** é cobrado na abertura.
- `GetFullSchemaForDepartmentAsync` (usado pela tela de detalhe via
  `GET /departments/{id}/ticket-schema?includeInternal=true`) inclui os internos,
  para o atendente preencher.

O ponto em aberto é a **leitura de valores**: `GET /api/v1/tickets/{id}/custom-fields`
(`TicketsController.GetCustomFields` → `CustomFieldService.GetValuesAsync`)
devolve os valores de **todos** os campos aplicáveis ao chamado, inclusive os
internos, para qualquer principal que tenha `Tickets.View` + ACL no chamado.
O `IsInternal` **não** filtra essa resposta.

## Decisão atual

Não implementar agora. Na data desta ADR:

- O produto é um **console de atendimento**; não existe portal de solicitante no
  repositório (`DiscoveryRMM_Site`). Todo acesso exige `Tickets.View` + ACL, ou
  seja, todo leitor de ticket é um atendedor.
- Não há um conceito de "solicitante vs atendente" (role/permission) que
  permita decidir quem pode ver o valor interno.
- Implementar um filtro por papel sem esse conceito seria especulativo e
  poderia **bloquear acesso legítimo** de atendentes.

## Risco se nada for feito

Se for adicionada uma superfície de solicitante/portal (ou um token de leitura
de ticket com escopo reduzido), os valores de campos internos **vazam** por
`GET /tickets/{id}/custom-fields`.

## Implementação proposta (quando houver o conceito de papel)

1. Definir a permissão/role que caracteriza "atendente" (ex.:
   `Tickets.InternalFields.View` ou reutilizar `Departments.View`, que já protege
   o schema completo).
2. `TicketsController.GetCustomFields`: resolver a permissão do principal e
   passar `includeInternal` para o serviço.
3. `CustomFieldService.GetValuesAsync`: quando `includeInternal = false`,
   excluir as definições com `ScopeType == Department && IsInternal` (a query
   `BuildDefinitionsQueryAsync` já recebe o `scopeType`; adicionar o filtro).
4. Manter o schema público já existente para o formulário de abertura.
5. Testes de integração: atendente vê o interno; principal sem a permissão não
   vê o campo nem na listagem de valores.

## Arquivos relacionados

- `src/Discovery.Api/Controllers/TicketsController.cs` (GetCustomFields)
- `src/Discovery.Api/Controllers/DepartmentsController.cs` (GetTicketSchema)
- `src/Discovery.Infrastructure/Services/CustomFieldService.cs`
  (`GetValuesAsync`, `BuildDefinitionsQueryAsync`)
- `src/Discovery.Infrastructure/Services/DepartmentCustomFieldService.cs`
  (`GetPublicSchemaForDepartmentAsync`, `GetFullSchemaForDepartmentAsync`)
- `DiscoveryRMM_Site/src/components/tickets/TicketFieldsSection.tsx`
