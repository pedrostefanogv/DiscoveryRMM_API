<!-- Language selector -->
<div align="right">
  🌐 <strong>PT-BR</strong> | <a href="#english-version">EN-US</a>
</div>

---

<div align="center">

<img src="https://img.shields.io/badge/Discovery-RMM-0078D4?style=for-the-badge&logo=windows&logoColor=white" alt="Discovery RMM" />

# Discovery RMM — Servidor Central

**Plataforma de gerenciamento remoto de endpoints (RMM) open-source**  
construída com .NET 10, NATS e PostgreSQL.

[![CI](https://img.shields.io/github/actions/workflow/status/pedrostefanogv/DiscoveryRMM_API/ci.yml?branch=release&label=CI&logo=githubactions&logoColor=white)](https://github.com/pedrostefanogv/DiscoveryRMM_API/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/actions/workflow/status/pedrostefanogv/DiscoveryRMM_API/release.yml?branch=release&label=Release&logo=githubactions&logoColor=white)](https://github.com/pedrostefanogv/DiscoveryRMM_API/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-15+-336791?logo=postgresql&logoColor=white)](https://www.postgresql.org)
[![NATS](https://img.shields.io/badge/NATS-2.x-27AAE1?logo=natsdotio&logoColor=white)](https://nats.io)
[![Version](https://img.shields.io/badge/version-1.0.0-brightgreen)](CHANGELOG.md)

</div>

> [!IMPORTANT]
> Este projeto usa IA generativa e práticas de vibe coding como parte do processo de desenvolvimento, revisão e documentação. Se você não concorda com esse tipo de processo assistido por IA, não utilize este projeto.

---

## 📋 Índice

- [Sobre o Projeto](#sobre-o-projeto)
- [Agent Windows](#-agent-windows)
- [Funcionalidades](#-funcionalidades)
- [Arquitetura](#-arquitetura)
- [Stack](#-stack-tecnológica)
- [Instalação Rápida](#-instalação-rápida)
- [Recuperação de Senha/Admin](#recuperação-de-senhaadmin-shell-local)
- [Configuração](#-configuração)
- [Contribuição](#-contribuição)
- [Licença](#-licença)

---

## Sobre o Projeto

**Discovery RMM** é um servidor de gerenciamento remoto de endpoints (RMM) open-source desenvolvido para equipes de TI que precisam de visibilidade, controle e automação sobre sua infraestrutura de dispositivos Windows e Linux.

O servidor expõe uma **API REST + WebSocket** que se comunica com agents leves instalados nos endpoints, oferecendo inventário de hardware, execução remota de scripts, geração automática de chamados e muito mais.

```
┌─────────────────────────────────────────────────────────┐
│                  Discovery RMM Server                   │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌───────┐  │
│  │  REST API │  │ SignalR  │  │   NATS   │  │  AI   │  │
│  │ (Scalar) │  │(Realtime)│  │ (Broker) │  │ Chat  │  │
│  └──────────┘  └──────────┘  └──────────┘  └───────┘  │
│         ↑              ↑            ↑                   │
│         └──────────────┴────────────┘                   │
│                    Agent (Windows)                      │
└─────────────────────────────────────────────────────────┘
```

---

## 🖥️ Agent Windows

> O **Discovery Agent** é o componente instalado nos endpoints e se comunica com este servidor.

[![Agent Repository](https://img.shields.io/badge/GitHub-Discovery--Agent-181717?style=for-the-badge&logo=github)](https://github.com/pedrostefanogv/DiscoveryRMM_Agent)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D4?style=for-the-badge&logo=windows)](https://github.com/pedrostefanogv/DiscoveryRMM_Agent/releases)

O agent é distribuído como **executável `.exe` para Windows** e realiza:
- Autenticação mútua com o servidor via JWT + API Key
- Coleta de inventário de hardware (CPU, RAM, disco, rede)
- Execução remota de scripts PowerShell/CMD
- Comunicação em tempo real via **NATS** (com fallback para **SignalR**)
- Auto-atualização automática

📦 **Download do Agent:** [Releases do Agent](https://github.com/pedrostefanogv/DiscoveryRMM_Agent/releases)

---

## ✨ Funcionalidades

Os módulos abaixo estão em fase **testável / pré-estável**: disponíveis para validação, mas ainda sujeitos a ajustes antes de uma classificação estável.

| Módulo | Descrição | Status |
|--------|-----------|--------|
| 🔐 **Autenticação** | JWT + API Keys + MFA (TOTP/FIDO2) | 🧪 Pré-estável |
| 📦 **Inventário** | Hardware, software, rede por endpoint | 🧪 Pré-estável |
| 💬 **Chat com IA** | OpenAI/Ollama para análise de tickets | 🧪 Pré-estável |
| 🎫 **Auto-Tickets** | Motor automático de geração de chamados | 🧪 Pré-estável |
| 🔧 **Campos Custom** | Campos configuráveis por entidade | 🧪 Pré-estável |
| 🖥️ **Acesso Remoto** | MeshCentral embed + debug remoto | 🧪 Pré-estável |
| 📊 **Relatórios** | Templates personalizáveis com exportação | 🧪 Pré-estável |
| 🗄️ **Object Storage** | Local, MinIO e S3-compatible | 🧪 Pré-estável |
| 🚀 **App Store** | Catálogo de apps com deploy automatizado | 🧪 Pré-estável |
| 📡 **NATS Messaging** | Broker com auth callout e credenciais | 🧪 Pré-estável |
| 🔭 **OpenTelemetry** | Rastreamento distribuído e métricas | 🧪 Pré-estável |
| 🔄 **Auto-Update** | Self-update do servidor via script | 🧪 Pré-estável |

---

## 🏗️ Arquitetura

```
discovery-rmm-server/
├── src/
│   ├── Discovery.Api/          # ASP.NET Core - Controllers, Hubs, Middleware
│   ├── Discovery.Core/         # Entidades, Interfaces, DTOs, ValueObjects
│   ├── Discovery.Infrastructure/ # Repositórios, Services, NATS, Storage
│   └── Discovery.Migrations/   # FluentMigrator - schema do banco
├── .github/
│   └── workflows/
│       ├── ci.yml              # Build + Testes
│       ├── release.yml         # Channel Delivery (beta/lts/release)
│       └── security-hotfix.yml # Fast-track para CVEs críticos
└── docs/                       # Documentação por capacidade
```

### Canais de Release

| Branch | Canal | Propósito |
|--------|-------|-----------|
| `release` | 🟢 Estável | Branch padrão — produção |
| `beta` | 🟡 Beta | Pré-release com novas features |
| `lts` | 🔵 LTS | Long-Term Support |
| `dev` | ⚪ Dev | Desenvolvimento ativo |

---

## 🛠️ Stack Tecnológica

<div align="center">

| Camada | Tecnologia |
|--------|-----------|
| Runtime | .NET 10 (ASP.NET Core) |
| Banco de dados | PostgreSQL 15+ com pgvector |
| Mensageria | NATS 2.x com JetStream |
| Cache | Redis (StackExchange.Redis) |
| ORM | Entity Framework Core 10 + Npgsql |
| Migrations | FluentMigrator |
| Realtime | SignalR (WebSocket) |
| IA | OpenAI / Ollama (pgvector embeddings) |
| Observabilidade | OpenTelemetry (traces + metrics) |
| Acesso Remoto | MeshCentral |
| Autenticação | JWT Bearer + API Keys + Argon2 |
| Validação | FluentValidation |
| API Docs | Scalar (OpenAPI) |
| Storage | Local / MinIO / AWS S3 |
| Testes | NUnit + Mocks |

</div>

---

## 🚀 Instalação Rápida

### Pré-requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [PostgreSQL 15+](https://www.postgresql.org/download/) com extensão `pgvector`
- [NATS Server 2.x](https://nats.io/download/)
- Redis (opcional, para cache)

### 1. Clonar e Restaurar

```bash
git clone https://github.com/pedrostefanogv/DiscoveryRMM_API.git
cd DiscoveryRMM_API
dotnet restore Discovery.slnx
```

### 2. Configurar Ambiente

```bash
cp src/Discovery.Api/appsettings.Development.json.example src/Discovery.Api/appsettings.Development.json
# Editar com suas credenciais de banco e NATS
```

### 3. Rodar Migrations

```bash
dotnet run --project src/Discovery.Api -- --migrate
```

### 4. Iniciar o Servidor

```bash
dotnet run --project src/Discovery.Api
# API disponível em: https://localhost:7001
# Docs Scalar: https://localhost:7001/scalar
```

### 5. Primeiro Login

> [!IMPORTANT]
> Credenciais iniciais após rodar as migrations:
> - Login: `admin`
> - Senha padrão: `Mudar@123`
>
> No primeiro acesso, o sistema exige troca de senha, atualização do perfil e conclusão do cadastro de MFA antes de liberar o uso completo.

### Recuperação de Senha/Admin (Shell Local)

Para cenários de recuperação (senha esquecida, perda de MFA, conta admin inativa), use o modo de manutenção local no servidor:

```bash
dotnet run --project src/Discovery.Api -- --recover-admin
```

Comportamento padrão do comando:
- Redefine a senha do login `admin` (gera senha temporária forte se não for informada)
- Força troca de senha no próximo login
- Revoga sessões ativas do usuário
- Reseta MFA (remove chaves ativas)
- Reativa o usuário se estiver inativo
- Recria o usuário admin se ele não existir e garante vínculo ao role `Admin`

Uso mais seguro (evita senha no histórico do shell):

```bash
echo "NovaSenhaForte@2026" | dotnet run --project src/Discovery.Api -- --recover-admin --password-stdin
```

Ajuda completa do comando:

```bash
dotnet run --project src/Discovery.Api -- --recover-admin-help
```

### Instalação Linux (Produção)

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/pedrostefanogv/DiscoveryRMM_API/release/scripts/linux/bootstrap_install_discovery.sh)"
```

O bootstrap clona o repositório localmente e executa todo o processo de instalação.

Suporte de arquitetura do servidor (fase atual):
- Linux x64 e Linux arm64 para API/portal.
- O Agent continua distribuido como Windows .exe (x86/x64).

Exemplos de canal/branch:

```bash
# Interativo: pergunta lts/release/beta/dev
bash -c "$(curl -fsSL https://raw.githubusercontent.com/pedrostefanogv/DiscoveryRMM_API/release/scripts/linux/bootstrap_install_discovery.sh)"

# Direto em um canal
DISCOVERY_RELEASE_CHANNEL=dev bash -c "$(curl -fsSL https://raw.githubusercontent.com/pedrostefanogv/DiscoveryRMM_API/release/scripts/linux/bootstrap_install_discovery.sh)"

# Com argumento explicito de branch
bash -c "$(curl -fsSL https://raw.githubusercontent.com/pedrostefanogv/DiscoveryRMM_API/release/scripts/linux/bootstrap_install_discovery.sh)" -- --branch beta
```

> Consulte [DEPLOYMENT_OFFLINE_INSTALL.md](docs/DEPLOYMENT_OFFLINE_INSTALL.md) para instalação offline.

---

## ⚙️ Configuração

As principais configurações ficam em `appsettings.json` / variáveis de ambiente:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=discovery;Username=...;Password=..."
  },
  "Nats": {
    "Url": "nats://localhost:4222",
    "CredentialsFile": "/etc/discovery/nats.creds"
  },
  "Jwt": {
    "Issuer": "discovery-rmm",
    "Audience": "discovery-agents",
    "SecretKey": "<sua-chave-secreta>"
  },
  "Redis": {
    "ConnectionString": "localhost:6379"
  }
}
```

> Consulte [CONFIGURATION.md](docs/CONFIGURATION.md) para referência completa.

---

## 🔐 Segurança

- Autenticação JWT + API Keys com Argon2 para hashing
- NATS com autenticação callout
- Rate limiting em endpoints críticos
- Scan automático de CVEs via Dependabot
- Workflow de security hotfix fast-track para vulnerabilidades críticas
- Nenhum secret deve ser commitado — use variáveis de ambiente

Reporte vulnerabilidades via [GitHub Security Advisories](https://github.com/pedrostefanogv/DiscoveryRMM_API/security/advisories).

---

## 🤝 Contribuição

Contribuições são bem-vindas! Leia o [CONTRIBUTING.md](CONTRIBUTING.md) antes de enviar PRs.

```bash
# Fluxo básico
git checkout dev
git checkout -b feature/sua-feature
# ... develop ...
git push origin feature/sua-feature
# Abrir PR para dev
```

### Executar Testes

```bash
dotnet test src/Discovery.Tests/Discovery.Tests.csproj
```

---

## 📄 Licença

Distribuído sob a licença MIT. Veja [LICENSE](LICENSE) para mais informações.

---

## 🔗 Links Úteis

- 📦 [Agent Windows](https://github.com/pedrostefanogv/DiscoveryRMM_Agent) — Componente instalado nos endpoints
- 📖 [Documentação Completa](docs/)
- 🔄 [Changelog](CHANGELOG.md)
- 🔒 [Security Advisories](https://github.com/pedrostefanogv/DiscoveryRMM_API/security/advisories)
- 📋 [Guia de Deployment](docs/DEPLOYMENT_OFFLINE_INSTALL.md)

---

---

<a name="english-version"></a>

<div align="right">
  🌐 <a href="#top">PT-BR</a> | <strong>EN-US</strong>
</div>

<div align="center">

# Discovery RMM — Server

**Open-source Remote Monitoring & Management (RMM) platform**  
built with .NET 10, NATS and PostgreSQL.

[![CI](https://img.shields.io/github/actions/workflow/status/pedrostefanogv/DiscoveryRMM_API/ci.yml?branch=release&label=CI&logo=githubactions&logoColor=white)](https://github.com/pedrostefanogv/DiscoveryRMM_API/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/actions/workflow/status/pedrostefanogv/DiscoveryRMM_API/release.yml?branch=release&label=Release&logo=githubactions&logoColor=white)](https://github.com/pedrostefanogv/DiscoveryRMM_API/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-15+-336791?logo=postgresql&logoColor=white)](https://www.postgresql.org)
[![NATS](https://img.shields.io/badge/NATS-2.x-27AAE1?logo=natsdotio&logoColor=white)](https://nats.io)
[![Version](https://img.shields.io/badge/version-1.0.0-brightgreen)](CHANGELOG.md)

</div>

> [!IMPORTANT]
> This project uses generative AI and vibe coding practices as part of its development, review and documentation process. If you do not agree with this AI-assisted process, do not use this project.

## About

**Discovery RMM** is an open-source Remote Monitoring & Management server designed for IT teams that need visibility, control and automation over their Windows and Linux device infrastructure.

The server exposes a **REST API + WebSocket** that communicates with lightweight agents installed on endpoints, providing hardware inventory, remote script execution, automated ticket creation and much more.

## 🖥️ Windows Agent

> The **Discovery Agent** is installed on endpoints and communicates with this server.

[![Agent Repository](https://img.shields.io/badge/GitHub-Discovery--Agent-181717?style=for-the-badge&logo=github)](https://github.com/pedrostefanogv/DiscoveryRMM_Agent)

The agent is distributed as a **Windows `.exe`** and handles:
- Mutual authentication with the server via JWT + API Key
- Hardware inventory collection (CPU, RAM, disk, network)
- Remote PowerShell/CMD script execution
- Real-time communication via **NATS** (with **SignalR** fallback)
- Automatic self-update

📦 **Download Agent:** [Agent Releases](https://github.com/pedrostefanogv/DiscoveryRMM_Agent/releases)

## ✨ Features

The modules below are **testable / pre-stable**: available for validation, but still subject to changes before a stable classification.

| Module | Description | Status |
|--------|-------------|--------|
| 🔐 **Authentication** | JWT + API Keys + MFA (TOTP/FIDO2) | 🧪 Pre-stable |
| 📦 **Inventory** | Hardware, software, network per endpoint | 🧪 Pre-stable |
| 💬 **AI Chat** | OpenAI/Ollama for ticket analysis | 🧪 Pre-stable |
| 🎫 **Auto-Tickets** | Automatic ticket generation engine | 🧪 Pre-stable |
| 🔧 **Custom Fields** | Configurable fields per entity | 🧪 Pre-stable |
| 🖥️ **Remote Access** | MeshCentral embed + remote debug | 🧪 Pre-stable |
| 📊 **Reports** | Customizable templates with export | 🧪 Pre-stable |
| 🗄️ **Object Storage** | Local, MinIO and S3-compatible | 🧪 Pre-stable |
| 🚀 **App Store** | App catalog with automated deployment | 🧪 Pre-stable |
| 📡 **NATS Messaging** | Broker with auth callout and credentials | 🧪 Pre-stable |
| 🔭 **OpenTelemetry** | Distributed tracing and metrics | 🧪 Pre-stable |

## 🚀 Quick Start

```bash
git clone https://github.com/pedrostefanogv/DiscoveryRMM_API.git
cd DiscoveryRMM_API
dotnet restore Discovery.slnx
dotnet run --project src/Discovery.Api
# API available at: https://localhost:7001
# Scalar docs: https://localhost:7001/scalar
```

### First Login

> [!IMPORTANT]
> Initial credentials after running the migrations:
> - Login: `admin`
> - Default password: `Mudar@123`
>
> On first access, the system forces password change, profile update and MFA onboarding before full access is granted.

### Admin Access Recovery (Local Shell)

For recovery scenarios (forgotten password, lost MFA, inactive admin account), use the local maintenance mode on the server:

```bash
dotnet run --project src/Discovery.Api -- --recover-admin
```

Default command behavior:
- Resets password for `admin` login (generates a strong temporary password when not provided)
- Forces password change on next login
- Revokes active sessions for that user
- Resets MFA (deactivates active keys)
- Reactivates the user if inactive
- Recreates the admin user if missing and ensures `Admin` role binding

Safer usage (avoid password in shell history):

```bash
echo "YourStrongPassword@2026" | dotnet run --project src/Discovery.Api -- --recover-admin --password-stdin
```

Full command help:

```bash
dotnet run --project src/Discovery.Api -- --recover-admin-help
```

### Linux Install (Production)

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/pedrostefanogv/DiscoveryRMM_API/release/scripts/linux/bootstrap_install_discovery.sh)"
```

The bootstrap clones the repository locally and executes the full install process.

Branch/channel examples:

```bash
# Interactive: asks for lts/release/beta/dev
bash -c "$(curl -fsSL https://raw.githubusercontent.com/pedrostefanogv/DiscoveryRMM_API/release/scripts/linux/bootstrap_install_discovery.sh)"

# Direct channel selection
DISCOVERY_RELEASE_CHANNEL=dev bash -c "$(curl -fsSL https://raw.githubusercontent.com/pedrostefanogv/DiscoveryRMM_API/release/scripts/linux/bootstrap_install_discovery.sh)"

# Explicit branch argument
bash -c "$(curl -fsSL https://raw.githubusercontent.com/pedrostefanogv/DiscoveryRMM_API/release/scripts/linux/bootstrap_install_discovery.sh)" -- --branch beta
```

## 🤝 Contributing

Contributions are welcome! Read [CONTRIBUTING.md](CONTRIBUTING.md) before sending PRs.

```bash
git checkout dev
git checkout -b feature/your-feature
git push origin feature/your-feature
# Open PR targeting dev
```

### Run Tests

```bash
dotnet test src/Discovery.Tests/Discovery.Tests.csproj
```

## 📄 License

Distributed under the MIT License. See [LICENSE](LICENSE) for more information.

## 🔗 Useful Links

- 📦 [Windows Agent](https://github.com/pedrostefanogv/DiscoveryRMM_Agent) — Component installed on endpoints
- 📖 [Full Documentation](docs/)
- 🔄 [Changelog](CHANGELOG.md)
- 🔒 [Security Advisories](https://github.com/pedrostefanogv/DiscoveryRMM_API/security/advisories)
- 📋 [Deployment Guide](docs/DEPLOYMENT_OFFLINE_INSTALL.md)
