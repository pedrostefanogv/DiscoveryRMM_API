# Plano de Correcao e Implementacao da Integracao MeshCentral

Data: 2026-04-16

## Objetivo

Corrigir a integracao do Discovery com o MeshCentral para que o controle remoto funcione de forma estavel, auditavel e aderente ao modelo operacional desejado:

- cada usuario do sistema deve ter seu proprio usuario no MeshCentral;
- a sessao remota deve abrir embutida no sistema;
- uma conta tecnica separada deve existir apenas para provisionamento, reconciliacao e operacoes administrativas de backend.

## Resumo Executivo

O projeto ja possui uma base relevante de integracao com MeshCentral, incluindo:

- geracao de URLs de embed para usuario e agent;
- sincronizacao de identidades Discovery -> MeshCentral;
- reconciliacao de grupos por site;
- resolucao de direitos MeshCentral a partir das roles locais;
- endpoints administrativos para backfill e diagnostico parcial;
- persistencia de vinculos de usuario e site no banco.

O principal problema arquitetural identificado e que o codigo atual mistura dois fluxos que precisam ser separados:

1. fluxo web de sessao remota para navegador;
2. fluxo administrativo/backend para control.ashx e operacoes de provisionamento.

Hoje o embed web esta baseado em geracao de auth na URL, enquanto o padrao mais alinhado ao MeshCentral e ao TacticalRMM para acesso embutido por usuario e o uso de login token com parametro login.

Em paralelo, o backend administrativo ainda tem pontos acoplados ao usuario fixo admin, o que piora seguranca, auditoria e operacao.

## Modelo Alvo

### 1. Identidade por usuario

Cada usuario humano do Discovery que possa usar controle remoto deve possuir:

- MeshCentralUserId
- MeshCentralUsername
- direitos efetivos por site/grupo calculados a partir das roles locais

Esse modelo deve ser a base de auditoria e autorizacao da sessao remota no MeshCentral.

### 2. Conta tecnica separada

Uma conta tecnica dedicada deve ser usada apenas para:

- chamadas administrativas ao control.ashx;
- criacao/atualizacao de usuarios remotos;
- criacao e reconciliacao de grupos;
- atribuicao e remocao de memberships/rights;
- diagnostico operacional.

Essa conta nao deve ser usada como identidade da sessao remota de usuarios finais, salvo excecoes explicitas e controladas.

### 3. Embed web por login token

O navegador deve receber URL embutivel do MeshCentral usando login token por usuario, com:

- allowLoginToken habilitado no MeshCentral;
- allowFraming habilitado no MeshCentral;
- token gerado no backend;
- parametro login na URL;
- gotonode ou gotodevicename conforme contexto;
- viewmode e hide controlados pelo backend.

### 4. Grupos por site como fonte de verdade

O desenho atual baseado em grupos por site deve ser mantido. O TacticalRMM serve como referencia para autenticacao e health-check, mas nao como modelo completo de permissao por no.

## Diagnostico do Estado Atual

### Componentes ja existentes no projeto

- src/Discovery.Infrastructure/Services/MeshCentralEmbeddingService.cs
- src/Discovery.Infrastructure/Services/MeshCentralApiService.cs
- src/Discovery.Infrastructure/Services/MeshCentralIdentitySyncService.cs
- src/Discovery.Infrastructure/Services/MeshCentralPolicyResolver.cs
- src/Discovery.Infrastructure/Services/MeshCentralGroupPolicySyncService.cs
- src/Discovery.Infrastructure/Services/MeshCentralProvisioningService.cs
- src/Discovery.Api/Controllers/MeshCentralController.cs
- src/Discovery.Api/Controllers/AgentAuthController.cs
- src/Discovery.Core/Configuration/MeshCentralOptions.cs

### Achados principais

1. O sistema ja tenta operar no modelo usuario-por-usuario, mas o fluxo de embed e o fluxo administrativo ainda nao estao claramente separados.
2. O servico de embed atual gera auth diretamente na URL, enquanto a referencia mais solida para browser embed usa login token.
3. O servico administrativo ainda depende de usuario fixo admin em pontos sensiveis.
4. A configuracao atual ainda nao explicita de forma suficiente a conta tecnica de integracao nem o contrato de autenticacao do embed.
5. Ha pouca ou nenhuma cobertura automatizada focada em MeshCentral em Discovery.Tests.
6. Parte do problema pode estar fora da API, no proprio MeshCentral ou no proxy reverso, especialmente framing, TLS, dominio e configuracao de token.

## Referencias Externas Utilizadas

### MeshCentral

Capacidades relevantes confirmadas:

- suporte a allowLoginToken para login automatico por token;
- suporte a allowFraming para embed em iframe;
- uso de URLs com parametro login para sessao web;
- uso de auth/control.ashx em fluxos administrativos e de WebSocket;
- controle de permissao baseado no usuario MeshCentral e nos rights do grupo/dispositivo.

### TacticalRMM

Padroes relevantes aproveitaveis:

