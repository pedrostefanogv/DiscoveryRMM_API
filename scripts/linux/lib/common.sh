# Discovery RMM installer – common utilities
# This file is sourced by install_discovery_server.sh and other lib modules.

set_log_context() {
  local context="${1:-install}"
  LOG_CONTEXT="$context"
}

log() {
  printf '[%s] %s\n' "$LOG_CONTEXT" "$*"
}

warn() {
  printf '[%s][aviso] %s\n' "$LOG_CONTEXT" "$*" >&2
}

fail() {
  printf '[%s][erro] %s\n' "$LOG_CONTEXT" "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || fail "Comando obrigatorio ausente: $1"
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
    amd64|x86_64)  printf 'linux-x64'   ;;
    arm64|aarch64) printf 'linux-arm64' ;;
    *)             return 1              ;;
  esac
}

validate_dotnet_runtime() {
  local runtime="$1"
  case "$runtime" in
    linux-x64|linux-arm64) return ;;
    *) fail "DISCOVERY_DOTNET_RUNTIME invalido: $runtime (use linux-x64 ou linux-arm64)" ;;
  esac
}

wizard_step_label() {
  local legacy_root_step="${1:-}"
  local step_label="${2:-$legacy_root_step}"
  printf '%s' "$step_label"
}

wizard_header() {
  local title="$1"
  local step_label="${2:-}"

  echo
  echo "========================================"
  if [[ -n "$step_label" ]]; then
    echo " Discovery RMM - Etapa $step_label"
  else
    echo " Discovery RMM - Wizard"
  fi
  echo "----------------------------------------"
  echo " $title"
  echo "----------------------------------------"
}

refresh_sudo_credentials() {
  if [[ "${EUID}" -eq 0 ]]; then
    return
  fi

  if sudo -n -v >/dev/null 2>&1; then
    return
  fi

  sudo -n true >/dev/null 2>&1 || fail "sudo sem credencial ativa. Execute: sudo -v"
}

start_sudo_keepalive() {
  if [[ "${EUID}" -eq 0 ]]; then
    return
  fi

  refresh_sudo_credentials
  (
    while true; do
      sleep 60
      sudo -n -v >/dev/null 2>&1 || exit 1
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

generate_random_password() {
  local length="${1:-24}"
  local generated

  set +o pipefail
  generated="$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c "$length")"
  set -o pipefail

  [[ -n "$generated" ]] || fail "Nao foi possivel gerar senha aleatoria."
  printf '%s' "$generated"
}

is_valid_repo_url() {
  local url="$1"
  if [[ "$url" =~ ^(https?|ssh):// ]]; then
    return 0
  fi
  if [[ "$url" =~ ^git@[^:]+:.+\.git$ ]]; then
    return 0
  fi
  return 1
}

detect_internal_ipv4() {
  local ip_candidate=""

  if command -v ip >/dev/null 2>&1; then
    ip_candidate="$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i=1;i<=NF;i++) if ($i=="src") {print $(i+1); exit}}')"
  fi

  if [[ -z "$ip_candidate" ]] && command -v hostname >/dev/null 2>&1; then
    ip_candidate="$(hostname -I 2>/dev/null | awk '{for (i=1;i<=NF;i++) if ($i !~ /^127\./) {print $i; exit}}')"
  fi

  if [[ "$ip_candidate" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf '%s' "$ip_candidate"
  fi
}

build_certificate_san_entry() {
  local host="$1"
  [[ -n "$host" ]] || return 0
  if [[ "$host" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf 'IP:%s' "$host"
  else
    printf 'DNS:%s' "$host"
  fi
}

is_ipv4_address() {
  local value="$1"
  [[ "$value" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]
}

normalize_host_without_scheme() {
  local value="$1"
  value="${value#http://}"
  value="${value#https://}"
  value="${value%%/*}"
  printf '%s' "$value"
}

build_https_origin_from_host() {
  local host="$1"
  [[ -n "$host" ]] || return 0
  host="$(normalize_host_without_scheme "$host")"
  [[ -n "$host" ]] || return 0
  printf 'https://%s' "$host"
}

build_portal_access_url() {
  local host="$1"
  local origin
  origin="$(build_https_origin_from_host "$host")"
  [[ -n "$origin" ]] || return 0
  printf '%s/' "$origin"
}

build_nip_io_host_from_ipv4() {
  local ip="$1"
  [[ -n "$ip" ]] || return 0
  printf '%s.nip.io' "$(printf '%s' "$ip" | tr '.' '-')"
}

resolve_internal_portal_domain() {
  local internal_host="${INTERNAL_API_HOST:-}"
  internal_host="$(normalize_host_without_scheme "$internal_host")"
  [[ -n "$internal_host" ]] || return 0

  if is_ipv4_address "$internal_host"; then
    build_nip_io_host_from_ipv4 "$internal_host"
    return
  fi
  printf '%s' "$internal_host"
}

resolve_fido2_server_domain() {
  local explicit_domain="${DISCOVERY_FIDO2_SERVER_DOMAIN:-}"
  local access_mode="${ACCESS_MODE:-internal}"
  local resolved=""

  if [[ -n "$explicit_domain" ]]; then
    resolved="$(normalize_host_without_scheme "$explicit_domain")"
  elif [[ "$access_mode" == "external" || "$access_mode" == "hybrid" ]]; then
    resolved="$(normalize_host_without_scheme "${EXTERNAL_API_HOST:-}")"
  elif [[ "$access_mode" == "internal" ]]; then
    resolved="$(normalize_host_without_scheme "${INTERNAL_API_HOST:-}")"
  fi

  if [[ -z "$resolved" ]]; then
    resolved="localhost"
  fi

  if is_ipv4_address "$resolved"; then
    local nip_host
    nip_host="$(build_nip_io_host_from_ipv4 "$resolved")"
    warn "FIDO2 ServerDomain informado como IP ($resolved). Convertendo automaticamente para dominio compativel: $nip_host"
    resolved="$nip_host"
  fi

  printf '%s' "$resolved"
}

generate_random_admin_login() {
  local suffix
  suffix="$(generate_random_password 10 | tr '[:upper:]' '[:lower:]')"
  printf 'admin%s' "$suffix"
}

# ── context initialization ─────────────────────────────────────────────────

initialize_log_context_from_requested_mode() {
  local requested_mode="${INSTALL_MODE:-${DISCOVERY_INSTALL_MODE:-}}"
  requested_mode="$(printf '%s' "$requested_mode" | tr '[:upper:]' '[:lower:]')"

  if [[ "${MAINTENANCE_MODE:-0}" -eq 1 ]]; then
    set_log_context "maintenance"
    return
  fi

  if [[ "$UPDATE_STACK_ONLY" -eq 1 ]]; then
    set_log_context "update"
    return
  fi

  if [[ "$UPDATE_NATS_CONFIG_ONLY" -eq 1 ]]; then
    set_log_context "update-nats"
    return
  fi

  case "$requested_mode" in
    update|update-stack|rebuild|upgrade) set_log_context "update"       ;;
    nats|nats-only|update-nats)          set_log_context "update-nats"  ;;
    maintenance|advanced|manutencao)     set_log_context "maintenance"  ;;
    *)                                   set_log_context "install"      ;;
  esac
}

resolve_nats_conf_path() {
  local nats_conf
  nats_conf="$(systemctl cat nats-server 2>/dev/null | sed -n 's/.*-c \([^ ]*\).*/\1/p' | head -n 1)"
  printf '%s' "${nats_conf:-/etc/nats-server.conf}"
}
