#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT_PATH="$SCRIPT_DIR/$(basename "${BASH_SOURCE[0]}")"
ORIGINAL_ARGS=("$@")

NON_INTERACTIVE=0
CONFIG_FILE=""
UPDATE_NATS_CONFIG_ONLY=0
INSTALL_MODE=""
SUDO_KEEPALIVE_PID=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --non-interactive)
      NON_INTERACTIVE=1
      shift
      ;;
    --config)
      CONFIG_FILE="${2:-}"
      [[ -n "$CONFIG_FILE" ]] || { echo "Parametro --config exige caminho de arquivo" >&2; exit 1; }
      shift 2
      ;;
    --update-nats-config|--nats-only)
      UPDATE_NATS_CONFIG_ONLY=1
      shift
      ;;
    --mode)
      INSTALL_MODE="${2:-}"
      [[ -n "$INSTALL_MODE" ]] || { echo "Parametro --mode exige valor (full|nats)" >&2; exit 1; }
      shift 2
      ;;
    *)
      echo "Parametro invalido: $1" >&2
      exit 1
      ;;
  esac
done

if [[ -n "$CONFIG_FILE" ]]; then
  [[ -f "$CONFIG_FILE" ]] || { echo "Arquivo de configuracao nao encontrado: $CONFIG_FILE" >&2; exit 1; }
  # shellcheck disable=SC1090
  source "$CONFIG_FILE"
fi

# Compatibilidade com arquivos de configuracao antigos.
if [[ -z "${GITHUB_PAT:-}" && -n "${GITHUB_TOKEN:-}" ]]; then
  GITHUB_PAT="$GITHUB_TOKEN"
fi

log() {
  printf '[install] %s\n' "$*"
}

fail() {
  printf '[install][erro] %s\n' "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || fail "Comando obrigatorio ausente: $1"
}

prompt_required_value() {
  local var_name="$1"
  local prompt_text="$2"
  local secret="${3:-0}"
  local current_value="${!var_name:-}"

  if [[ -n "$current_value" ]]; then
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    fail "Variavel obrigatoria ausente para modo nao interativo: $var_name"
  fi

  local input=""
  if [[ "$secret" -eq 1 ]]; then
    read -r -s -p "$prompt_text: " input
    printf '\n'
  else
    read -r -p "$prompt_text: " input
  fi

  [[ -n "$input" ]] || fail "Valor obrigatorio nao informado para $var_name"
  printf -v "$var_name" '%s' "$input"
}

confirm_root_bootstrap_password() {
  local password_already_configured="${1:-0}"

  if [[ "$password_already_configured" -eq 1 || "$NON_INTERACTIVE" -eq 1 ]]; then
    [[ -n "${DISCOVERY_INSTALL_USER_PASSWORD:-}" ]] || fail "Variavel obrigatoria ausente para modo nao interativo: DISCOVERY_INSTALL_USER_PASSWORD"
    return
  fi

  local password_confirm=""
  read -r -s -p "Confirme a senha do usuario instalador: " password_confirm
  printf '\n'

  [[ "${DISCOVERY_INSTALL_USER_PASSWORD}" == "$password_confirm" ]] || fail "As senhas do usuario instalador nao conferem."
}

validate_installer_user_name() {
  local user_name="$1"
  [[ "$user_name" =~ ^[a-z_][a-z0-9_-]*\$?$ ]] || fail "Nome de usuario invalido: $user_name"
  [[ "$user_name" != "root" ]] || fail "O usuario instalador nao pode ser root."
}

ensure_installer_user_from_root() {
  local user_name="$1"
  local user_password="$2"

  require_cmd useradd
  require_cmd usermod
  require_cmd chpasswd
  require_cmd sudo

  if ! getent group sudo >/dev/null 2>&1; then
    fail "Grupo sudo nao encontrado neste sistema. Este instalador espera uma base Debian/Ubuntu com sudo."
  fi

  if id -u "$user_name" >/dev/null 2>&1; then
    log "Usuario instalador $user_name ja existe; ajustando senha e grupo sudo"
  else
    log "Criando usuario instalador $user_name"
    useradd --create-home --shell /bin/bash "$user_name"
  fi

  printf '%s:%s\n' "$user_name" "$user_password" | chpasswd
  usermod -aG sudo "$user_name"
}

