# Plano de Integração OpenRouter e Provedores Compatíveis

> Status: Plano técnico  
> Data: 2026-04-29

## Objetivo

Suportar oficialmente o OpenRouter e um modo genérico para APIs compatíveis com OpenAI, permitindo configuração segura por cliente/site, descoberta e busca de modelos disponíveis, seleção de modelos de chat e seleção assistida de modelos de embedding compatíveis com o pipeline da base de conhecimento.

## Leitura da Documentação OpenRouter

Pontos relevantes da documentação oficial:

- API base: `https://openrouter.ai/api/v1`.
- Chat: `POST /api/v1/chat/completions`, compatível com o formato OpenAI.
- Embeddings: `POST /api/v1/embeddings`, com suporte a `input` string ou array de strings para batching.
- Autenticação: `Authorization: Bearer <OPENROUTER_API_KEY>`.
- Headers opcionais de atribuição: `HTTP-Referer`, `X-OpenRouter-Title` e `X-OpenRouter-Categories`.
- Modelos: `GET /api/v1/models` retorna `data[]` com `id`, `name`, `description`, `context_length`, `architecture.input_modalities`, `architecture.output_modalities`, `pricing`, `top_provider` e `supported_parameters`.
- OpenRouter normaliza respostas de chat no formato OpenAI, incluindo `choices`, `message`, `delta` no streaming, `usage`, `tool_calls` e `finish_reason` normalizado.
- Modelos de embedding podem ser consultados pela página de modelos com `output_modalities=embeddings`; para integração programática, o serviço deve filtrar o catálogo por capacidade de embedding e manter fallback manual.

## Estado Atual do Projeto

Arquivos centrais:

- `src/Discovery.Core/ValueObjects/AIIntegrationSettings.cs`: configuração global de IA, incluindo `Provider`, `ApiKey`, `BaseUrl`, `ChatModel`, `EmbeddingModel`, `EmbeddingDimensions`, `EmbeddingBaseUrl` e `EmbeddingApiKey`.
- `src/Discovery.Core/ValueObjects/AIIntegrationSettingsOverride.cs`: overrides por cliente/site, hoje sem `ApiKey`, `BaseUrl`, `Provider`, `EmbeddingModel` e flags globais de embedding.
- `src/Discovery.Infrastructure/Services/ConfigurationResolver.cs`: resolve Server -> Client -> Site e aplica overrides.
- `src/Discovery.Infrastructure/Services/OpenAiProvider.cs`: provider de chat compatível com OpenAI, aceita `BaseUrl` e `ApiKey` via `LlmOptions`.
- `src/Discovery.Infrastructure/Services/OpenAiEmbeddingProvider.cs`: provider de embeddings compatível com OpenAI, aceita `baseUrlOverride` e `apiKeyOverride`, mas gera apenas um embedding por chamada.
- `src/Discovery.Infrastructure/Services/AiChatService.cs`: monta `LlmOptions`, injeta RAG e usa a tool `knowledge_search`.
- `src/Discovery.Infrastructure/Services/KnowledgeMcpTool.cs`: gera embedding da consulta e executa busca semântica/keyword.
- `src/Discovery.Api/Services/KnowledgeEmbeddingBackgroundService.cs` e `src/Discovery.Api/Services/KnowledgeEmbeddingQueueBackgroundService.cs`: geram embeddings dos chunks da KB.
- `docs/KNOWLEDGE_EMBEDDING_ANALYSIS.md`: já identifica concorrência duplicada, ausência de batching e necessidade de otimizar o pipeline de embedding.

Constatações:

- O código já tem compatibilidade parcial com OpenRouter porque `BaseUrl` pode apontar para `https://openrouter.ai/api/v1/`.
- Não há provider OpenRouter explícito, nem headers de atribuição do OpenRouter.
- Não há catálogo remoto de modelos, busca de modelos, teste de chave ou validação de capacidade do modelo.
- `ChatModel` já pode ser sobrescrito por cliente/site, mas `ApiKey`, `Provider` e `BaseUrl` são globais.
- O embedding é tratado como espaço vetorial global; mudar dimensão aciona reset via `KnowledgeEmbeddingResetService`.
- A coluna de embedding usa `pgvector` com dimensão fixa, então modelos de embedding diferentes por cliente exigem estratégia de espaços vetoriais, não apenas override simples.

