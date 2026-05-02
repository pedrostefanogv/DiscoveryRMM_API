#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT_PATH="$SCRIPT_DIR/$(basename "${BASH_SOURCE[0]}")"
LIB_DIR="$SCRIPT_DIR/lib"
TEMPLATES_DIR="$SCRIPT_DIR/templates"
NGINX_TEMPLATE_PATH="$TEMPLATES_DIR/nginx-discovery.conf.tpl"
SELFUPDATE_TEMPLATE_PATH="$TEMPLATES_DIR/selfupdate-discovery-api.sh"
ZEROSSL_ACME_TEMPLATE_PATH="$TEMPLATES_DIR/zerossl-acme-certificate.sh"

NON_INTERACTIVE=0
CONFIG_FILE=""
UPDATE_NATS_CONFIG_ONLY=0
UPDATE_STACK_ONLY=0
MAINTENANCE_MODE=0
INSTALL_MODE=""
SUDO_KEEPALIVE_PID=""
LOG_CONTEXT="install"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --non-interactive) NON_INTERACTIVE=1; shift ;;
    --config)
      CONFIG_FILE="${2:-}"
      [[ -n "$CONFIG_FILE" ]] || { echo "Parametro --config exige caminho de arquivo" >&2; exit 1; }
      shift 2 ;;
    --update-nats-config|--nats-only) UPDATE_NATS_CONFIG_ONLY=1; shift ;;
    --update-stack|--rebuild-only|--upgrade) UPDATE_STACK_ONLY=1; shift ;;
    --maintenance|--advanced) MAINTENANCE_MODE=1; shift ;;
    --mode)
      INSTALL_MODE="${2:-}"
      [[ -n "$INSTALL_MODE" ]] || { echo "Parametro --mode exige valor (full|nats|update|upgrade|maintenance)" >&2; exit 1; }
      shift 2 ;;
    *)
      echo "Parametro invalido: $1" >&2; exit 1 ;;
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
GITHUB_PAT="${GITHUB_PAT:-}"

# ── Source library modules in dependency order ─────────────────────────────
# shellcheck source=lib/common.sh
source "$LIB_DIR/common.sh"
# shellcheck source=lib/normalize.sh
source "$LIB_DIR/normalize.sh"
# shellcheck source=lib/install.sh
source "$LIB_DIR/install.sh"
# shellcheck source=lib/services.sh
source "$LIB_DIR/services.sh"
# shellcheck source=lib/certs.sh
source "$LIB_DIR/certs.sh"
# shellcheck source=lib/prompts.sh
source "$LIB_DIR/prompts.sh"
# shellcheck source=lib/deploy.sh
source "$LIB_DIR/deploy.sh"
# shellcheck source=lib/modes.sh
source "$LIB_DIR/modes.sh"

# ── Bootstrap ──────────────────────────────────────────────────────────────

initialize_log_context_from_requested_mode
require_cmd sudo
start_sudo_keepalive
trap cleanup_sudo_keepalive EXIT

# ── Full installation wizard ──────────────────────────────────────────────