- modelo hibrido com conta tecnica + usuarios sincronizados;
- uso de login token no navegador para abrir control/terminal/files;
- health-check operacional da integracao MeshCentral;
- automacao inicial de setup e validacao do grupo/dispositivo.

Padroes que nao devem ser copiados sem adaptacao:

- sincronizacao de permissao por no como fonte primaria;
- simplificacoes operacionais ligadas ao stack e ao modelo de tenancy do TacticalRMM.

## Plano de Implementacao

### Fase 1 - Diagnostico operacional e contrato da integracao

Objetivo:
Fechar as ambiguidades operacionais antes de alterar o codigo.

Entregas:

- checklist do MeshCentral obrigatorio para producao;
- validacao de allowLoginToken e allowFraming;
- validacao de BaseUrl/PublicBaseUrl/certificado/proxy;
- validacao de dominio MeshCentral;
- validacao da conta tecnica e seus rights administrativos;
- definicao formal do contrato de configuracao da integracao.

Bloqueios resolvidos nesta fase:

- distinguir falha de codigo de falha de configuracao do MeshCentral;
- evitar migrar a autenticacao sem garantir suporte do servidor.

### Fase 2 - Refatoracao da configuracao MeshCentral

Objetivo:
Explicitar em configuracao os dois fluxos: navegador e backend administrativo.

Mudancas esperadas:

- evoluir MeshCentralOptions para representar com clareza:
  - usuario tecnico da integracao;
  - segredo usado para gerar login token do embed;
  - modo de autenticacao administrativa do backend;
  - dominio e URLs publicas;
  - compatibilidade temporaria com LoginKeyHex, se necessario.

Resultado esperado:

- fim da dependencia implicita de admin;
- menor ambiguidade de manutencao;
- melhor documentacao e observabilidade.

### Fase 3 - Correcao do fluxo de embed web por usuario

Objetivo:
Fazer o embed web abrir a sessao remota como o usuario MeshCentral correspondente ao usuario autenticado no Discovery.

Mudancas esperadas:

- ajustar MeshCentralEmbeddingService para gerar login token e URL com parametro login;
- manter viewmode/hide/gotonode sob controle do backend;
- fazer MeshCentralController falhar de forma clara quando o usuario nao estiver sincronizado com MeshCentral;
- preservar restricoes de escopo por client/site/agent.

Beneficios:

- auditoria correta no MeshCentral;
- menor risco de elevacao de privilegio por sessao compartilhada;
- alinhamento com o comportamento esperado do produto.

### Fase 4 - Correcao do fluxo administrativo do backend

Objetivo:
Eliminar o hardcode de admin nas chamadas ao control.ashx.

Mudancas esperadas:

- refatorar MeshCentralApiService para usar a conta tecnica configurada;
- melhorar telemetria e mensagens de erro;
- distinguir falhas de:
  - usuario tecnico invalido;
  - token/chave invalida;
  - dominio incorreto;
  - grupo inexistente;
  - drift de membership/rights.

Beneficios:

- menor risco operacional;
- melhor suporte e troubleshooting;
- possibilidade real de rotacionar credenciais sem quebrar o embed por usuario.

### Fase 5 - Endurecimento da sincronizacao de identidade e direitos

Objetivo:
Consolidar o modelo usuario-por-usuario ja existente.

Mudancas esperadas:

- reforcar validacao de usernames remotos;
- tratar de forma idempotente criacao, atualizacao, revogacao e reconciliacao;
- melhorar o mapeamento de roles locais para rights MeshCentral;
- tratar cenarios de drift parcial por site/grupo.

Importante:

- preservar a arquitetura local baseada em cliente/site/grupo;
- nao migrar para um modelo centrado em node-level sync como regra principal.

### Fase 6 - Revisao do fluxo agent-scoped

Objetivo:
Decidir explicitamente como o endpoint agent-auth deve operar no novo modelo.

Alternativas:

1. manter como excecao controlada com conta tecnica;
2. restringir a casos especificos de suporte operacional;
3. descontinuar se o caso de uso nao justificar a superficie adicional.

Recomendacao inicial:

- manter apenas se houver uso real validado;
- caso mantido, registrar auditoria separada e deixar a excepcao documentada.

### Fase 7 - Diagnostico e health-check operacional

Objetivo:
Adicionar um mecanismo tecnico de validacao da integracao antes e depois do rollout.

Entregas sugeridas:

- endpoint protegido ou comando administrativo de diagnostico;
- validacao de conexao ao control.ashx;
- validacao de geracao de login token;
- validacao de resolucao do mesh do site;
- validacao do usuario remoto do solicitante;
- validacao da URL de embed gerada.

Inspiracao reaproveitada do TacticalRMM:

- comando de check da integracao;
- verificacao objetiva de token, URL e group binding.

### Fase 8 - Testes automatizados

Objetivo:
Cobrir os pontos criticos hoje sem testes dedicados.

Cobertura minima recomendada:

