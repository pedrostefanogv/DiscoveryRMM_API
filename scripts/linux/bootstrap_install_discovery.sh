#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=""
LOCAL_INSTALLER_PATH=""
if [[ -n "${BASH_SOURCE[0]:-}" ]]; then
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  LOCAL_INSTALLER_PATH="$SCRIPT_DIR/install_discovery_server.sh"
fi

log() {
  printf '[bootstrap] %s\n' "$*"
}

warn() {
  printf '[bootstrap][aviso] %s\n' "$*" >&2
}

fail() {
  printf '[bootstrap][erro] %s\n' "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || fail "Comando obrigatorio ausente: $1"
}

run_privileged() {
  if [[ "$EUID" -eq 0 ]]; then
    "$@"
    return
  fi

  if command -v sudo >/dev/null 2>&1; then
    sudo "$@"
    return
  fi

  return 1
}

ensure_bootstrap_command() {
  local command_name="$1"
  shift

  if command -v "$command_name" >/dev/null 2>&1; then
    return
  fi

  if ! command -v apt-get >/dev/null 2>&1; then
    fail "Comando obrigatorio ausente: $command_name. Instale os pacotes: $*"
  fi

  warn "Comando '$command_name' ausente; tentando instalar pacotes: $*"
  if ! run_privileged env DEBIAN_FRONTEND=noninteractive apt-get update -y; then
    fail "Nao foi possivel atualizar a lista de pacotes para instalar: $*"
  fi

  if ! run_privileged env DEBIAN_FRONTEND=noninteractive apt-get install -y "$@"; then
    fail "Nao foi possivel instalar os pacotes: $*"
  fi

  require_cmd "$command_name"
}

ensure_bootstrap_dependencies() {
  require_cmd bash
  ensure_bootstrap_command mktemp coreutils
  ensure_bootstrap_command git git ca-certificates
}

detect_system_architecture() {
  local arch=""

  if command -v dpkg >/dev/null 2>&1; then
    arch="$(dpkg --print-architecture 2>/dev/null || true)"
  fi

  if [[ -z "$arch" ]]; then
    arch="$(uname -m 2>/dev/null || true)"
  fi

  printf '%s' "$arch"
}

map_arch_to_dotnet_runtime() {
  local arch_raw="${1:-}"
  local arch
  arch="$(printf '%s' "$arch_raw" | tr '[:upper:]' '[:lower:]')"

  case "$arch" in
    amd64|x86_64)
      printf 'linux-x64'
      ;;
    arm64|aarch64)
      printf 'linux-arm64'
      ;;
    *)
      return 1
      ;;
  esac
}

normalize_branch() {
  local raw="$1"
  local normalized
  normalized="$(printf '%s' "$raw" | tr '[:upper:]' '[:lower:]')"

  case "$normalized" in
    lts|release|beta|dev)
      printf '%s' "$normalized"
      return
      ;;
  esac

  [[ "$raw" =~ ^[A-Za-z0-9._/-]+$ ]] || fail "Branch invalida: $raw"
  printf '%s' "$raw"
}

is_maintenance_mode() {
  local mode_raw="${1:-}"
  local mode
  mode="$(printf '%s' "$mode_raw" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$mode" in
    maintenance|advanced|manutencao) return 0 ;;
    *) return 1 ;;
  esac
}

installer_supports_maintenance() {
  local installer_path="$1"
  [[ -f "$installer_path" ]] || return 1
  grep -Eq 'apply_maintenance_mode|--maintenance|maintenance|manutencao' "$installer_path"
}

exec_installer() {
  local installer_path="$1"
  shift

  log "Executando instalador"
  exec env \
    DISCOVERY_DOTNET_RUNTIME="$DISCOVERY_DOTNET_RUNTIME" \
    DISCOVERY_GIT_BRANCH="$BOOTSTRAP_BRANCH" \
    DISCOVERY_RELEASE_CHANNEL="$BOOTSTRAP_BRANCH" \
    bash "$installer_path" "$@"
}