bootstrap_root_execution() {
  echo
  echo "[install][aviso] O instalador foi executado como root."
  echo "[install][aviso] Para evitar uma instalacao presa ao root, sera criado ou usado um usuario comum com sudo para continuar."
  echo

  DISCOVERY_INSTALL_USER="${DISCOVERY_INSTALL_USER:-discovery-installer}"
  if [[ "$NON_INTERACTIVE" -eq 0 ]]; then
    local input_user=""
    read -r -p "Nome do usuario instalador [$DISCOVERY_INSTALL_USER]: " input_user
    DISCOVERY_INSTALL_USER="${input_user:-$DISCOVERY_INSTALL_USER}"
  fi

  validate_installer_user_name "$DISCOVERY_INSTALL_USER"
  local installer_password_already_configured=0
  [[ -n "${DISCOVERY_INSTALL_USER_PASSWORD:-}" ]] && installer_password_already_configured=1
  prompt_required_value DISCOVERY_INSTALL_USER_PASSWORD "Senha do usuario instalador" 1
  confirm_root_bootstrap_password "$installer_password_already_configured"
  ensure_installer_user_from_root "$DISCOVERY_INSTALL_USER" "$DISCOVERY_INSTALL_USER_PASSWORD"

  local askpass_file
  askpass_file="$(mktemp /tmp/discovery-install-sudo-askpass.XXXXXX)"
  cat > "$askpass_file" <<'EOF'
#!/usr/bin/env sh
printf '%s\n' "$DISCOVERY_SUDO_ASKPASS_PASSWORD"
EOF
  chown "$DISCOVERY_INSTALL_USER:$DISCOVERY_INSTALL_USER" "$askpass_file"
  chmod 700 "$askpass_file"

  log "Reexecutando instalador como $DISCOVERY_INSTALL_USER"
  set +e
  sudo -u "$DISCOVERY_INSTALL_USER" -H \
    env \
      DISCOVERY_ROOT_BOOTSTRAPPED=1 \
      DISCOVERY_INSTALL_USER="$DISCOVERY_INSTALL_USER" \
      DISCOVERY_INSTALL_USER_PASSWORD="$DISCOVERY_INSTALL_USER_PASSWORD" \
      DISCOVERY_SUDO_ASKPASS_PASSWORD="$DISCOVERY_INSTALL_USER_PASSWORD" \
      SUDO_ASKPASS="$askpass_file" \
      bash "$SCRIPT_PATH" "${ORIGINAL_ARGS[@]}"
  local exit_code=$?
  set -e

  rm -f "$askpass_file"
  exit "$exit_code"
}

refresh_sudo_credentials() {
  if [[ -n "${SUDO_ASKPASS:-}" ]]; then
    sudo -A -v >/dev/null 2>&1 || fail "sudo nao aceitou a senha do usuario instalador."
  else
    sudo -n -v >/dev/null 2>&1 || fail "sudo sem credencial ativa. Execute: sudo -v"
  fi
}

start_sudo_keepalive() {
  refresh_sudo_credentials
  (
    while true; do
      sleep 60
      if [[ -n "${SUDO_ASKPASS:-}" ]]; then
        sudo -A -v >/dev/null 2>&1 || exit 1
      else
        sudo -n -v >/dev/null 2>&1 || exit 1
      fi
    done
  ) &
  SUDO_KEEPALIVE_PID="$!"
}

cleanup_sudo_keepalive() {
  if [[ -n "${SUDO_KEEPALIVE_PID:-}" ]]; then
    kill "$SUDO_KEEPALIVE_PID" >/dev/null 2>&1 || true
  fi
}

cleanup_on_exit() {
  cleanup_git_askpass
  cleanup_sudo_keepalive
}

if [[ "${EUID}" -eq 0 && "${DISCOVERY_ROOT_BOOTSTRAPPED:-0}" != "1" ]]; then
  bootstrap_root_execution
fi

require_cmd sudo
start_sudo_keepalive
trap cleanup_sudo_keepalive EXIT

prompt_if_empty() {
  local var_name="$1"
  local prompt_text="$2"
  local secret="${3:-0}"
  local default_value="${4:-}"
  local current_value="${!var_name:-}"

  if [[ -n "$current_value" ]]; then
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    if [[ -n "$default_value" ]]; then
      printf -v "$var_name" '%s' "$default_value"
      return
    fi
    fail "Variavel obrigatoria ausente para modo nao interativo: $var_name"
  fi

  local input=""
  if [[ "$secret" -eq 1 ]]; then
    read -r -s -p "$prompt_text" input
    printf '\n'
  else
    if [[ -n "$default_value" ]]; then
      read -r -p "$prompt_text [$default_value]: " input
      input="${input:-$default_value}"
    else
      read -r -p "$prompt_text: " input
    fi
  fi

  [[ -n "$input" ]] || fail "Valor obrigatorio nao informado para $var_name"
  printf -v "$var_name" '%s' "$input"
}

select_operation_mode() {
  if [[ "$UPDATE_NATS_CONFIG_ONLY" -eq 1 ]]; then
    return
  fi

  local requested_mode="${INSTALL_MODE:-}"
  if [[ -z "$requested_mode" && -n "${DISCOVERY_INSTALL_MODE:-}" ]]; then
    requested_mode="$DISCOVERY_INSTALL_MODE"
  fi

  if [[ -n "$requested_mode" ]]; then
    requested_mode="$(printf '%s' "$requested_mode" | tr '[:upper:]' '[:lower:]')"
    case "$requested_mode" in
      full|install|complete)
        UPDATE_NATS_CONFIG_ONLY=0
        return
        ;;
      nats|nats-only|update-nats)
        UPDATE_NATS_CONFIG_ONLY=1
        return
        ;;
      *)
        fail "Modo invalido: $requested_mode (use full ou nats)"
        ;;
    esac
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    return
  fi

  echo
  echo "Escolha a operacao:" 
  echo "1) Instalacao completa (API + Postgres + NATS + servicos)"
  echo "2) Atualizar somente configuracao do NATS (inclui issuer/auth_callout)"

  local selected_option
  read -r -p "Opcao [1]: " selected_option
  selected_option="${selected_option:-1}"

  case "$selected_option" in
    1)
      UPDATE_NATS_CONFIG_ONLY=0
      ;;
    2)
      UPDATE_NATS_CONFIG_ONLY=1
      ;;
    *)
      fail "Opcao invalida: $selected_option"
      ;;
  esac
}

