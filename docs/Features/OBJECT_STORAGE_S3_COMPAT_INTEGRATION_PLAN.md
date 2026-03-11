## Plan: Object Storage Privado Multi-Entidade

Implementar uma camada de armazenamento de objetos desacoplada de provedor (S3 API), com bucket global e isolamento lógico por prefixo (global/client/{clientId}/...), mantendo objetos privados e download por URL pré-assinada sob demanda via redirect 302. A estratégia reduz acoplamento ao filesystem atual, permite AWS S3/Cloudflare R2/Oracle S3-compatível sem mudança de domínio, e reaproveita padrões já existentes de DI/options/resolução hierárquica.

**Steps**
1. Fase 0 - Baseline e mapeamento de pontos de integração (bloqueia fases seguintes)
2. Identificar e documentar todos os fluxos de arquivo cobertos na Fase 1: relatórios (execução, retenção, download normal e stream) e anexos de tickets/notas.
3. Confirmar contratos e campos persistidos que hoje acoplam ao disco local (ResultPath, nomes de arquivo e content type), incluindo dependências em serviços de retenção e endpoints de download.
4. Fase 1 - Modelo e abstrações de storage (depende da fase 0)
5. Criar contrato de domínio para object storage (upload, download por stream opcional, delete, existência, URL pré-assinada de download, metadados mínimos).
6. Definir esquema de chave de objeto com isolamento lógico por prefixo: global/{area}/... para dados globais e clients/{clientId}/{area}/... para dados por cliente.
7. Definir convenção de áreas na chave (ex.: reports, tickets, notes) para permitir políticas de retenção e auditoria sem depender de nome de arquivo.
8. Fase 2 - Configuração e seleção de provider (depende da fase 1)
9. Introduzir ObjectStorageOptions com provider ativo, endpoint S3 compatível, credenciais, bucket global, região, flags de path-style, e TTL padrão de URL pré-assinada (24h).
10. Registrar providers via DI usando factory/estratégia, com implementação base S3 API para AWS S3, Cloudflare R2 e Oracle S3 compatível por configuração (sem alterar código de domínio).
11. Manter provider local como fallback opcional somente para desenvolvimento, preservando paridade de contrato.
12. Fase 3 - Persistência e migração de metadados (depende da fase 2)
13. Evoluir metadados de arquivos para armazenar StorageProvider, Bucket, ObjectKey, ContentType, SizeBytes, Checksum/ETag (quando disponível) e manter compatibilidade temporária com ResultPath durante migração.
14. Criar migration para tabela de execuções de relatório e entidades de anexo (tickets/notas) com novos campos de object storage.
15. Planejar backfill progressivo: novos arquivos já gravam em object storage; arquivos legados continuam válidos até job de migração opcional copiar do disco para bucket.
16. Fase 4 - Integração dos fluxos de negócio (depende da fase 3)
17. Refatorar geração de relatórios para salvar em object storage (stream), persistindo metadados de objeto em vez de caminho físico local.
18. Adaptar anexos de tickets/notas para upload no object storage usando chave por cliente e área.
19. Substituir endpoints de download para gerar URL pré-assinada privada e responder com redirect 302; manter autorização da API antes de emitir a URL.
20. Tratar fallback de leitura durante transição: se metadado novo inexistente e houver ResultPath, usar caminho legado até concluir migração.
21. Fase 5 - Retenção, segurança e observabilidade (paralelo parcial com fase 4 após contrato estável)
22. Atualizar serviços de retenção para remover objetos por chave/prefixo no bucket em vez de File.Delete.
23. Definir política de privacidade padrão: bucket sem ACL pública, URLs assinadas somente para download, validade 24h configurável por ambiente.
24. Incluir auditoria: registrar emissão de URL assinada (entidade, usuário/agente solicitante, chave, expiração, provider).
25. Adicionar métricas e logs estruturados para upload, geração de URL, falhas de provider e latência.
26. Fase 6 - Compatibilidade S3 API multi-vendor e hardening (depende da fase 4 e 5)
27. Validar comportamento com AWS S3, Cloudflare R2 e Oracle S3 compatível apenas por troca de configuração (endpoint/credenciais/path-style/signature).
28. Garantir tratamento de diferenças comuns de provedores S3 compatíveis (URL style, região, assinatura, headers) dentro do provider.
29. Documentar matriz de compatibilidade e limitações conhecidas por vendor.

**Relevant files**
- src/Meduza.Api/Program.cs - registrar options, provider factory, serviços de storage e fallback local para dev.
- src/Meduza.Api/appsettings.json - seção ObjectStorage com bucket global, endpoint S3 compatível e TTL default de 24h.
- src/Meduza.Api/appsettings.Development.json - configuração local/dev e credenciais de exemplo.
- src/Meduza.Api/Controllers/ReportsController.cs - trocar download direto por emissão/redirect 302 para URL pré-assinada.
- src/Meduza.Api/Controllers/TicketsController.cs - integrar upload/download de anexos ao storage abstrato.
- src/Meduza.Api/Controllers/NotesController.cs - integrar upload/download de anexos ao storage abstrato.
- src/Meduza.Api/Services/ReportRetentionBackgroundService.cs - retenção por object key/prefixo.
- src/Meduza.Infrastructure/Services/ReportService.cs - substituir gravação/leitura local por object storage e metadados.
- src/Meduza.Core/Entities/ReportExecution.cs - novos campos de storage (provider, bucket, key etc.) e compatibilidade transitória.
- src/Meduza.Core/Interfaces/IConfigurationResolver.cs - referência para reaproveitar escopo client/site ao compor object key.
- src/Meduza.Infrastructure/Services/ConfigurationResolver.cs - referência para resolução de escopo efetivo quando necessário.
- src/Meduza.Migrations/Migrations/ - nova migration para campos de object storage e índices por key/client.

**Verification**
1. Build completo: executar dotnet build Meduza.slnx.
2. Teste funcional relatórios: gerar relatório, validar metadados persistidos (bucket/objectKey) e confirmar download via endpoint com redirect 302 para URL assinada.
3. Teste funcional anexos tickets/notas: upload, persistência de metadados, download por redirect 302 e expiração após 24h.
4. Teste de segurança: confirmar que URL direta sem assinatura não acessa objeto; confirmar bucket privado sem listagem pública.
5. Teste de compatibilidade: rodar mesma suíte com configuração AWS S3, Cloudflare R2 e Oracle S3 compatível alterando apenas settings.
6. Teste de retenção: simular expiração, executar serviço de retenção e validar remoção no bucket + consistência no banco.
7. Teste de regressão legada: validar arquivos antigos (com ResultPath) continuam acessíveis durante fase de transição.

**Decisions**
- Escopo Fase 1 inclui relatórios e anexos de tickets/notas.
- Modelo multi-entidade: bucket global único com isolamento por prefixo por cliente + prefixo separado para dados globais.
- Download deve ser via redirect 302 para URL pré-assinada, não payload JSON.
- TTL padrão da URL pré-assinada: 24 horas (configurável por ambiente).
- Compatibilidade alvo: AWS S3, Cloudflare R2, Oracle S3 compatível via S3 API.

**Further Considerations**
1. Migração de legado: recomendação é rollout em duas etapas (gravação nova em object storage imediatamente, backfill assíncrono posterior), para evitar downtime.
2. Custo/performance: avaliar habilitar compressão e lifecycle por prefixo (reports/, tickets/, notes/) para otimizar custo de storage.
3. Governança: definir convenção de nomenclatura de object keys imutável antes de entrar em produção para evitar reindexação posterior.