bootstrap_header() {
  local title="$1"
  local step_label="$2"

  echo >&2
  echo "========================================" >&2
  echo " Discovery RMM - Etapa $step_label" >&2
  echo "----------------------------------------" >&2
  echo " $title" >&2
  echo "----------------------------------------" >&2
}

choose_branch_interactive() {
  while true; do
    bootstrap_header "Canal/branch de instalacao" "2/2"
    echo "Selecione o canal/branch:" >&2
    echo " 1) lts     - suporte longo prazo" >&2
    echo " 2) release - canal estavel" >&2
    echo " 3) beta    - novidades em teste" >&2
    echo " 4) dev     - desenvolvimento" >&2
    echo " 5) custom  - informar branch manualmente" >&2
    echo "----------------------------------------" >&2
    echo "Dica: digite o numero ou o nome (ex: release)." >&2
    echo "Padrao: [2] release (pressione Enter)." >&2

    local selected_option
    read -r -p "Opcao [2]: " selected_option
    selected_option="${selected_option:-2}"
    selected_option="$(printf '%s' "$selected_option" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"

    case "$(printf '%s' "$selected_option" | tr '[:upper:]' '[:lower:]')" in
      1|lts)
        printf 'lts'
        return
        ;;
      2|release)
        printf 'release'
        return
        ;;
      3|beta)
        printf 'beta'
        return
        ;;
      4|dev)
        printf 'dev'
        return
        ;;
      5|custom)
        local custom_branch
        read -r -p "Informe a branch custom: " custom_branch
        custom_branch="$(printf '%s' "$custom_branch" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
        [[ -n "$custom_branch" ]] || {
          echo "Branch custom nao informada. Tente novamente." >&2
          continue
        }
        printf '%s' "$(normalize_branch "$custom_branch")"
        return
        ;;
      *)
        echo "Opcao invalida: $selected_option. Use 1-5 ou o nome da branch (lts/release/beta/dev)." >&2
        ;;
    esac
  done
}

choose_operation_mode_interactive() {
  while true; do
    bootstrap_header "Modo de Operacao" "1/2"
    echo "Escolha o que sera executado neste momento:" >&2
    echo "1) Instalacao completa (API + portal web + Postgres + NATS + servicos)" >&2
    echo "2) Atualizar somente configuracao do NATS (inclui issuer/auth_callout)" >&2
    echo "3) Atualizar instalacao existente (repositorios + rebuild API/portal + restart servicos)" >&2
    echo "4) Ver dados do servidor (instalacao atual, usuario, senha, chaves e afins)" >&2
    echo "5) Manutencao avancada (reset senha/MFA e recovery de usuario admin)" >&2
    echo "----------------------------------------" >&2

    local selected_option
    read -r -p "Opcao [1]: " selected_option
    selected_option="${selected_option:-1}"
    selected_option="$(printf '%s' "$selected_option" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"

    case "$(printf '%s' "$selected_option" | tr '[:upper:]' '[:lower:]')" in
      1|full|install|complete)
        printf 'full'
        return
        ;;
      2|nats|nats-only|update-nats)
        printf 'nats'
        return
        ;;
      3|update|update-stack|rebuild|upgrade)
        printf 'update'
        return
        ;;
      4|info|server|dados)
        echo >&2
        echo "========== Dados locais (/etc/discovery-api) ==========" >&2
        if command -v sudo >/dev/null 2>&1 && sudo test -f /etc/discovery-api/discovery.env 2>/dev/null; then
          sudo sed 's/^/  /' /etc/discovery-api/discovery.env >&2 || true
        elif [[ -f /etc/discovery-api/discovery.env ]]; then
          sed 's/^/  /' /etc/discovery-api/discovery.env >&2 || true
        else
          echo "  Arquivo /etc/discovery-api/discovery.env nao encontrado." >&2
        fi
        echo "=======================================================" >&2
        echo "Pressione Enter para voltar ao menu..." >&2
        read -r _
        ;;
      5|maintenance|advanced|manutencao)
        printf 'maintenance'
        return
        ;;
      *)
        echo "Opcao invalida: $selected_option. Use 1-5." >&2
        ;;
    esac
  done
}