main() {
  initialize_log_context_from_requested_mode
  select_operation_mode

  if [[ "$UPDATE_NATS_CONFIG_ONLY" -eq 1 ]]; then apply_nats_reconfiguration_only; return; fi
  if [[ "$UPDATE_STACK_ONLY" -eq 1 ]]; then apply_stack_update_only; return; fi
  if [[ "$MAINTENANCE_MODE" -eq 1 ]]; then apply_maintenance_mode; return; fi

  wizard_header "Repositorios" "$(wizard_step_label "3/8" "2/7")"
  echo "Informe os repositorios Git que serao clonados."
  echo "API: backend principal (DiscoveryRMM_API)."
  echo "Agent: codigo do agente para build."
  echo "Site: portal web/console administrativo."
  echo "Exemplos:"
  echo "- https://github.com/OWNER/DiscoveryRMM_API.git"
  echo "- git@github.com:OWNER/DiscoveryRMM_API.git"
  echo "----------------------------------------"
  prompt_repo_url DISCOVERY_GIT_REPO "URL do repositorio da API" "https://github.com/pedrostefanogv/DiscoveryRMM_API"
  prompt_repo_url DISCOVERY_AGENT_GIT_REPO "URL do repositorio do Agent (build)" "https://github.com/pedrostefanogv/DiscoveryRMM_Agent"
  prompt_repo_url DISCOVERY_SITE_GIT_REPO "URL do repositorio do portal web" "https://github.com/pedrostefanogv/DiscoveryRMM_Site"
  select_install_branch

  wizard_header "Acesso do portal/API" "$(wizard_step_label "5/8" "4/7")"
  echo "1) internal - acesso somente na rede interna."
  echo "2) external - acesso somente via Cloudflare Tunnel."
  echo "3) hybrid   - interno e externo ao mesmo tempo."
  echo "----------------------------------------"
  prompt_if_empty ACCESS_MODE "Modo de acesso (1/2/3 ou internal/external/hybrid)" 0 "internal"
  normalize_access_mode

  if [[ "$ACCESS_MODE" == "internal" || "$ACCESS_MODE" == "hybrid" ]]; then
    echo; echo "Endereco interno usado por agentes/UI na rede local."
    local internal_ip; internal_ip="$(detect_internal_ipv4)"
    if [[ -n "$internal_ip" ]]; then
      prompt_if_empty INTERNAL_API_HOST "IP ou hostname interno da API" 0 "$internal_ip"
    else prompt_if_empty INTERNAL_API_HOST "IP ou hostname interno da API"; fi
  fi
  if [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]]; then
    echo; echo "Endereco externo publicado via Cloudflare (ex: api.suaempresa.com)."
    prompt_if_empty EXTERNAL_API_HOST "Hostname externo da API (Cloudflare)"
    echo "Token do Cloudflare Tunnel para publicar a API."
    prompt_if_empty CLOUDFLARE_TUNNEL_TOKEN "Cloudflare Tunnel token" 1
  fi

  prompt_tls_certificate_configuration

  wizard_header "Documentacao OpenAPI (Scalar)" "$(wizard_step_label "7/9" "6/8")"
  echo "Habilite ou nao a documentacao OpenAPI (Scalar) na API."
  echo "Se OpenAPI estiver desativado, os endpoints /openapi e /scalar ficam indisponiveis."
  echo "----------------------------------------"
  prompt_if_empty OPENAPI_ENABLED "Habilitar OpenAPI/Scalar? (y/N)" 0 "n"
  normalize_openapi_enabled
  prompt_if_empty OPENAPI_SCALAR_ENABLED "Habilitar UI do Scalar em producao? (y/N)" 0 "$OPENAPI_ENABLED"
  normalize_openapi_scalar_enabled

  wizard_header "Banco de dados (PostgreSQL)" "$(wizard_step_label "8/9" "7/8")"
  echo "O instalador cria o usuario e o database se nao existirem."
  echo "----------------------------------------"
  prompt_if_empty POSTGRES_DB "Nome do database PostgreSQL" 0 "discovery"
  prompt_if_empty POSTGRES_USER "Usuario PostgreSQL" 0 "discovery_app"
  prompt_postgres_password

  prompt_nats_configuration
  prompt_selfupdate_settings

  validate_security_inputs
  normalize_nats_settings

  DISCOVERY_API_BASE="${DISCOVERY_API_BASE:-/opt/discovery-api}"
  DISCOVERY_SITE_BASE="${DISCOVERY_SITE_BASE:-/opt/discovery-site}"
  DISCOVERY_AGENT_SRC="${DISCOVERY_AGENT_SRC:-/opt/discovery-agent-src}"
  DISCOVERY_AGENT_ARTIFACTS="${DISCOVERY_AGENT_ARTIFACTS:-/opt/discovery-agent-artifacts}"
  DISCOVERY_OPS_DIR="${DISCOVERY_OPS_DIR:-/opt/discovery-ops}"

  local detected_arch; local detected_dotnet_runtime
  detected_arch="$(detect_system_architecture)"
  if detected_dotnet_runtime="$(map_arch_to_dotnet_runtime "$detected_arch")"; then :; else
    detected_dotnet_runtime="linux-x64"
    warn "Arquitetura nao mapeada (${detected_arch:-desconhecida}); usando runtime padrao linux-x64"
  fi

  DISCOVERY_DETECTED_ARCH="$detected_arch"
  DISCOVERY_DETECTED_DOTNET_RUNTIME="$detected_dotnet_runtime"
  if [[ -z "${DISCOVERY_DOTNET_RUNTIME:-}" ]]; then DISCOVERY_DOTNET_RUNTIME="$detected_dotnet_runtime"; fi
  validate_dotnet_runtime "$DISCOVERY_DOTNET_RUNTIME"

  log "Arquitetura detectada: ${DISCOVERY_DETECTED_ARCH:-desconhecida}"
  log "Runtime .NET da API: ${DISCOVERY_DOTNET_RUNTIME}"
  log "Build do Agent permanece Windows x86/x64 nesta fase"
  DISCOVERY_SITE_API_URL="${DISCOVERY_SITE_API_URL:-}"
  normalize_site_realtime_settings

  DISCOVERY_API_RELEASES="${DISCOVERY_API_BASE}/releases"
  DISCOVERY_API_SHARED="${DISCOVERY_API_BASE}/shared"
  DISCOVERY_API_SOURCE="${DISCOVERY_API_BASE}/source"
  DISCOVERY_API_CURRENT="${DISCOVERY_API_BASE}/current"
  DISCOVERY_SITE_RELEASES="${DISCOVERY_SITE_BASE}/releases"
  DISCOVERY_SITE_SOURCE="${DISCOVERY_SITE_BASE}/source"
  DISCOVERY_SITE_CURRENT="${DISCOVERY_SITE_BASE}/current"

  confirm_installation

  install_apt_dependencies
  ensure_dotnet_sdk
  ensure_nodejs
  ensure_service_user
  create_directories

  setup_postgres
  setup_redis
  generate_nats_account_keys
  setup_nats
  setup_proxy_certificate
  setup_jwt_signing_keys
  setup_cloudflare_tunnel
  write_environment_file
  setup_zerossl_renewal_timer

  trap cleanup_on_exit EXIT
  setup_git_askpass
  clone_or_update_repo "$DISCOVERY_GIT_REPO" "$DISCOVERY_API_SOURCE"
  clone_or_update_repo "$DISCOVERY_AGENT_GIT_REPO" "$DISCOVERY_AGENT_SRC"
  clone_or_update_repo "$DISCOVERY_SITE_GIT_REPO" "$DISCOVERY_SITE_SOURCE"

  publish_api
  publish_site
  install_selfupdate_script
  write_systemd_service
  write_site_proxy_config
  run_db_migrations
  show_summary
}

main "$@"