normalize_install_branch_input() {
  local raw_branch="$1"
  local normalized_branch
  normalized_branch="$(printf '%s' "$raw_branch" | tr '[:upper:]' '[:lower:]')"

  case "$normalized_branch" in
    lts|release|beta|dev)
      printf '%s' "$normalized_branch"
      return
      ;;
  esac

  [[ "$raw_branch" =~ ^[A-Za-z0-9._/-]+$ ]] || fail "Branch invalida: $raw_branch"
  printf '%s' "$raw_branch"
}

select_install_branch() {
  local requested_branch="${DISCOVERY_GIT_BRANCH:-}"
  if [[ -z "$requested_branch" && -n "${DISCOVERY_RELEASE_CHANNEL:-}" ]]; then
    requested_branch="$DISCOVERY_RELEASE_CHANNEL"
  fi

  if [[ -n "$requested_branch" ]]; then
    DISCOVERY_GIT_BRANCH="$(normalize_install_branch_input "$requested_branch")"
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    DISCOVERY_GIT_BRANCH="release"
    return
  fi

  while true; do
    echo
    echo "Escolha o canal/branch de instalacao:"
    echo "1) lts"
    echo "2) release"
    echo "3) beta"
    echo "4) dev"
    echo "5) custom (digitar branch manualmente)"

    local selected_option
    read -r -p "Opcao [2]: " selected_option
    selected_option="${selected_option:-2}"
    selected_option="$(printf '%s' "$selected_option" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"

    case "$(printf '%s' "$selected_option" | tr '[:upper:]' '[:lower:]')" in
      1|lts)
        DISCOVERY_GIT_BRANCH="lts"
        return
        ;;
      2|release)
        DISCOVERY_GIT_BRANCH="release"
        return
        ;;
      3|beta)
        DISCOVERY_GIT_BRANCH="beta"
        return
        ;;
      4|dev)
        DISCOVERY_GIT_BRANCH="dev"
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
        DISCOVERY_GIT_BRANCH="$(normalize_install_branch_input "$custom_branch")"
        return
        ;;
      *)
        echo "Opcao invalida: $selected_option. Use 1-5 ou o nome da branch (lts/release/beta/dev)." >&2
        ;;
    esac
  done
}

normalize_access_mode() {
  ACCESS_MODE="${ACCESS_MODE:-internal}"
  ACCESS_MODE="$(printf '%s' "$ACCESS_MODE" | tr '[:upper:]' '[:lower:]')"
  case "$ACCESS_MODE" in
    internal|external|hybrid) ;;
    *) fail "ACCESS_MODE invalido: $ACCESS_MODE (use internal, external ou hybrid)" ;;
  esac
}