BOOTSTRAP_REPO_URL="${DISCOVERY_BOOTSTRAP_REPO_URL:-https://github.com/pedrostefanogv/DiscoveryRMM_API.git}"
BOOTSTRAP_BRANCH="${DISCOVERY_BOOTSTRAP_BRANCH:-${DISCOVERY_RELEASE_CHANNEL:-${DISCOVERY_GIT_BRANCH:-release}}}"
BOOTSTRAP_WORKDIR=""
INSTALLER_RELATIVE_PATH="scripts/linux/install_discovery_server.sh"
BOOTSTRAP_INSTALL_MODE="${DISCOVERY_INSTALL_MODE:-${INSTALL_MODE:-}}"

INSTALLER_ARGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode)
      BOOTSTRAP_INSTALL_MODE="${2:-}"
      [[ -n "$BOOTSTRAP_INSTALL_MODE" ]] || fail "Parametro --mode exige valor (full|nats|update|maintenance)"
      shift 2
      ;;
    --branch|-b)
      BOOTSTRAP_BRANCH="${2:-}"
      [[ -n "$BOOTSTRAP_BRANCH" ]] || fail "Parametro --branch exige valor"
      shift 2
      ;;
    --repo-url)
      BOOTSTRAP_REPO_URL="${2:-}"
      [[ -n "$BOOTSTRAP_REPO_URL" ]] || fail "Parametro --repo-url exige valor"
      shift 2
      ;;
    --workdir)
      BOOTSTRAP_WORKDIR="${2:-}"
      [[ -n "$BOOTSTRAP_WORKDIR" ]] || fail "Parametro --workdir exige valor"
      shift 2
      ;;
    --)
      shift
      while [[ $# -gt 0 ]]; do
        INSTALLER_ARGS+=("$1")
        shift
      done
      ;;
    *)
      INSTALLER_ARGS+=("$1")
      shift
      ;;
  esac
done

ensure_bootstrap_dependencies

if [[ -z "${DISCOVERY_BOOTSTRAP_BRANCH:-}" && -z "${DISCOVERY_RELEASE_CHANNEL:-}" && -z "${DISCOVERY_GIT_BRANCH:-}" ]]; then
  non_interactive=0
  for arg in "${INSTALLER_ARGS[@]}"; do
    if [[ "$arg" == "--non-interactive" ]]; then
      non_interactive=1
      break
    fi
  done

  if [[ "$non_interactive" -eq 0 ]]; then
    if [[ -z "$BOOTSTRAP_INSTALL_MODE" ]]; then
      BOOTSTRAP_INSTALL_MODE="$(choose_operation_mode_interactive)"
    fi
    if is_maintenance_mode "$BOOTSTRAP_INSTALL_MODE"; then
      BOOTSTRAP_BRANCH="dev"
      log "Modo maintenance: usando branch 'dev' (unica com suporte a manutencao)"
    else
      BOOTSTRAP_BRANCH="$(choose_branch_interactive)"
    fi
  fi
fi

if is_maintenance_mode "$BOOTSTRAP_INSTALL_MODE" && [[ "$BOOTSTRAP_BRANCH" == "release" || "$BOOTSTRAP_BRANCH" == "lts" ]]; then
  warn "BOOTSTRAP_BRANCH='$BOOTSTRAP_BRANCH' nao possui manutencao; alterando para 'dev'"
  BOOTSTRAP_BRANCH="dev"
fi

if is_maintenance_mode "$BOOTSTRAP_INSTALL_MODE" && [[ -n "${LOCAL_INSTALLER_PATH:-}" && -f "$LOCAL_INSTALLER_PATH" ]]; then
  if installer_supports_maintenance "$LOCAL_INSTALLER_PATH"; then
    log "Modo maintenance: usando instalador local em $LOCAL_INSTALLER_PATH"
    exec_installer "$LOCAL_INSTALLER_PATH" --mode "$BOOTSTRAP_INSTALL_MODE" "${INSTALLER_ARGS[@]}"
  fi
  warn "Instalador local nao possui suporte ao modo maintenance; tentando clone remoto."
fi

if [[ -n "$BOOTSTRAP_INSTALL_MODE" ]]; then
  INSTALLER_ARGS=(--mode "$BOOTSTRAP_INSTALL_MODE" "${INSTALLER_ARGS[@]}")
fi

BOOTSTRAP_BRANCH="$(normalize_branch "$BOOTSTRAP_BRANCH")"

if [[ -z "${DISCOVERY_DOTNET_RUNTIME:-}" ]]; then
  local_arch="$(detect_system_architecture)"
  if DISCOVERY_DOTNET_RUNTIME="$(map_arch_to_dotnet_runtime "$local_arch")"; then
    log "Runtime .NET detectado para o host: $DISCOVERY_DOTNET_RUNTIME ($local_arch)"
  else
    DISCOVERY_DOTNET_RUNTIME="linux-x64"
    warn "Arquitetura nao mapeada (${local_arch:-desconhecida}); usando runtime padrao linux-x64"
  fi
fi

if [[ -z "$BOOTSTRAP_WORKDIR" ]]; then
  BOOTSTRAP_WORKDIR="$(mktemp -d /tmp/discovery-bootstrap.XXXXXX)"
fi

clone_candidates=("$BOOTSTRAP_BRANCH")
case "$BOOTSTRAP_BRANCH" in
  lts|release|beta|dev)
    for candidate in release dev beta lts; do
      [[ "$candidate" == "$BOOTSTRAP_BRANCH" ]] && continue
      clone_candidates+=("$candidate")
    done
    ;;
