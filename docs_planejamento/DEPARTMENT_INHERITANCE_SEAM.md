# Seam de Herança de Departamento (preparado, não implementado)

> Status: NÃO implementado. Por decisão de produto, `departments.inherit_from_global_id`
> continua apenas informativo. Este documento registra o desenho e o único ponto de código
> onde a herança deve entrar quando for priorizada.

## 1. Situação atual

`Department.InheritFromGlobalId` existe no banco, na API e na tela, mas NENHUM comportamento o
consome: não há herança de membros, de configuração nem de competências.

A equipe candidata de um departamento é resolvida hoje por
`IDepartmentTeamResolver.ResolveMembersAsync` (`DepartmentTeamResolver`): membros com vínculo
ativo do próprio departamento cujo usuário também está ativo, ordenados pela data de entrada.
Esse resolvedor é usado pela estratégia determinística (round-robin / menos abertos) e pela
triagem por IA — logo, implementar a herança ali cobre os dois fluxos sem tocar nos serviços.

## 2. O que decidir antes de implementar

1. Escopo da herança: somente membros? membros + configuração de triagem (modo, confiança,
   fallback, pesos, teto de tokens)? competências dos membros?
2. Precedência: o departamento do cliente sobrescreve o global campo a campo ou só quando vazio?
3. Segurança multi-tenant: membros do departamento global atuando em chamados de um cliente
   concreto precisam respeitar a ACL do cliente (hoje a ACL é resolvida por requisição, não pelo
   resolvedor).
4. Operação: a herança vale no momento da decisão (dinâmica) ou é congelada no cadastro
   (snapshot)?
5. Auditoria: registrar na decisão de triagem qual departamento forneceu cada membro
   (hoje `ticket_assignment_decisions` registra a origem da decisão, não a origem do candidato).

## 3. Pontos de código a alterar (somente estes)

| Arquivo | Mudança |
|---|---|
| `Discovery.Infrastructure/Services/DepartmentTeamResolver.cs` | resolver o departamento global via `InheritFromGlobalId` e compor a lista (próprios + herdados, com precedência decidida) |
| `Discovery.Core/Entities/Department.cs` | se a herança for campo a campo, expor o valor efetivo (ou um método de merge) |
| `Discovery.Core/Cqrs/Departments/Commands/DepartmentCommands.cs` + `DepartmentHandlers.cs` | validação de ciclos (A herda de B que herda de A), de escopo (global não herda de cliente) e de configuração herdada |
| `DiscoveryRMM_Site/src/pages/settings/DepartmentDetailPage.tsx` | indicar visualmente o que é herdado e permitir sobrescrever |
| `Discovery.Migrations` | nenhuma coluna nova é obrigatória; a herança usa `inherit_from_global_id` existente |

## 4. Testes mínimos quando implementar

- departamento de cliente vazio herda os membros do global;
- departamento com membros próprios não mistura os dois (ou mistura conforme a precedência escolhida);
- ciclo de herança é rejeitado na validação;
- departamento global nunca herda de departamento de cliente;
- round-robin e triagem por IA enxergam a mesma lista herdada;
- exclusão/desativação do departamento global não deixa candidatos órfãos.