## Lacuna de Segurança Imediata

Antes de habilitar chave por cliente/site, corrigir o tratamento de segredo de embedding:

- `EmbeddingApiKey` deve ser criptografada em `ConfigurationService.ProtectAiJson`.
- `EmbeddingApiKey` deve ser descriptografada em `ConfigurationResolver.GetAISettingsAsync` e `ResolveAI`.
- `EmbeddingApiKey` deve ser mascarada/removida em `ConfigurationsController.SanitizeAiJson` e em qualquer retorno de configuração/resolved config.
- Client/Site devem continuar sem expor segredos no JSON público.
- Adicionar testes cobrindo persistência, retorno sanitizado e resolução com chave de embedding.

## Arquitetura Proposta

### 1. Providers suportados

Manter strings para preservar compatibilidade e definir constantes:

- `openai`: OpenAI direto.
- `openrouter`: OpenRouter oficial.
- `openai-compatible`: modo genérico para APIs compatíveis.

Evolução sugerida:

- Renomear conceitualmente `OpenAiProvider` para provider compatível com OpenAI ou criar `OpenAiCompatibleLlmProvider` mantendo wrapper/alias para evitar quebra.
- Incluir no `LlmOptions` ou em um novo `AiResolvedProviderContext`: `Provider`, `BaseUrl`, `ApiKey`, `Headers`, `TimeoutMs` e identificador estável de usuário/tenant quando aplicável.
- Para `openrouter`, aplicar headers opcionais `HTTP-Referer`, `X-OpenRouter-Title` e `X-OpenRouter-Categories`.
- Para `openai-compatible`, usar endpoints padrão `chat/completions`, `embeddings` e `models`, com fallback para seleção manual quando `/models` não existir.

### 2. Credenciais por cliente/site

Não guardar segredos de tenant dentro de `AIIntegrationSettingsJson` de Client/Site, porque esse JSON hoje é sanitizado e retornado em APIs de configuração. Criar armazenamento próprio de credenciais:

Entidade sugerida: `AiProviderCredential`

- `Id`
- `ScopeType`: `Server`, `Client`, `Site`
- `ClientId`, `SiteId`
- `Provider`: `openai`, `openrouter`, `openai-compatible`
- `BaseUrl`
- `EmbeddingBaseUrl`
- `ApiKeyEncrypted`
- `EmbeddingApiKeyEncrypted`
- `HasApiKey`, `HasEmbeddingApiKey` calculados para DTOs
- `KeyFingerprintHash` para cache/auditoria sem revelar segredo
- `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`

Resolução:

1. Site credential, se existir.
2. Client credential, se existir.
3. Server/global credential.
4. Fallback para `AIIntegrationSettings` global legado durante migração.

APIs sugeridas:

- `GET /api/v{version}/configurations/ai/providers`
- `GET /api/v{version}/configurations/ai/credentials?scopeType=&clientId=&siteId=`
- `PUT /api/v{version}/configurations/ai/credentials`
- `DELETE /api/v{version}/configurations/ai/credentials`
- `POST /api/v{version}/configurations/ai/credentials/test`

As respostas nunca devem retornar chave bruta; apenas flags como `hasApiKey`, `hasEmbeddingApiKey`, provider, base URL e metadados.

### 3. Catálogo e busca de modelos

Criar `IAiModelCatalogService` com implementação para OpenRouter e compatível OpenAI.

Contrato sugerido:

- `ListModelsAsync(scope, provider, capability, search, refresh, ct)`
- `GetModelAsync(scope, provider, modelId, ct)`
- `ValidateModelAsync(scope, provider, modelId, capability, ct)`

DTO sugerido:

- `id`
- `name`
- `description`
- `provider`
- `capabilities`: `chat`, `tools`, `streaming`, `embeddings`, `vision`, `audio`, `file`
- `inputModalities`
- `outputModalities`
- `supportedParameters`
- `contextLength`
- `maxCompletionTokens`
- `pricing`
- `isFree`
- `isRecommendedForChat`
- `isRecommendedForEmbedding`
- `embeddingDimensions`, quando conhecido por metadado, mapeamento local ou teste controlado

OpenRouter:

- Chamar `GET https://openrouter.ai/api/v1/models`.
- Enviar `Authorization` quando houver chave de tenant, porque disponibilidade/preços podem variar por conta.
- Cachear por `provider + baseUrl + keyFingerprint + capability` com TTL de 30 a 60 minutos.
- Permitir `refresh=true` para forçar atualização.
- Buscar localmente em `id`, `name`, `description`, provider e capacidades.

