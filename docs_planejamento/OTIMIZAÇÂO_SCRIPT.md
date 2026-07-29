Plano de melhoria recomendado
P0 — Corrigir antes do próximo deploy
Unificar o script instalado e o template.
Corrigir carregamento das variáveis sem source inseguro.
Fazer o update executar sempre como discovery-api.
Garantir que DISCOVERY_GIT_REPO e branch sejam carregados do EnvironmentFile.
Validar o branch configurado contra o branch remoto.
Corrigir o parser/handshake NATS do frontend, já implementado localmente.
Validar ACL do agent com remote.session.>, já corrigido localmente.
Adicionar smoke test do WebSocket NATS.
Verificar XKey por derivação de chave pública.
P1 — Robustez do deploy
Build em worktree temporário.
Publicação atômica por release.
Snapshot de env antes de alterações.
Rollback completo de binário, Site e env.
Manifesto de versão.
Health/readiness check com timeout e retries.
Validação do Nginx antes do reload.
Verificação de PID anterior/novo.
P2 — Segurança e manutenção
Checksum dos binários baixados.
Versões pinadas de nk, nats CLI, Node e .NET.
Remoção dos curl -s sem validação.
Logs estruturados com correlation ID.
Redação de segredos nos logs.
Testes shell automatizados com bats ou equivalente.
shellcheck obrigatório no CI.
Testes de instalação em VM limpa e de update/rollback em VM descartável.
Próxima execução recomendada
Antes de aplicar novas mudanças, eu recomendo executar nesta ordem:

Corrigir o handshake NATS e ACL, já preparados localmente.
Corrigir o fluxo do script de update.
Criar testes de smoke pós-deploy.
Commitar essas melhorias.
Fazer push para as branches corretas.
Executar o update somente pelo serviço/script oficial.
Validar API, NATS, Nginx, Site e agent.
