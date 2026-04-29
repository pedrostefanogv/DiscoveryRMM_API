# ADR: Estratégia de Versionamento de API

> **Status:** Implementado ✅  
> **Data:** 2026-04-29  
> **Decisores:** Time DiscoveryRMM  

---

## Contexto

A API atualmente não possui versionamento. Todos os endpoints estão em `/api/*` sem prefixo de versão. O projeto usa controllers ASP.NET Core com `[Route("api/[controller]")]` e partial classes.

Com 52 controllers e 37+ endpoints só no `AgentAuthController`, mudanças breaking são inevitáveis à medida que o produto evolui.

---

## Alternativas Consideradas

### A) URL Path (`/api/v1/`, `/api/v2/`)

```csharp
[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class TicketsController : ControllerBase { }
```

**Prós:** Mais comum, simples, óbvio para clientes.  
**Contras:** Muda URLs existentes (requer redirect 301).

### B) Query String (`?api-version=1.0`)

**Prós:** URLs não mudam.  
**Contras:** Menos RESTful, cache mais complexo.

### C) Header (`X-API-Version: 1.0`)

**Prós:** URLs limpas.  
**Contras:** Invisível para debugging, difícil em browsers.

### D) Content Negotiation (`Accept: application/json;v=1.0`)

**Prós:** Muito RESTful.  
**Contras:** Complexo, pouco adotado.

---

## Recomendação: **A — URL Path** com `Asp.Versioning.Mvc`

### Por que URL Path?

1. **Visível** — fácil debugar, testar e documentar
2. **Compatível com Swagger/Scalar** — gera docs por versão automaticamente
3. **Adotado por APIs maduras** — Stripe, GitHub, Azure
4. **Sem ambiguidade** de cache (URLs diferentes = recursos diferentes)

### Plano de Implementação

#### Fase 1 — Setup (iminente)
- Adicionar pacote `Asp.Versioning.Mvc` (1 pacote leve)
- Configurar no `Program.cs` via extension method
- Versão atual se torna `v1` (default)
- Adicionar redirect `301` de `/api/*` para `/api/v1/*`

```csharp
// Program.cs
builder.Services.AddDiscoveryApiVersioning();

// DependencyInjection/ApiVersioningServiceCollectionExtensions.cs
services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
});
```

#### Fase 2 — Documentação
- Scalar UI mostra dropdown de versões
- Deprecation header para versões antigas

#### Fase 3 — Política de versionamento
- **v1** permanece estável (atual)
- **v2** só é criada quando há breaking change documentado
- Suporte a **2 versões simultâneas** no máximo
- **Depreciação**: 6 meses de aviso antes de remover versão antiga

### Impacto nos Controllers

```csharp
// Antes
[Route("api/[controller]")]

// Depois
[Route("api/v{version:apiVersion}/[controller]")]
```

Os controllers na pasta `AgentAuth/` usam `[Route("api/agent-auth")]` — muda para `[Route("api/v{version:apiVersion}/agent-auth")]`.

### Mitigação de Breaking Change

- Adicionar **redirect permanente (301)** de `/api/agent-auth/*` → `/api/v1/agent-auth/*`
- Manter ambos os padrões por 1 release (grace period)
- Notificar parceiros de integração (MeshCentral, agents) com 30 dias de antecedência

---

## Decisão

- [x] **Aprovar URL Path** — implementado ✅
- [ ] **Aprovar Header** — descartado
- [ ] **Adiar** — descartado

---

## Referências

- [ASP.NET API Versioning](https://github.com/dotnet/aspnet-api-versioning)
- [Microsoft REST API Guidelines — Versioning](https://github.com/microsoft/api-guidelines/blob/vNext/Guidelines.md#12-versioning)
