# Plano de Implementacao - Autenticacao NATS por Agent (Multi-tenant)

## Objetivo
Implementar autenticacao e autorizacao no NATS para garantir isolamento forte entre agentes, de forma que cada agent se comunique apenas com o servidor e com os dados do seu proprio escopo (Client -> Site -> Agent), impedindo acesso cruzado entre clientes/sites/agentes.

## Contexto Atual (Projeto)
- Ja existe autenticacao de agent na API via token (bootstrap inicial).
- NATS esta sem credenciais no startup da API e sem ACL por tenant/site/agent.
- Subjects atuais nao carregam escopo completo de tenant.
- Dominio ja possui hierarquia Client -> Site -> Agent.

## Decisoes Arquiteturais
1. Modelo principal de identidade no NATS: NKey/JWT por agent.
2. Broker NATS compartilhado entre multiplos clientes (multi-tenant).
3. Escopo da entrega: backend + aplicacao agent.
4. Usuario/senha padrao: permitido apenas temporariamente para componentes internos do servidor (nao para identidade de agent).

## Requisitos Funcionais
1. Cada agent deve assinar apenas comandos do seu proprio subject.
2. Cada agent deve publicar apenas heartbeat/resultado/hardware do seu proprio subject.
3. Nenhum agent pode publicar/assinar subject de outro agent, mesmo no mesmo site.
4. Nenhum agent pode acessar dados de outro site no mesmo client.
5. Nenhum agent pode acessar dados de outro client no mesmo broker.
6. Backend deve emitir credenciais NATS de curta duracao para agents autenticados.
7. Backend deve suportar renovacao de credenciais sem downtime significativo.

## Modelo de Subject (Canonico)
Padrao recomendado:
- tenant.{clientId}.site.{siteId}.agent.{agentId}.command
- tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat
- tenant.{clientId}.site.{siteId}.agent.{agentId}.result
- tenant.{clientId}.site.{siteId}.agent.{agentId}.hardware

Observacao:
- Evitar wildcard amplo para agentes.
- Subjects de dashboard/eventos devem carregar metadados de escopo para filtragem segura.

## Fases de Implementacao

### Fase 0 - Contratos e Baseline
1. Definir oficialmente o formato de subject com Client/Site/Agent.
2. Definir claims de JWT/NATS por agent:
   - sub: agentId
   - meduza_client_id: clientId
   - meduza_site_id: siteId
   - permissoes de publish/subscribe
   - exp curto (ex.: 5-15 min) + renovacao
3. Definir feature flags de migracao (legacy + novo subject por janela curta).

### Fase 1 - Hardening Imediato do Backend
1. Remover qualquer confianca em agentId vindo de payload onde ja exista identidade autenticada.
2. Validar ownership em resultado de comando:
   - command.AgentId deve ser igual ao agent autenticado.
3. Revisar heartbeat/realtime para impedir spoofing entre agentes.
4. Logar e auditar tentativas de acesso cruzado.

### Fase 2 - API de Credenciais NATS
1. Criar endpoint autenticado para emissao de credencial NATS por agent:
   - bootstrap com AgentToken atual da API.
2. Implementar servico de emissao JWT/NATS com assinatura por chave da conta NATS.
3. Criar endpoint de renovacao de credencial com overlap de validade para reconexao segura.
4. Registrar auditoria de emissao/renovacao/revogacao.

### Fase 3 - ACL no Broker NATS
1. Configurar autorizacao por JWT/NKey no broker.
2. Aplicar permissoes estritas por subject:
   - Agent publica apenas heartbeat/result/hardware do proprio namespace.
   - Agent assina apenas command do proprio namespace.
3. Manter usuario/senha padrao apenas para servicos internos temporarios.
4. Bloquear wildcard amplo para principals de agent.

### Fase 4 - Refatoracao da Camada de Mensageria
1. Atualizar interfaces e implementacoes de mensageria para usar subject canonico.
2. Propagar clientId/siteId/agentId no fluxo:
   - controller -> service -> messaging -> handlers.
3. Suportar dual-routing temporario por feature flag durante migracao.

### Fase 5 - Atualizacao do Agent
1. No startup, agent deve:
   - usar AgentToken para solicitar credencial NATS.
   - conectar no NATS com JWT/NKey recebido.
2. Implementar refresh antes da expiracao.
3. Implementar reconexao com backoff.
4. Remover credenciais fixas compartilhadas do agent.

### Fase 6 - Rollout Controlado
1. Homologar com dois clientes reais no mesmo broker para validar isolamento.
2. Ativar por tenant com feature flag.
3. Monitorar rejeicoes de permissao e falhas de autenticacao NATS.
4. Remover legado (subject antigo) apos estabilizacao.

## Estrategia de Usuario/Senha Padrao (Temporaria)
Aplicar apenas para componentes internos do servidor que ainda nao estiverem migrados para JWT/NKey:
- Exemplo de usuario: meduza_internal
- Exemplo de senha padrao inicial: Meduza@1234

Importante:
- Trocar esse valor imediatamente em ambiente real por segredo seguro.
- Nunca usar essa credencial como identidade de agent.
- Armazenar segredo em variavel de ambiente/secret manager.

## Arquivos Impactados (Referencia)
- src/Meduza.Api/Program.cs
- src/Meduza.Infrastructure/Messaging/NatsAgentMessaging.cs
- src/Meduza.Api/Services/NatsBackgroundService.cs
- src/Meduza.Api/Services/NatsSignalRBridge.cs
- src/Meduza.Api/Controllers/AgentAuthController.cs
- src/Meduza.Api/Middleware/AgentAuthMiddleware.cs
- src/Meduza.Infrastructure/Services/AgentTokenAuthService.cs
- src/Meduza.Api/Controllers/AgentInstallController.cs
- src/Meduza.Infrastructure/Repositories/AgentRepository.cs
- src/Meduza.Infrastructure/Repositories/CommandRepository.cs
- src/Meduza.Api/appsettings.json
- src/Meduza.Api/appsettings.Development.json

## Criterios de Aceite
1. Agent A nao consegue assinar/publicar subject de Agent B.
2. Agent de Site 1 nao acessa mensagens de Site 2.
3. Agent de Client X nao acessa mensagens de Client Y.
4. Emissao e renovacao de credenciais funcionam com token valido.
5. Token expirado/revogado e rejeitado na emissao de credencial NATS.
6. Fluxo comando -> execucao -> resultado permanece funcional apos migracao.
7. Tentativas de spoofing sao rejeitadas e auditadas.

## Riscos e Mitigacoes
1. Risco: quebra de compatibilidade com agentes legados.
   Mitigacao: dual-routing temporario + rollout por feature flag.
2. Risco: expiracao agressiva de JWT causar desconexao frequente.
   Mitigacao: refresh antecipado e overlap de validade.
3. Risco: configuracao incorreta de ACL no broker.
   Mitigacao: testes negativos automatizados de publish/subscribe cruzado.

## Entregaveis
1. Documento de contrato de subjects e claims.
2. Endpoint de emissao/renovacao de credenciais NATS.
3. Configuracao de broker com ACL por agent.
4. Camada de mensageria migrada para escopo Client/Site/Agent.
5. Agent atualizado para fluxo de credencial dinamica.
6. Suite de testes de isolamento multi-tenant.
