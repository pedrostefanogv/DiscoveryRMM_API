# Plano de Acao MeshCentral - Meduza

## Objetivo
Consolidar o status real da integracao MeshCentral no projeto Meduza, incluindo entregas concluidas, validacoes realizadas e o que falta implementar para fechar as fases de sync, governanca e autenticacao unificada.

## O Que Foi Feito

### 1) Base de integracao e configuracao
- Adicionado bloco de configuracao MeshCentral no appsettings (incluindo habilitacao da integracao).
- Registrados servicos MeshCentral no DI da API.
- Criadas opcoes de execucao para instalacao do agent com modo configuravel.

### 2) Embedding com token (Fase 1)
- Implementado servico de geracao de URL de embedding com token assinado.
- Criado endpoint para gerar URL de embed para suporte remoto por agent.
- Criado endpoint de preparacao para embed por usuario (base para autenticacao unificada futura).

### 3) Provisionamento de grupo e link de instalacao (Fase 2)
- Implementado fluxo WebSocket com autenticacao para:
  - listar grupos (meshes),
  - criar grupo (createmesh),
  - gerar link de convite (createInviteLink).
- Integrado fluxo no endpoint de deploy token, com fallback para modo por template quando o fluxo real nao estiver disponivel.
- Implementadas instrucoes de instalacao para Windows/Linux com suporte a:
  - modo background,
  - modo interactive,
  - padrao atual em background.

### 4) Persistencia para sync
- Persistencia de mapeamento MeshCentral por site:
  - meshcentral_group_name,
  - meshcentral_mesh_id.
- Criada migration para incluir colunas e indice em site_configurations.
- Atualizado repositorio de configuracao de site para gravar e atualizar os campos.

### 5) Validacoes executadas
- Build da solucao realizado com sucesso apos os incrementos.
- Testes reais de conectividade e autenticacao WebSocket no MeshCentral (ambiente com certificado autoassinado) realizados.
- Criacao real de grupo cliente/site validada no servidor MeshCentral e meshid obtido.

## O Que Falta

### Prioridade Alta (proxima entrega)
1. Sync de usuarios Meduza -> MeshCentral
- Criar usuario no MeshCentral quando usuario elegivel for provisionado no Meduza.
- Atualizar/desativar/remover usuario no MeshCentral conforme ciclo de vida local.
- Persistir identificador remoto do usuario para reconciliacao.

2. Vinculo de permissoes usuario x grupo cliente/site
- Automatizar addmeshuser/removemeshuser por escopo de cliente/site.
- Garantir principio de menor privilegio por tenant.

3. Job de reconciliacao periodica
- Reconciliar usuarios, grupos e vinculacoes.
- Corrigir drift causado por alteracoes manuais fora do Meduza.

### Prioridade Media
4. Ingestao de eventos MeshCentral
- Consumir eventos relevantes para auditoria e timeline de seguranca.
- Registrar eventos criticos no stack de logging do Meduza.

5. Enriquecimento de inventario
- Consultar nodes/deviceinfo para complementar visao operacional no painel.

### Prioridade Estrategica
6. Login token / SSO progressivo
- Evoluir para sessao transparente no embed (sem novo login manual) com token controlado pelo backend.

7. Acoes remotas com governanca
- Expor runcommand/devicepower/device messages sob politicas de permissao, trilha de auditoria e aprovacao quando necessario.

## Decisoes Tecnicas Atuais
- Transporte principal da automacao: WebSocket no control.ashx.
- Organizacao multi-tenant: grupo por client/site.
- Persistencia de vinculo remoto no escopo de site para permitir reconciliacao e suporte operacional.
- Estrategia de resiliencia: fluxo real WebSocket com fallback por template para nao bloquear deploy.
- Modo de instalacao padrao: background (configuravel para interactive).

## Riscos e Cuidados
- Certificado TLS autoassinado exige tratamento controlado por ambiente.
- Credenciais e chaves devem permanecer fora de codigo e com rotacao planejada.
- Operacoes remotas exigem guardrails de seguranca e auditoria antes de exposicao ampla.

## Proximos Passos Sugeridos
1. Implementar MeshCentralUserSyncService com persistencia de identificador remoto por usuario.
2. Implementar sincronizacao de membership de grupos por client/site.
3. Criar job de reconciliacao periodica com relatorio de divergencias.
4. Adicionar endpoint administrativo para reconciliacao manual on-demand.
5. Incluir testes de integracao cobrindo create/update/delete de usuario e membership.