Filtros:

- Chat: `architecture.output_modalities` contém `text`; `input_modalities` contém `text`.
- Tools: `supported_parameters` contém `tools` ou `tool_choice`.
- Embeddings: `architecture.output_modalities` contém `embeddings`; fallback por `id/name` contendo `embedding`/`embed` apenas quando o provedor genérico não expõe metadados completos.
- Modelos incompatíveis devem aparecer com badge/razão, mas não ser selecionáveis para aquele uso.

APIs sugeridas:

- `GET /api/v{version}/configurations/ai/models?scopeType=&clientId=&siteId=&provider=&capability=&search=&refresh=`
- `GET /api/v{version}/configurations/ai/models/{modelId}`
- `POST /api/v{version}/configurations/ai/models/validate`

### 4. Seleção de modelos de chat

Fluxo recomendado:

1. Usuário escolhe provider (`openrouter`, `openai`, `openai-compatible`).
2. Informa/testa chave API no escopo desejado.
3. Sistema lista modelos de chat disponíveis.
4. UI permite pesquisar por nome/id, filtrar grátis, filtrar suporte a tool calling e ordenar por contexto/preço.
5. Ao salvar `ChatModel`, manter o override no `AIIntegrationSettingsOverride` de Client/Site, como já ocorre hoje.
6. Para Knowledge Base com tool calls, alertar quando o modelo selecionado não suportar `tools`/`tool_choice`; permitir modo sem tool call apenas com RAG injetado.

### 5. Seleção de modelos de embedding

Por causa da coluna `vector(N)` e do índice HNSW, o MVP deve tratar `EmbeddingModel` como configuração global do espaço vetorial. Chave de API pode ser por cliente/site, mas o modelo/dimensão de embedding deve continuar global até haver suporte a múltiplos espaços vetoriais.

MVP:

- Listar modelos de embedding compatíveis pelo catálogo.
- Exibir dimensão conhecida/estimada.
- Recomendar inicialmente:
  - `openai/text-embedding-3-small`: custo baixo, 1536 dimensões, bom padrão para RAG.
  - `openai/text-embedding-3-large`: melhor qualidade, 3072 dimensões, exige reset/reprocessamento.
  - `qwen/qwen3-embedding-0.6b`: opção econômica quando disponível no OpenRouter.
- Ao trocar `EmbeddingModel` ou `EmbeddingDimensions`, acionar o fluxo já existente de reset/reprocessamento.
- Validar em runtime se o vetor retornado tem `EmbeddingDimensions`; se não bater, falhar antes de gravar.

Fase avançada para embedding por cliente:

- Introduzir `EmbeddingSpace` com `Id`, `ScopeType`, `ClientId`, `SiteId`, `Provider`, `Model`, `Dimensions` e versão.
- Associar chunks a `EmbeddingSpaceId`.
- Criar índice vetorial por espaço/dimensão ou separar tabela por dimensão.
- Reprocessar apenas artigos do escopo afetado.
- Ajustar `SearchSemanticAsync` para buscar no espaço correto.

### 6. Otimização do pipeline de embedding

Alinhar com `docs/KNOWLEDGE_EMBEDDING_ANALYSIS.md`:

- Adicionar `GenerateEmbeddingsAsync(IReadOnlyList<string> inputs, ...)` em `IEmbeddingProvider`.
- Implementar batching em `OpenAiEmbeddingProvider` usando `input: string[]`.
- Manter o método atual `GenerateEmbeddingAsync` como wrapper para compatibilidade.
- Consolidar ou coordenar os dois workers para evitar concorrência simultânea contra a API.
- Implementar backoff para 429 e 529, respeitando `Retry-After` quando existir.
- Registrar métricas: total de chunks, latência, erros por provider/modelo, custo quando retornado.
- Não persistir embeddings cujo tamanho difira da configuração ativa.

### 7. Tratamento de erros e UX administrativa

Mapear erros comuns para mensagens acionáveis:

- 401: chave inválida ou ausente.
- 402: créditos insuficientes no OpenRouter.
- 404: modelo inexistente ou não compatível com o endpoint usado.
- 429: rate limit; mostrar retry/backoff.
- 529: provider upstream sobrecarregado; sugerir fallback/roteamento.