validate_security_inputs() {
  if [[ -n "${POSTGRES_PASSWORD:-}" ]] && (( ${#POSTGRES_PASSWORD} < 12 )); then
    fail "POSTGRES_PASSWORD precisa ter pelo menos 12 caracteres."
  fi

  local effective_nats_password
  effective_nats_password="${NATS_AUTH_PASSWORD:-${NATS_PASSWORD:-}}"
  [[ -n "$effective_nats_password" ]] || fail "Senha NATS nao informada."

  if (( ${#effective_nats_password} < 12 )); then
    fail "NATS_PASSWORD precisa ter pelo menos 12 caracteres."
  fi
}

normalize_nats_settings() {
  NATS_BIND_HOST="${NATS_BIND_HOST:-0.0.0.0}"
  NATS_MONITOR_HOST="${NATS_MONITOR_HOST:-127.0.0.1}"
  NATS_AUTH_USER="${NATS_AUTH_USER:-$NATS_USER}"
  NATS_AUTH_PASSWORD="${NATS_AUTH_PASSWORD:-$NATS_PASSWORD}"
  NATS_AUTH_CALLOUT_SUBJECT="${NATS_AUTH_CALLOUT_SUBJECT:-\$SYS.REQ.USER.AUTH}"
  NATS_AUTH_CALLOUT_ENABLED="${NATS_AUTH_CALLOUT_ENABLED:-1}"

  if [[ "$NATS_AUTH_CALLOUT_ENABLED" != "0" && -z "${NATS_AUTH_CALLOUT_ISSUER:-}" ]]; then
    fail "NATS_AUTH_CALLOUT_ISSUER e obrigatorio quando NATS_AUTH_CALLOUT_ENABLED=1."
  fi
}

resolve_nats_conf_path() {
  local nats_conf
  nats_conf="$(systemctl cat nats-server 2>/dev/null | sed -n 's/.*-c \([^ ]*\).*/\1/p' | head -n 1)"
  printf '%s' "${nats_conf:-/etc/nats-server.conf}"
}

load_existing_nats_defaults() {
  local env_file="/etc/discovery-api/discovery.env"
  local nats_conf
  nats_conf="$(resolve_nats_conf_path)"

  if sudo test -f "$env_file"; then
    NATS_AUTH_USER="${NATS_AUTH_USER:-$(sudo awk -F= '/^Nats__AuthUser=/{print substr($0, index($0,$2)); exit}' "$env_file" 2>/dev/null || true)}"
    NATS_AUTH_PASSWORD="${NATS_AUTH_PASSWORD:-$(sudo awk -F= '/^Nats__AuthPassword=/{print substr($0, index($0,$2)); exit}' "$env_file" 2>/dev/null || true)}"
    local existing_callout_enabled
    existing_callout_enabled="$(sudo awk -F= '/^Nats__AuthCallout__Enabled=/{print tolower(substr($0, index($0,$2))); exit}' "$env_file" 2>/dev/null || true)"
    if [[ -z "${NATS_AUTH_CALLOUT_ENABLED:-}" && -n "$existing_callout_enabled" ]]; then
      if [[ "$existing_callout_enabled" == "true" ]]; then
        NATS_AUTH_CALLOUT_ENABLED="1"
      elif [[ "$existing_callout_enabled" == "false" ]]; then
        NATS_AUTH_CALLOUT_ENABLED="0"
      fi
    fi
    NATS_AUTH_CALLOUT_SUBJECT="${NATS_AUTH_CALLOUT_SUBJECT:-$(sudo awk -F= '/^Nats__AuthCallout__Subject=/{print substr($0, index($0,$2)); exit}' "$env_file" 2>/dev/null || true)}"
  fi

  if sudo test -f "$nats_conf"; then
    NATS_BIND_HOST="${NATS_BIND_HOST:-$(sudo sed -n 's/^listen:[[:space:]]*\([^:]*\):[0-9][0-9]*.*/\1/p' "$nats_conf" | head -n 1)}"
    NATS_MONITOR_HOST="${NATS_MONITOR_HOST:-$(sudo sed -n 's/^http:[[:space:]]*\([^:]*\):[0-9][0-9]*.*/\1/p' "$nats_conf" | head -n 1)}"
    NATS_AUTH_CALLOUT_ISSUER="${NATS_AUTH_CALLOUT_ISSUER:-$(sudo sed -n 's/^[[:space:]]*issuer:[[:space:]]*"\([^"]*\)".*/\1/p' "$nats_conf" | head -n 1)}"
  fi

  NATS_USER="${NATS_USER:-${NATS_AUTH_USER:-discovery_nats}}"
  NATS_PASSWORD="${NATS_PASSWORD:-${NATS_AUTH_PASSWORD:-}}"
}

prompt_nats_configuration() {
  load_existing_nats_defaults

  prompt_if_empty NATS_USER "Usuario NATS" 0 "discovery_nats"
  prompt_if_empty NATS_PASSWORD "Senha NATS" 1
  prompt_if_empty NATS_AUTH_USER "Usuario de bypass da API no NATS (auth_users)" 0 "$NATS_USER"
  prompt_if_empty NATS_AUTH_PASSWORD "Senha de bypass da API no NATS" 1 "$NATS_PASSWORD"
  prompt_if_empty NATS_BIND_HOST "Host de bind do NATS (ex: 0.0.0.0 ou 127.0.0.1)" 0 "0.0.0.0"
  prompt_if_empty NATS_MONITOR_HOST "Host do monitor HTTP do NATS (porta 8222)" 0 "127.0.0.1"
  prompt_if_empty NATS_AUTH_CALLOUT_ENABLED "Habilitar NATS auth callout? (1/0)" 0 "1"
  prompt_if_empty NATS_AUTH_CALLOUT_SUBJECT "Subject do auth callout" 0 "\$SYS.REQ.USER.AUTH"

  if [[ "$NATS_AUTH_CALLOUT_ENABLED" != "0" ]]; then
    prompt_if_empty NATS_AUTH_CALLOUT_ISSUER "Issuer publico do auth callout (gerado no servidor)"
  fi
}

update_nats_environment_file() {
  local env_file="/etc/discovery-api/discovery.env"
  if ! sudo test -f "$env_file"; then
    log "Arquivo $env_file nao encontrado. Pulando atualizacao de variaveis NATS da API."
    return
  fi

  log "Atualizando variaveis NATS no /etc/discovery-api/discovery.env"

  local tmp_file
  tmp_file="$(mktemp)"

  sudo awk '
    !/^Nats__Url=/ &&
    !/^Nats__AuthUser=/ &&
    !/^Nats__AuthPassword=/ &&
    !/^Nats__AuthCallout__Enabled=/ &&
    !/^Nats__AuthCallout__Subject=/
  ' "$env_file" > "$tmp_file"

  cat >> "$tmp_file" <<EOF
Nats__Url=nats://${NATS_AUTH_USER}:${NATS_AUTH_PASSWORD}@127.0.0.1:4222
Nats__AuthUser=${NATS_AUTH_USER}
Nats__AuthPassword=${NATS_AUTH_PASSWORD}
Nats__AuthCallout__Enabled=$( [[ "$NATS_AUTH_CALLOUT_ENABLED" == "1" ]] && echo true || echo false )
Nats__AuthCallout__Subject=${NATS_AUTH_CALLOUT_SUBJECT}
EOF

  sudo install -m 640 -o root -g discovery-api "$tmp_file" "$env_file"
  rm -f "$tmp_file"
}

apply_nats_reconfiguration_only() {
  log "Modo de atualizacao NATS: reconfigurando NATS e variaveis da API"

  prompt_nats_configuration
  validate_security_inputs
  normalize_nats_settings

  setup_nats
  update_nats_environment_file

  if sudo systemctl is-active --quiet discovery-api; then
    log "Reiniciando discovery-api para aplicar novas configuracoes NATS"
    sudo systemctl restart discovery-api
  fi

  log "Atualizacao de configuracao NATS concluida"
}

install_apt_dependencies() {
  log "Instalando dependencias de sistema"
  sudo apt-get update -y
  sudo apt-get install -y \
    apt-transport-https \
    ca-certificates \
    curl \
    git \
    gnupg \
    jq \
    lsb-release \
    openssl \
    postgresql \
    postgresql-contrib \
    nats-server
}

ensure_pgvector_package() {
  local pg_major
  pg_major="$(psql --version | sed -n 's/.* \([0-9][0-9]*\)\..*/\1/p' | head -n 1)"
  [[ -n "$pg_major" ]] || fail "Nao foi possivel detectar versao major do PostgreSQL."

  local vector_pkg="postgresql-${pg_major}-pgvector"
  if dpkg -s "$vector_pkg" >/dev/null 2>&1; then
    log "Pacote ${vector_pkg} ja instalado"
    return
  fi

  log "Instalando suporte a embeddings no PostgreSQL (${vector_pkg})"
  sudo apt-get install -y "$vector_pkg"
}

ensure_dotnet_sdk() {
  if command -v dotnet >/dev/null 2>&1; then
    log "dotnet ja instalado"
    return
  fi

  log "Instalando dotnet SDK 10.0"

  local ubuntu_version
  ubuntu_version="$(. /etc/os-release && printf '%s' "$VERSION_ID")"

  if curl -fsSL "https://packages.microsoft.com/config/ubuntu/${ubuntu_version}/packages-microsoft-prod.deb" -o /tmp/packages-microsoft-prod.deb; then
    sudo dpkg -i /tmp/packages-microsoft-prod.deb
    rm -f /tmp/packages-microsoft-prod.deb
    sudo apt-get update -y
    if sudo apt-get install -y dotnet-sdk-10.0; then
      return
    fi
    log "Falha no apt para dotnet-sdk-10.0, aplicando fallback com dotnet-install.sh"
  else
    log "Repositorio apt da Microsoft indisponivel para Ubuntu ${ubuntu_version}, aplicando fallback com dotnet-install.sh"
  fi

  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh
  sudo mkdir -p /usr/share/dotnet
  sudo /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet
  sudo ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
  rm -f /tmp/dotnet-install.sh

  command -v dotnet >/dev/null 2>&1 || fail "dotnet nao foi instalado com sucesso"
}

ensure_service_user() {
  if id -u discovery-api >/dev/null 2>&1; then
    log "Usuario de servico discovery-api ja existe"
    return
  fi

  log "Criando usuario de servico discovery-api"
  sudo useradd --system --create-home --home-dir /opt/discovery-api --shell /usr/sbin/nologin discovery-api
}

create_directories() {
  log "Criando estrutura de diretorios"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_API_BASE"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_API_RELEASES"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_API_SHARED"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_API_SOURCE"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_AGENT_SRC"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_AGENT_ARTIFACTS"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_OPS_DIR"
  sudo install -d -m 750 -o root -g discovery-api /etc/discovery-api
  sudo install -d -m 750 -o root -g discovery-api /etc/discovery-api/certs
}

setup_git_askpass() {
  local askpass_tmp
  askpass_tmp="$(mktemp)"
  cat > "$askpass_tmp" <<'EOF'
#!/usr/bin/env sh
case "$1" in
  *Username*) printf '%s\n' "x-access-token" ;;
  *Password*) printf '%s\n' "$GITHUB_PAT" ;;
  *) printf '\n' ;;
esac
EOF
  ASKPASS_FILE="$DISCOVERY_OPS_DIR/git-askpass.sh"
  sudo install -m 750 -o discovery-api -g discovery-api "$askpass_tmp" "$ASKPASS_FILE"
  rm -f "$askpass_tmp"
  export GIT_ASKPASS="$ASKPASS_FILE"
  export GIT_TERMINAL_PROMPT=0
  export GITHUB_PAT
}

cleanup_git_askpass() {
  if [[ -n "${ASKPASS_FILE:-}" && -f "$ASKPASS_FILE" ]]; then
    sudo rm -f "$ASKPASS_FILE" || rm -f "$ASKPASS_FILE" || true
  fi
}

clone_or_update_repo() {
  local repo_url="$1"
  local repo_dir="$2"

  local -a git_env
  git_env=(
    env
    "GIT_ASKPASS=$GIT_ASKPASS"
    "GIT_TERMINAL_PROMPT=0"
    "GITHUB_PAT=$GITHUB_PAT"
  )

  if [[ ! -d "$repo_dir/.git" ]]; then
    log "Clonando repositorio: $repo_url"
    sudo -u discovery-api "${git_env[@]}" git clone --branch "$DISCOVERY_GIT_BRANCH" "$repo_url" "$repo_dir"
  else
    log "Atualizando repositorio existente: $repo_dir"
    sudo -u discovery-api "${git_env[@]}" git -C "$repo_dir" fetch origin "$DISCOVERY_GIT_BRANCH"
    sudo -u discovery-api "${git_env[@]}" git -C "$repo_dir" checkout "$DISCOVERY_GIT_BRANCH"
    sudo -u discovery-api "${git_env[@]}" git -C "$repo_dir" reset --hard "origin/$DISCOVERY_GIT_BRANCH"
  fi
}

setup_postgres() {
  log "Configurando PostgreSQL"

  local escaped_db_user
  local escaped_db_name
  local escaped_db_password
  escaped_db_user="${POSTGRES_USER//\'/\'\'}"
  escaped_db_name="${POSTGRES_DB//\'/\'\'}"
  escaped_db_password="${POSTGRES_PASSWORD//\'/\'\'}"

  sudo systemctl enable postgresql
  sudo systemctl restart postgresql

  ensure_pgvector_package

  if ! sudo -u postgres psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='${escaped_db_user}'" | grep -q 1; then
    sudo -u postgres psql -c "CREATE USER \"${POSTGRES_USER}\" WITH PASSWORD '${escaped_db_password}';"
  fi

  if ! sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='${escaped_db_name}'" | grep -q 1; then
    sudo -u postgres psql -c "CREATE DATABASE \"${POSTGRES_DB}\" OWNER \"${POSTGRES_USER}\";"
  fi

  sudo -u postgres psql -c "GRANT ALL PRIVILEGES ON DATABASE \"${POSTGRES_DB}\" TO \"${POSTGRES_USER}\";"
  sudo -u postgres psql -d "${POSTGRES_DB}" -c "CREATE EXTENSION IF NOT EXISTS vector;"
}

setup_nats() {
  log "Configurando NATS"

  local nats_conf
  nats_conf="$(resolve_nats_conf_path)"

  sudo install -d -m 755 -o root -g root "$(dirname "$nats_conf")"

  local auth_block
  if [[ "$NATS_AUTH_CALLOUT_ENABLED" == "1" ]]; then
    auth_block=$(cat <<EOF
authorization {
  timeout: 1
  users = [
    { user: "$NATS_AUTH_USER", password: "$NATS_AUTH_PASSWORD" }
  ]
  auth_callout {
    issuer: "$NATS_AUTH_CALLOUT_ISSUER"
    auth_users: [ "$NATS_AUTH_USER" ]
  }
}
EOF
)
  else
    auth_block=$(cat <<EOF
authorization {
  timeout: 1
  users = [
    { user: "$NATS_AUTH_USER", password: "$NATS_AUTH_PASSWORD" }
  ]
}
EOF
)
  fi

  sudo tee "$nats_conf" >/dev/null <<EOF
listen: ${NATS_BIND_HOST}:4222
http: ${NATS_MONITOR_HOST}:8222
server_name: discovery-nats
${auth_block}
EOF

  sudo chmod 640 "$nats_conf"
  sudo chown root:nats "$nats_conf" || true

  sudo systemctl enable nats-server
  sudo systemctl restart nats-server
}

setup_internal_certificate() {
  [[ "$ACCESS_MODE" == "internal" || "$ACCESS_MODE" == "hybrid" ]] || return 0

  log "Gerando certificado self-signed para acesso interno"

  local san_entry="DNS:$INTERNAL_API_HOST"
  if [[ "$INTERNAL_API_HOST" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    san_entry="IP:$INTERNAL_API_HOST"
  fi

  local cert_conf
  cert_conf="$(mktemp)"
  cat > "$cert_conf" <<EOF
[req]
default_bits = 4096
prompt = no
default_md = sha256
distinguished_name = dn
x509_extensions = v3_req

[dn]
CN = $INTERNAL_API_HOST
O = Discovery

[v3_req]
subjectAltName = $san_entry
extendedKeyUsage = serverAuth
keyUsage = digitalSignature, keyEncipherment
EOF

  sudo rm -f /etc/discovery-api/certs/api-internal.key /etc/discovery-api/certs/api-internal.crt

  sudo openssl req -x509 -nodes -days 825 \
    -newkey rsa:4096 \
    -keyout /etc/discovery-api/certs/api-internal.key \
    -out /etc/discovery-api/certs/api-internal.crt \
    -config "$cert_conf"

  rm -f "$cert_conf"

  sudo chmod 640 /etc/discovery-api/certs/api-internal.key
  sudo chmod 644 /etc/discovery-api/certs/api-internal.crt
  sudo chown root:discovery-api /etc/discovery-api/certs/api-internal.key
}

setup_cloudflare_tunnel() {
  [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]] || return 0

  log "Instalando e configurando cloudflared"

  if ! command -v cloudflared >/dev/null 2>&1; then
    curl -fsSL https://pkg.cloudflare.com/cloudflare-main.gpg | sudo gpg --yes --dearmor -o /usr/share/keyrings/cloudflare-main.gpg
    echo "deb [signed-by=/usr/share/keyrings/cloudflare-main.gpg] https://pkg.cloudflare.com/cloudflared $(lsb_release -cs) main" | sudo tee /etc/apt/sources.list.d/cloudflared.list >/dev/null
    sudo apt-get update -y
    sudo apt-get install -y cloudflared
  fi

  sudo cloudflared service install "$CLOUDFLARE_TUNNEL_TOKEN"
  sudo systemctl enable cloudflared
  sudo systemctl restart cloudflared
}

publish_api() {
  log "Publicando Discovery.Api"

  local release_id
  release_id="$(date +%Y%m%d%H%M%S)-initial"
  local release_dir="$DISCOVERY_API_RELEASES/$release_id"

  sudo -u discovery-api mkdir -p "$release_dir"
  sudo -u discovery-api dotnet publish "$DISCOVERY_API_SOURCE/src/Discovery.Api/Discovery.Api.csproj" \
    -c Release \
    -r "$DISCOVERY_DOTNET_RUNTIME" \
    --self-contained false \
    -o "$release_dir" \
    /p:UseAppHost=true

  sudo -u discovery-api rm -f "$release_dir"/appsettings*.json || true
  sudo -u discovery-api ln -sfn "$release_dir" "$DISCOVERY_API_CURRENT"

  [[ -x "$release_dir/Discovery.Api" ]] || fail "Binario Discovery.Api nao gerado"
}

write_environment_file() {
  log "Escrevendo arquivo de ambiente da API"

  local public_host
  if [[ "$ACCESS_MODE" == "external" ]]; then
    public_host="$EXTERNAL_API_HOST"
  elif [[ "$ACCESS_MODE" == "hybrid" ]]; then
    public_host="$EXTERNAL_API_HOST"
  else
    public_host="$INTERNAL_API_HOST"
  fi

  local cert_env=""
  if [[ "$ACCESS_MODE" == "internal" || "$ACCESS_MODE" == "hybrid" ]]; then
    cert_env=$(cat <<'EOF'
ASPNETCORE_Kestrel__Certificates__Default__Path=/etc/discovery-api/certs/api-internal.crt
ASPNETCORE_Kestrel__Certificates__Default__KeyPath=/etc/discovery-api/certs/api-internal.key
EOF
)
  fi

  sudo tee /etc/discovery-api/discovery.env >/dev/null <<EOF
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:8080;https://0.0.0.0:8443
ConnectionStrings__DefaultConnection=Host=127.0.0.1;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
Nats__Url=nats://${NATS_AUTH_USER}:${NATS_AUTH_PASSWORD}@127.0.0.1:4222
Nats__AuthUser=${NATS_AUTH_USER}
Nats__AuthPassword=${NATS_AUTH_PASSWORD}
Nats__AuthCallout__Enabled=$( [[ "$NATS_AUTH_CALLOUT_ENABLED" == "1" ]] && echo true || echo false )
Nats__AuthCallout__Subject=${NATS_AUTH_CALLOUT_SUBJECT}
AgentPackage__PublicApiScheme=https
AgentPackage__PublicApiServer=${public_host}
DISCOVERY_API_BASE=${DISCOVERY_API_BASE}
DISCOVERY_API_SOURCE=${DISCOVERY_API_SOURCE}
DISCOVERY_API_RELEASES=${DISCOVERY_API_RELEASES}
DISCOVERY_API_CURRENT=${DISCOVERY_API_CURRENT}
DISCOVERY_GIT_REPO=${DISCOVERY_GIT_REPO}
DISCOVERY_GIT_BRANCH=${DISCOVERY_GIT_BRANCH}
DISCOVERY_GIT_TOKEN_FILE=/etc/discovery-api/github.token
DISCOVERY_DOTNET_RUNTIME=${DISCOVERY_DOTNET_RUNTIME}
${cert_env}
EOF

  sudo chmod 640 /etc/discovery-api/discovery.env
  sudo chown root:discovery-api /etc/discovery-api/discovery.env

  sudo tee /etc/discovery-api/github.token >/dev/null <<EOF
${GITHUB_PAT}
EOF
  sudo chmod 640 /etc/discovery-api/github.token
  sudo chown root:discovery-api /etc/discovery-api/github.token
}

install_selfupdate_script() {
  local source_script="$SCRIPT_DIR/selfupdate_discovery_api.sh"
  local target_script="$DISCOVERY_OPS_DIR/selfupdate-discovery-api.sh"

  [[ -f "$source_script" ]] || fail "Script de self-update nao encontrado em $source_script"

  sudo install -m 750 -o discovery-api -g discovery-api "$source_script" "$target_script"
}

write_systemd_service() {
  log "Criando servico systemd discovery-api"

  sudo tee /etc/systemd/system/discovery-api.service >/dev/null <<EOF
[Unit]
Description=Discovery API
After=network-online.target postgresql.service nats-server.service
Wants=network-online.target

[Service]
Type=simple
User=discovery-api
Group=discovery-api
WorkingDirectory=${DISCOVERY_API_CURRENT}
EnvironmentFile=/etc/discovery-api/discovery.env
ExecStartPre=${DISCOVERY_OPS_DIR}/selfupdate-discovery-api.sh
ExecStart=${DISCOVERY_API_CURRENT}/Discovery.Api
Restart=always
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF

  sudo systemctl daemon-reload
  sudo systemctl enable discovery-api
  sudo systemctl restart discovery-api
}

run_db_migrations() {
  log "Aplicando migracoes"
  sudo -u discovery-api bash -c "set -a; source /etc/discovery-api/discovery.env; set +a; dotnet '${DISCOVERY_API_CURRENT}/Discovery.Migrations.dll'" || true
}

show_summary() {
  log "Instalacao concluida"
  echo
  echo "Resumo:"
  echo "- API base: $DISCOVERY_API_BASE"
  echo "- API current: $DISCOVERY_API_CURRENT"
  echo "- Agent source: $DISCOVERY_AGENT_SRC"
  echo "- Agent artifacts: $DISCOVERY_AGENT_ARTIFACTS"
  echo "- Access mode: $ACCESS_MODE"
  if [[ "$ACCESS_MODE" == "internal" || "$ACCESS_MODE" == "hybrid" ]]; then
    echo "- Host interno: $INTERNAL_API_HOST"
  fi
  if [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]]; then
    echo "- Host externo: $EXTERNAL_API_HOST"
  fi
  echo
  echo "Verificacoes recomendadas:"
  echo "1) sudo systemctl status discovery-api --no-pager"
  echo "2) sudo systemctl status nats-server --no-pager"
  echo "3) sudo systemctl status postgresql --no-pager"
  echo "4) curl -k https://127.0.0.1:8443/health"
}

main() {
  select_operation_mode

  if [[ "$UPDATE_NATS_CONFIG_ONLY" -eq 1 ]]; then
    apply_nats_reconfiguration_only
    return
  fi

  prompt_if_empty GITHUB_PAT "GitHub PAT (bootstrap, sera salvo localmente para self-update)" 1
  prompt_if_empty DISCOVERY_GIT_REPO "URL do repositorio da API"
  prompt_if_empty DISCOVERY_AGENT_GIT_REPO "URL do repositorio do Agent (build)"
  select_install_branch
  prompt_if_empty ACCESS_MODE "Modo de acesso (internal/external/hybrid)" 0 "internal"

  normalize_access_mode

  if [[ "$ACCESS_MODE" == "internal" || "$ACCESS_MODE" == "hybrid" ]]; then
    prompt_if_empty INTERNAL_API_HOST "IP ou hostname interno da API"
  fi
  if [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]]; then
    prompt_if_empty EXTERNAL_API_HOST "Hostname externo da API (Cloudflare)"
    prompt_if_empty CLOUDFLARE_TUNNEL_TOKEN "Cloudflare Tunnel token" 1
  fi

  prompt_if_empty POSTGRES_DB "Nome do database PostgreSQL" 0 "discovery"
  prompt_if_empty POSTGRES_USER "Usuario PostgreSQL" 0 "discovery_app"
  prompt_if_empty POSTGRES_PASSWORD "Senha PostgreSQL" 1

  prompt_nats_configuration

  validate_security_inputs
  normalize_nats_settings

  DISCOVERY_API_BASE="${DISCOVERY_API_BASE:-/opt/discovery-api}"
  DISCOVERY_AGENT_SRC="${DISCOVERY_AGENT_SRC:-/opt/discovery-agent-src}"
  DISCOVERY_AGENT_ARTIFACTS="${DISCOVERY_AGENT_ARTIFACTS:-/opt/discovery-agent-artifacts}"
  DISCOVERY_OPS_DIR="${DISCOVERY_OPS_DIR:-/opt/discovery-ops}"
  DISCOVERY_DOTNET_RUNTIME="${DISCOVERY_DOTNET_RUNTIME:-linux-x64}"

  DISCOVERY_API_RELEASES="${DISCOVERY_API_BASE}/releases"
  DISCOVERY_API_SHARED="${DISCOVERY_API_BASE}/shared"
  DISCOVERY_API_SOURCE="${DISCOVERY_API_BASE}/source"
  DISCOVERY_API_CURRENT="${DISCOVERY_API_BASE}/current"

  install_apt_dependencies
  ensure_dotnet_sdk
  ensure_service_user
  create_directories

  setup_postgres
  setup_nats
  setup_internal_certificate
  setup_cloudflare_tunnel
  write_environment_file

  trap cleanup_on_exit EXIT
  setup_git_askpass
  clone_or_update_repo "$DISCOVERY_GIT_REPO" "$DISCOVERY_API_SOURCE"
  clone_or_update_repo "$DISCOVERY_AGENT_GIT_REPO" "$DISCOVERY_AGENT_SRC"

  publish_api
  install_selfupdate_script
  write_systemd_service
  run_db_migrations
  show_summary
}

main "$@"
