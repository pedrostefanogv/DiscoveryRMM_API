# Guia de Contribuição - Discovery RMM

Obrigado por considerar contribuir ao **Discovery RMM**! Este documento fornece diretrizes para colaborar com o projeto.

## 🎯 Princípios

- **Qualidade**: Mantenha altos padrões de código e testes
- **Documentação**: Documente decisões e mudanças significativas
- **Segurança**: Nunca committe credenciais, tokens ou secrets
- **Transparência**: Comunique mudanças arquiteturais ou breaking changes

---

## 🔀 Fluxo de Trabalho

### 1. **Branches por Tipo de Contribuição**

| Branch | Propósito | Base | Destino |
|--------|----------|------|---------|
| `feature/xxx` | Novas funcionalidades | `dev` | `dev` (PR) |
| `bugfix/xxx` | Correção de bugs | `dev` | `dev` (PR) |
| `hotfix/xxx` | Correção crítica em produção | `main` | `main` + `dev` |
| `chore/xxx` | Refatoração, deps, docs | `dev` | `dev` (PR) |
| `release/vX.Y.Z` | Preparação de release | `dev` | `main` (tag) |

### 2. **Criar Feature Branch**

```bash
# Atualize dev
git checkout dev
git pull origin dev

# Crie branch de feature
git checkout -b feature/sua-feature-descritiva
```

### 3. **Commit com Conventional Commits**

Use o padrão [Conventional Commits](https://www.conventionalcommits.org/):

```
<tipo>(<escopo>): <descrição>

<corpo opcional>

<footer opcional>
```

**Tipos:**
- `feat:` Nova funcionalidade
- `fix:` Correção de bug
- `docs:` Mudanças na documentação
- `style:` Formatação, sem alteração de código
- `refactor:` Reorganização de código, sem mudança de comportamento
- `perf:` Melhoria de performance
- `test:` Adição/atualização de testes
- `chore:` Build, deps, CI/CD
- `breaking:` Mudança breaking (adicione `!` após tipo)

**Exemplos:**

```bash
git commit -m "feat(meshcentral): adicionar sincronização de ACLs"
git commit -m "fix(nats): resolver timeout na autenticação"
git commit -m "breaking!: remover suporte para PostgreSQL < 14"
```

### 4. **Pull Request**

1. **Envie PR para `dev`** (não `main`):
   - `dev` → próxima release (beta/alpha)
   - `main` → apenas releases estáveis

2. **Título do PR**:
   ```
   [feature|bugfix|docs]: Descrição clara da mudança
   ```

3. **Descrição**:
   ```markdown
   ## Descrição
   Breve resumo do que foi alterado e por quê.

   ## Tipo
   - [x] Feature
   - [ ] Bugfix
   - [ ] Breaking Change

   ## Checklist
   - [x] Código segue padrões do projeto
   - [x] Testes adicionados/atualizados
   - [x] Documentação atualizada
   - [x] Sem secrets/credenciais no código

   ## Testing
   Como testar a mudança?
   ```

5. **Após aprovação:**
   - Rebase/Squash sua branch
   - Merge para `dev`
   - Delete branch local

---

## 🧪 Testes

### Requisitos

- **Cobertura mínima**: 70% para novas features
- **Testes unitários**: Para lógica crítica
- **Testes de integração**: Para APIs e serviços

### Executar Testes

```bash
# Teste unitário
dotnet test src/Discovery.Tests/Discovery.Tests.csproj

# Com cobertura
dotnet test /p:CollectCoverage=true /p:CoverageFormat=opencover
```

---

## 📐 Padrões de Código

### C#

```csharp
// ✅ Bom
public class CustomerService
{
    private readonly ICustomerRepository _repository;
    
    public CustomerService(ICustomerRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }
    
    public async Task<CustomerDto> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var customer = await _repository.GetByIdAsync(id, cancellationToken);
        return customer != null ? _mapper.Map<CustomerDto>(customer) : null;
    }
}

// ❌ Evite
public class CustomerService
{
    public ICustomerRepository repo;
    
    public CustomerDto GetById(int id)
    {
        var c = repo.GetById(id); // sem async/await
        return c != null ? Map(c) : null;
    }
}
```

### Segurança

```csharp
// ✅ Correto: Senhas via Configuration/Secrets
var password = _configuration["Secrets:DbPassword"];

// ✅ Correto: API Keys via Environment
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

// ❌ NUNCA
const string password = "SuperSecret123"; // BLOQUEADO em PR
```

---

## 🔐 Segurança

### Pre-commit Hooks

Recomendamos instalar [git-secrets](https://github.com/awslabs/git-secrets):

```bash
# macOS
brew install git-secrets

# Linux
git clone https://github.com/awslabs/git-secrets.git
cd git-secrets && make install

# Registre padrões
git secrets --install
git secrets --register-aws
```

### Checklist de PR

- [ ] Nenhuma credencial commitada
- [ ] Nenhum token/API key visível
- [ ] Variáveis sensíveis via env/config
- [ ] Dependencies atualizadas e seguras
- [ ] Sem `console.log()`, `Debug.WriteLine()` em produção

---

## 📝 Documentação

### Quando Documentar

- Novas funcionalidades públicas
- Mudanças em APIs existentes
- Workflows complexos
- Configurações não óbvias

### Onde Documentar

| Tipo | Arquivo |
|------|---------|
| Setup/Deploy | `docs/DEPLOYMENT_OFFLINE_INSTALL.md` |
| Configuração | `docs/CONFIGURATION.md` |
| APIs | `src/Discovery.Api/Controllers/*.cs` (XML comments) |
| Integrações | `docs/MESHCENTRAL_INTEGRATION_PLAN.md` |

---

## 🚀 Release Process

### Release Estável (main)

1. **Criar release branch:**
   ```bash
   git checkout -b release/vX.Y.Z dev
   ```

2. **Bump de versão** (em .csproj, CHANGELOG.md)

3. **Merge e tag:**
   ```bash
   git checkout main
   git merge --no-ff release/vX.Y.Z -m "chore(release): v1.2.0"
   git tag -a vX.Y.Z -m "Release version X.Y.Z"
   git push origin main --tags
   ```

### Beta Release

- Branch: `beta`
- Tag: `vX.Y.Z-beta.N`
- Automático via CI/CD

---

## 💬 Comunicação

- **Issues**: Use templates para bugs/features
- **Discussions**: Perguntas sobre uso/arquitetura
- **Security**: Reporte via [SECURITY.md](SECURITY.md)

---

## 📜 Licença

Contribuindo, você concorda que suas contribuições são licenciadas sob a mesma licença do projeto.

---

Obrigado por contribuir! 🎉