- geracao de URL de embed por usuario com login token;
- validacao de viewmode/hide/gotonode;
- uso da conta tecnica no fluxo administrativo;
- sincronizacao de identidade em create/update/deprovision;
- reconciliacao de rights e memberships;
- falhas de configuracao e mensagens de erro;
- fallback de provisioning/agent embed quando aplicavel.

Observacao:

- hoje existem testes adjacentes de autenticacao e sessao remota, mas praticamente nao ha cobertura direta de MeshCentral.

### Fase 9 - Documentacao e rollout controlado

Objetivo:
Garantir implantacao segura e repetivel.

Entregas:

- atualizar docs/MESHCENTRAL.md com o fluxo real de integracao;
- atualizar docs/CONFIGURATION.md com o novo contrato de configuracao;
- documentar checklist do MeshCentral e troubleshooting;
- documentar sequencia de backfill e reconcile.

Sequencia de rollout:

1. validar configuracao MeshCentral e diagnostico;
2. implantar codigo com dry-run habilitado;
3. executar backfill de identidade em modo de leitura;
4. revisar relatorio de drift;
5. ativar applyChanges em ambiente controlado;
6. validar sessao embutida com usuario comum e usuario limitado;
7. expandir por ambiente/site de forma progressiva.

## Dependencias Tecnicas

### Dependencias de arquitetura

- Fase 1 bloqueia as fases 3 e 4.
- Fase 2 deve ser concluida antes da refatoracao principal dos servicos.
- Fases 3 e 4 podem avancar em paralelo apos a definicao do novo contrato de configuracao.
- Fase 8 deve ser concluida antes do rollout final.
- Fase 9 depende da estabilizacao das fases 3 a 8.

### Dependencias operacionais

- acesso ao MeshCentral com privilegios administrativos;
- confirmacao de configuracao do servidor/proxy;
- ambiente de homologacao para validar embed em iframe;
- massa de usuarios/sites/agentes para backfill controlado.

## Checklist de Verificacao

### Validacao de servidor MeshCentral

- allowLoginToken habilitado
- allowFraming habilitado
- URL publica valida
- certificado TLS coerente com a URL publica
- dominio configurado corretamente
- proxy reverso compativel com iframe e WebSocket

### Validacao de backend Discovery

- conta tecnica configurada e funcional
- geracao de login token por usuario funcionando
- conexao administrativa ao control.ashx funcionando
- resolucao de mesh por site funcionando
- sincronizacao de usernames e memberships funcionando

### Validacao funcional

- usuario com permissao abre sessao embutida com sucesso
- usuario sem permissao nao abre sessao
- auditoria do MeshCentral registra o usuario correto
- repair/provisioning nao quebra apos a mudanca
- reconcile/backfill gera relatorio coerente antes do apply

### Validacao de testes

- suite de Discovery.Tests executada
- novos testes de MeshCentral executados
- cenarios de falha de configuracao cobertos

## Arquivos Prioritarios para Implementacao

- src/Discovery.Core/Configuration/MeshCentralOptions.cs
- src/Discovery.Infrastructure/Services/MeshCentralEmbeddingService.cs
- src/Discovery.Infrastructure/Services/MeshCentralApiService.cs
- src/Discovery.Infrastructure/Services/MeshCentralIdentitySyncService.cs
- src/Discovery.Infrastructure/Services/MeshCentralPolicyResolver.cs
- src/Discovery.Infrastructure/Services/MeshCentralGroupPolicySyncService.cs
- src/Discovery.Infrastructure/Services/MeshCentralProvisioningService.cs
- src/Discovery.Api/Controllers/MeshCentralController.cs
- src/Discovery.Api/Controllers/AgentAuthController.cs
- src/Discovery.Api/Program.cs
- docs/MESHCENTRAL.md
- docs/CONFIGURATION.md

## Decisoes Registradas

- Modelo alvo aprovado: usuario por usuario no MeshCentral para sessao remota.
- Sessao remota deve permanecer embutida no sistema.
- Conta tecnica deve existir, mas apenas para backend/provisionamento/reconciliacao.
- O TacticalRMM deve servir como referencia de autenticacao e operacao, nao como copia integral do modelo de permissao.
- O desenho local por client/site/grupo deve ser preservado como fonte de verdade.

## Escopo Fora Desta Entrega

Itens nao incluidos neste plano inicial:

- SSO/OIDC completo com MeshCentral;
- ingestao de eventos MeshCentral para timeline/auditoria ampliada;
- redesign amplo do agente Discovery;
- substituicao total da camada de suporte remoto.

## Recomendacao Final

A correcao nao deve ser tratada como um simples ajuste de URL. O problema e de separacao de responsabilidades entre sessao web, autenticacao administrativa, sincronizacao de identidade e configuracao do proprio MeshCentral.

O caminho mais seguro e implementar o modelo hibrido:

- navegador usando login token por usuario;
- backend usando conta tecnica dedicada;
- grupos por site como base de autorizacao;
- diagnostico operacional antes do rollout;
- testes automatizados antes da ativacao ampla.