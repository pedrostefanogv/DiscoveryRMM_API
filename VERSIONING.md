# Política de Versionamento - Discovery RMM

## 📌 Semantic Versioning (SemVer)

Discovery RMM segue [Semantic Versioning](https://semver.org/):

```
MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]
X.Y.Z[-rc.1]
```

### Componentes

| Componente | Incrementa | Exemplo | Quando |
|------------|-----------|---------|--------|
| **MAJOR** | X | 1.0.0 → 2.0.0 | Breaking change na API |
| **MINOR** | Y | 1.5.0 → 1.6.0 | Nova feature compatível |
| **PATCH** | Z | 1.0.0 → 1.0.1 | Bugfix |
| **PRERELEASE** | - | 1.0.0-alpha.1 | Versão pré-release |
| **BUILD** | - | 1.0.0+build.123 | Metadados de build |

---

## 🔄 Ciclo de Versão

```
v1.0.0 (Stable)
    ↓
v1.1.0-alpha.1 → v1.1.0-alpha.2 (Desenvolvimento ativo)
    ↓
v1.1.0-beta.1 → v1.1.0-beta.2 (Testes com comunidade)
    ↓
v1.1.0-rc.1 → v1.1.0-rc.2 (Release Candidate - quase pronto)
    ↓
v1.1.0 (Stable Release)
    ↓
v1.1.1 (Hotfix se necessário)
```

---

## 🌳 Branches & Versões

### main (Produção)

- **Branches**: `main`
- **Versão**: Semver estável (vX.Y.Z)
- **Automatizado**: Tags + GitHub Releases
- **Ciclo**: 6-12 semanas entre releases

```
v1.0.0 → v1.0.1 (hotfix) → v1.1.0 → v2.0.0
```

### dev (Próxima Release)

- **Branches**: `dev`, `feature/*`, `bugfix/*`
- **Versão**: vX.Y.Z-dev.BUILD
- **Automatizado**: CI/CD em cada push
- **Ciclo**: Contínuo

### beta (Teste com Comunidade)

- **Branches**: `beta`
- **Versão**: vX.Y.Z-beta.N
- **Rebase**: A cada semana de `dev`
- **Ciclo**: 2-4 semanas

### release/vX.Y.Z (RC Phase)

- **Branches**: `release/v*`
- **Versão**: vX.Y.Z-rc.N
- **Duração**: 1-2 semanas
- **Merge**: `main` + merge back `dev`

### lts (Long-Term Support)

- **Branches**: `lts`
- **Versão**: vX.0.Z (bugfix only)
- **Suporte**: 24 meses após release
- **Escopo**: Apenas patches críticos

```
v1.0.0 (LTS) ← v1.0.1, v1.0.2, v1.0.3 (patches apenas)
```

---

## 🔖 Estratégia de Tagging

### Tag Stable

```bash
git tag -a v1.0.0 -m "Release version 1.0.0"
git push origin v1.0.0
```

Triggers GitHub Release na `main`

### Tag Prerelease

```bash
git tag -a v1.1.0-rc.1 -m "Release Candidate 1"
git push origin v1.1.0-rc.1
```

Triggers Draft Release (não publicada automaticamente)

---

## 📋 Checklist de Release

### ✅ Antes de Release

- [ ] Todos os tests passam (CI/CD green)
- [ ] Cobertura ≥ 70%
- [ ] Documentação atualizada
- [ ] CHANGELOG.md preenchido
- [ ] Versão em `.csproj` atualizada
- [ ] Breaking changes documentadas
- [ ] Dependências auditadas (sem vulnerabilidades)

### ✅ Release Estável (main)

```bash
# 1. Criar release branch
git checkout -b release/v1.2.0 dev

# 2. Atualizar versões
# - discovery-api.csproj: <Version>1.2.0</Version>
# - CHANGELOG.md: ## [1.2.0] - 2026-04-22

# 3. Commit
git add .
git commit -m "chore(release): bump version to 1.2.0"

# 4. Merge para main
git checkout main
git merge --no-ff release/v1.2.0 -m "chore(release): v1.2.0"

# 5. Tag
git tag -a v1.2.0 -m "Release version 1.2.0"

# 6. Merge back para dev
git checkout dev
git merge --no-ff release/v1.2.0 -m "chore(release): merge v1.2.0 to dev"

# 7. Push
git push origin main dev v1.2.0

# 8. Delete release branch
git branch -d release/v1.2.0
```

### ✅ Hotfix de Produção

```bash
# 1. Criar hotfix a partir de main
git checkout -b hotfix/v1.2.1 main

# 2. Corrigir bug + atualizar versão

# 3. Merge para main + tag
git checkout main
git merge --no-ff hotfix/v1.2.1 -m "chore(hotfix): v1.2.1"
git tag -a v1.2.1 -m "Hotfix version 1.2.1"

# 4. Merge para dev (IMPORTANTE!)
git checkout dev
git merge --no-ff hotfix/v1.2.1

# 5. Push
git push origin main dev v1.2.1
```

---

## 📊 Exemplo de CHANGELOG

```markdown
# Changelog

## [1.2.0] - 2026-04-22

### Added
- ✨ Sincronização de ACLs do MeshCentral
- ✨ Autenticação com token no NATS
- ✨ Embeddings para IA em PostgreSQL

### Fixed
- 🐛 Timeout na conexão NATS
- 🐛 Encoding de caracteres especiais em relatórios

### Changed
- 🔄 Melhorado performance de consultas

### Deprecated
- ⚠️ Suporte para PostgreSQL < 14 será removido na v2.0.0

### Security
- 🔒 Atualizado bcrypt para v4.2.0
- 🔒 Validação de CORS reforçada

## [1.1.0] - 2026-03-15

### Added
- ✨ API de relatórios avançados
```

---

## 🔐 Versionamento de Segurança

### Breaking Changes

```
feat!: Remover suporte para PostgreSQL < 14

BREAKING CHANGE: PostgreSQL 14+ é agora obrigatório.
Usuários em versões anteriores devem fazer upgrade.
```

Incrementa **MAJOR** version.

### Security Patches

```bash
git tag -a v1.0.2-security -m "Security patch: CVE-2026-XXXX"
```

Incrementa **PATCH** version.

---

## 🚀 Automação

### GitHub Actions

**Triggers automáticos:**

| Evento | Ação |
|--------|------|
| Push em `main` | Build + Test + Create Release |
| Push em `dev` | Build + Test + Upload artifact |
| Push tag `v*` | Build + Publish NuGet |
| PR para `main` | Verificação extra (tests, security, docs) |

---

## 📞 Suporte por Versão

| Versão | Status | Fim de Suporte |
|--------|--------|----------------|
| v1.0.x | LTS | 2027-04-22 |
| v1.1.x | Atual | 2026-10-22 |
| v1.2.x | Beta | 2026-07-22 |
| v2.0.x | Planejado | TBD |

---

## 📚 Referências

- [Semantic Versioning](https://semver.org/)
- [Conventional Commits](https://www.conventionalcommits.org/)
- [Git Flow](https://nvie.com/posts/a-successful-git-branching-model/)
- [Keep a Changelog](https://keepachangelog.com/)