esac

CLONED_BRANCH=""
INSTALLER_PATH=""
for candidate in "${clone_candidates[@]}"; do
  rm -rf "$BOOTSTRAP_WORKDIR"
  mkdir -p "$BOOTSTRAP_WORKDIR"

  log "Tentando bootstrap do canal/branch '$candidate'"
  if ! git clone --depth 1 --branch "$candidate" "$BOOTSTRAP_REPO_URL" "$BOOTSTRAP_WORKDIR" >/dev/null 2>&1; then
    log "Canal/branch '$candidate' indisponivel para clone"
    continue
  fi

  INSTALLER_PATH="$BOOTSTRAP_WORKDIR/$INSTALLER_RELATIVE_PATH"
  if [[ ! -f "$INSTALLER_PATH" ]]; then
    log "Instalador ausente em '$candidate' ($INSTALLER_RELATIVE_PATH)"
    continue
  fi

  CLONED_BRANCH="$candidate"
  break
done

[[ -n "$CLONED_BRANCH" ]] || fail "Nenhum canal/branch valido encontrado. Tentativas: ${clone_candidates[*]}"

if [[ "$CLONED_BRANCH" != "$BOOTSTRAP_BRANCH" ]]; then
  log "Fallback aplicado: solicitado '$BOOTSTRAP_BRANCH', usando '$CLONED_BRANCH'"
fi

if is_maintenance_mode "$BOOTSTRAP_INSTALL_MODE" && ! installer_supports_maintenance "$INSTALLER_PATH"; then
  fail "A branch '$CLONED_BRANCH' nao possui suporte ao modo maintenance. Informe --branch dev/beta (com suporte) ou execute scripts/linux/install_discovery_server.sh --mode maintenance em um checkout atualizado."
fi

exec_installer "$INSTALLER_PATH" "${INSTALLER_ARGS[@]}"