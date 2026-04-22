# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-04-22

### Added
- ✨ **Core API**: Discovery RMM Server - gerenciamento centralizado de agents
- ✨ **NATS Integration**: Message broker com autenticação por token
- ✨ **PostgreSQL Support**: Database com pgvector para embeddings de IA
- ✨ **MeshCentral Integration**: Sincronização de dispositivos e controle remoto
- ✨ **AI Chat Service**: Integração com OpenAI/Ollama para análise de tickets
- ✨ **Embeddings**: Busca semântica com pgvector (1536-dim)
- ✨ **Auto-Ticketing**: Motor automático de geração de chamados
- ✨ **Custom Fields**: Sistema flexível de campos customizáveis
- ✨ **API Tokens**: Autenticação com tokens de longa duração
- ✨ **WebSocket (SignalR)**: Comunicação em tempo real com agents
- ✨ **PSADT Integration**: Suporte para scripts de deployment
- ✨ **Object Storage**: Suporte para storage local, MinIO e S3
- ✨ **Reporting**: Gerador de relatórios com templates personalizáveis
- ✨ **Self-Update**: Script de auto-atualização para agent

### Infrastructure
- 🐳 **Docker Support**: Dockerfile para containerização
- 📦 **Linux Installer**: Script de instalação automatizada (bash)
- 🔒 **Self-Signed Certs**: Certificados para acesso interno
- 🌍 **Cloudflare Tunnel**: Suporte para acesso externo seguro
- 📊 **Monitoring**: Health checks e métricas básicas

### Security
- 🔒 Autenticação com JWT + API Keys
- 🔒 NATS com autenticação callout
- 🔒 Variáveis de ambiente para secrets
- 🔒 CORS configurável
- 🔒 Rate limiting em APIs críticas
- 🔒 Validação de input em todos os endpoints

### Testing
- ✅ Testes unitários completos (Discovery.Tests)
- ✅ Mocks para services críticos
- ✅ Fixture factories para dados de teste

### Documentation
- 📖 Deployment guide (Linux/Windows)
- 📖 Configuration guide
- 📖 API documentation
- 📖 MeshCentral integration guide
- 📖 Contributing guidelines

### Breaking Changes
- Nenhuma (primeira release estável)

---

## Próximas Versões (Planejadas)

### [1.1.0] - Planejado
- [ ] Kubernetes support
- [ ] Multi-tenant isolation
- [ ] Advanced audit logging
- [ ] Database replication

### [2.0.0] - Roadmap
- [ ] GraphQL API
- [ ] Real-time dashboards (WebGL)
- [ ] Distributed agents
- [ ] Plugin system

---

## Guidelines

### Changelog Format

```markdown
### Added
- ✨ Nova funcionalidade

### Changed
- 🔄 Mudança em funcionalidade existente

### Fixed
- 🐛 Correção de bug

### Deprecated
- ⚠️ Feature será removida em versão futura

### Removed
- ❌ Feature removida

### Security
- 🔒 Correção de segurança
```

### Issue Linking

```markdown
Closes #123
Fixes #456
Related to #789
```

### Version Bumping Rules

| Tipo | MAJOR | MINOR | PATCH |
|------|-------|-------|-------|
| Breaking Change | ✅ | | |
| Nova Feature | | ✅ | |
| Bugfix | | | ✅ |
| Hotfix Crítico | | | ✅ |
| Prerelease | | | -alpha/-beta |

---

### Header Format

```markdown
## [X.Y.Z] - YYYY-MM-DD

[Unreleased], [YYYY-MM-DD] (Past)
```

### Keep Last 3 Versions

Versões antigas são arquivadas em `docs/CHANGELOG_ARCHIVE.md`.

---

Última atualização: 2026-04-22