Adicionar teste de conexão que faça chamada leve:

- Para chat: validar `/models` e, opcionalmente, uma chamada curta a `chat/completions` quando o usuário pedir.
- Para embeddings: gerar embedding de uma frase curta e conferir dimensão.

### 8. Configurações novas em `AIIntegrationSettings`

Campos candidatos:

- `OpenRouterReferer`
- `OpenRouterTitle`
- `OpenRouterCategories`
- `ModelCatalogCacheMinutes`
- `AllowProviderFallbacks`
- `ProviderOrder` para roteamento OpenRouter em embeddings/chat, se necessário.

Evitar no MVP:

- Headers arbitrários livres com segredos, a menos que haja allowlist e criptografia.
- Embedding model diferente por cliente sem `EmbeddingSpace`.

## Plano de Implementação

### Fase 0: Segurança e compatibilidade legado

1. Proteger, descriptografar e mascarar `EmbeddingApiKey`.
2. Adicionar testes para `ConfigurationService`, `ConfigurationResolver` e `ConfigurationsController`.
3. Corrigir sanitização de AI JSON em paths de patch para Client/Site, se necessário.

### Fase 1: OpenRouter oficial

1. Adicionar constantes de provider e defaults: `openrouter` -> `https://openrouter.ai/api/v1/`.
2. Enviar headers `HTTP-Referer`, `X-OpenRouter-Title` e `X-OpenRouter-Categories` quando provider for OpenRouter.
3. Melhorar nomes/logs para `OpenAI-compatible` sem expor chaves.
4. Validar streaming com chunks finais sem `choices` e tool calls em `CompleteAsync`.

### Fase 2: Credenciais por escopo

1. Criar entidade, repositório e migration para `ai_provider_credentials`.
2. Implementar resolver de credenciais Server -> Client -> Site.
3. Integrar `AiChatService`, `KnowledgeMcpTool` e workers de embedding ao novo contexto resolvido.
4. Manter fallback para `AIIntegrationSettingsJson` global.

### Fase 3: Catálogo de modelos

1. Criar `IAiModelCatalogService` e DTOs.
2. Implementar OpenRouter via `/models`.
3. Implementar genérico via `/models` com fallback manual.
4. Adicionar cache por provider/baseUrl/key/capability.
5. Adicionar endpoints de listagem, busca e validação.

### Fase 4: Embeddings robustos

1. Adicionar batching na interface e provider.
2. Validar dimensão do vetor antes de persistir.
3. Aplicar seleção/recomendação de modelos de embedding no admin.
4. Reusar `KnowledgeEmbeddingResetService` quando dimensão mudar.
5. Implementar backoff e métricas.

### Fase 5: Testes e documentação

1. Testes unitários com HTTP fake para OpenRouter `/models`, `/chat/completions` e `/embeddings`.
2. Testes de resolução de credencial por Server/Client/Site.
3. Testes de sanitização de segredos.
4. Testes de filtro de modelos por `chat`, `tools` e `embeddings`.
5. Atualizar `docs/CONFIGURATION.md`, `docs/AUTHENTICATION.md` se houver nova permissão, e `docs/KNOWLEDGE_EMBEDDING_ANALYSIS.md` com o resultado do batching.

## MVP Recomendado

Para entregar valor com baixo risco:

1. Corrigir segurança de `EmbeddingApiKey`.
2. Adicionar provider `openrouter` com base URL e headers oficiais.
3. Adicionar teste de chave OpenRouter por escopo.
4. Criar catálogo de modelos OpenRouter com busca e filtros de chat/embedding/tools.
5. Permitir chave por cliente/site via tabela segura de credenciais.
6. Manter `EmbeddingModel` global no MVP, mas listar e recomendar modelos de embedding com validação de dimensão.
7. Adicionar batching de embeddings.

## Decisões Pendentes

- Permitir embedding model por cliente agora ou adiar até `EmbeddingSpace`.
- Definir se credenciais por site são necessárias no MVP ou se cliente basta.
- Definir política de cache do catálogo quando a chave muda.
- Definir permissões administrativas para ver/testar/sobrescrever credenciais de IA.
- Definir se OpenRouter provider routing (`provider.order`, `allow_fallbacks`, `data_collection`) entra no MVP ou em fase posterior.