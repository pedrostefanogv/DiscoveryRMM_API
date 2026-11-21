#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT_PATH="$SCRIPT_DIR/$(basename "${BASH_SOURCE[0]}")"
LIB_DIR="$SCRIPT_DIR/lib"
TEMPLATES_DIR="$SCRIPT_DIR/templates"
NGINX_TEMPLATE_PATH="$TEMPLATES_DIR/nginx-discovery.conf.tpl"
ZEROSSL_ACME_TEMPLATE_PATH="$TEMPLATES_DIR/zerossl-acme-certificate.sh"
LETSENCRYPT_ACME_TEMPLATE_PATH="$TEMPLATES_DIR/letsencrypt-acme-certificate.sh"

NON_INTERACTIVE=0
CONFIG_FILE=""
UPDATE_NATS_CONFIG_ONLY=0
UPDATE_STACK_ONLY=0
MAINTENANCE_MODE=0
INSTALL_MODE=""
SUDO_KEEPALIVE_PID=""
LOG_CONTEXT="install"
# Args originais preservados para o re-exec como usuario instalador
# (bootstrap_root_execution), que acontece depois do parsing.
ORIGINAL_ARGS=("$@")

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

# â”€â”€ Source library modules in dependency order â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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
# shellcheck source=lib/installer_user.sh
source "$LIB_DIR/installer_user.sh"
# shellcheck source=lib/deploy.sh
source "$LIB_DIR/deploy.sh"
# shellcheck source=lib/modes.sh
source "$LIB_DIR/modes.sh"

# â”€â”€ Bootstrap â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

initialize_log_context_from_requested_mode
validate_host_os
require_cmd sudo

# Se executado como root, cria/reutiliza um usuario comum com sudo e reexecuta
# o instalador como esse usuario (a instalacao nao fica presa ao root).
if [[ "${EUID}" -eq 0 && "${DISCOVERY_ROOT_BOOTSTRAPPED:-0}" != "1" ]]; then
  bootstrap_root_execution
fi

start_sudo_keepalive
trap cleanup_sudo_keepalive EXIT

# â”€â”€ Full installation wizard â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

wizard_step_repos() {
  wizard_header "Repositorios" "$(wizard_step_label "1/10" "1/10")"
  echo "Informe os repositorios Git que serao clonados."
  echo "API: backend principal (DiscoveryRMM_API)."
  echo "Agent: codigo do agente para build."
  echo "Site: portal web/console administrativo."
  echo "Exemplos:"
  echo "- https://github.com/OWNER/DiscoveryRMM_API.git"
  echo "- git@github.com:OWNER/DiscoveryRMM_API.git"
  echo "----------------------------------------"
  prompt_repo_url DISCOVERY_GIT_REPO "URL do repositorio da API" "https://github.com/pedrostefanogv/DiscoveryRMM_API" || return $?
  prompt_repo_url DISCOVERY_AGENT_GIT_REPO "URL do repositorio do Agent (build)" "https://github.com/pedrostefanogv/DiscoveryRMM_Agent" || return $?
  prompt_repo_url DISCOVERY_SITE_GIT_REPO "URL do repositorio do portal web" "https://github.com/pedrostefanogv/DiscoveryRMM_Site" || return $?
}

wizard_step_access() {
  wizard_header "Acesso do portal/API" "$(wizard_step_label "3/10" "3/10")"
  echo "1) internal - acesso somente na rede interna."
  echo "2) external - acesso somente via Cloudflare Tunnel."
  echo "3) hybrid   - interno e externo ao mesmo tempo."
  echo "----------------------------------------"
  while true; do
    prompt_if_empty ACCESS_MODE "Modo de acesso (1/2/3 ou internal/external/hybrid)" 0 "internal" || return $?
    local normalized_mode
    normalized_mode="$(printf '%s' "$ACCESS_MODE" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    case "$normalized_mode" in
      1|internal) ACCESS_MODE="internal"; break ;;
      2|external) ACCESS_MODE="external"; break ;;
      3|hybrid)   ACCESS_MODE="hybrid"; break ;;
      *)
        if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
          fail "ACCESS_MODE invalido: $ACCESS_MODE (use 1/2/3 ou internal/external/hybrid)"
        fi
        echo "Opcao invalida: '$ACCESS_MODE'. Use 1 (internal), 2 (external) ou 3 (hybrid)." >&2
        ACCESS_MODE=""
        ;;
    esac
  done

  if [[ "$ACCESS_MODE" == "internal" ]]; then
    EXTERNAL_API_HOST=""
    CLOUDFLARE_TUNNEL_TOKEN=""
  elif [[ "$ACCESS_MODE" == "external" ]]; then
    INTERNAL_API_HOST=""
  fi

  if [[ "$ACCESS_MODE" == "internal" || "$ACCESS_MODE" == "hybrid" ]]; then
    echo; echo "Endereco interno usado por agentes/UI na rede local."
    local internal_ip; internal_ip="$(detect_internal_ipv4)"
    if [[ -n "$internal_ip" ]]; then
      prompt_if_empty INTERNAL_API_HOST "IP ou hostname interno da API" 0 "$internal_ip" || return $?
    else
      prompt_if_empty INTERNAL_API_HOST "IP ou hostname interno da API" || return $?
    fi
  fi
  if [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]]; then
    echo; echo "Endereco externo publicado via Cloudflare (ex: api.suaempresa.com)."
    prompt_if_empty EXTERNAL_API_HOST "Hostname externo da API (Cloudflare)" || return $?
    prompt_cloudflare_tunnel_token || return $?
  fi
}

wizard_step_openapi() {
  wizard_header "Documentacao OpenAPI (Scalar)" "$(wizard_step_label "6/10" "6/10")"
  echo "OpenAPI publica o contrato da API em /openapi/{versao}.json (rota anonima)."
  echo "Scalar e a UI interativa em /scalar que consome esse contrato e inclui o"
  echo "console 'Try it out' para chamar a API pelo navegador (tambem anonima)."
  echo "Em servidor publico as duas rotas ficam expostas sem login: habilite apenas"
  echo "se precisar mesmo publicar a documentacao/console externamente."
  echo "Se OpenAPI estiver desativado, /openapi e /scalar ficam indisponiveis."
  echo "----------------------------------------"
  prompt_yes_no OPENAPI_ENABLED "Habilitar OpenAPI (contrato)? (y/N)" "n" || return $?
  normalize_openapi_enabled
  # A UI do Scalar e exposicao extra em producao: padrao seguro = N, mesmo com
  # o contrato OpenAPI habilitado (digite y para habilitar).
  prompt_yes_no OPENAPI_SCALAR_ENABLED "Habilitar UI do Scalar em producao? (y/N)" "n" || return $?
  normalize_openapi_scalar_enabled
}

wizard_step_session_and_update() {
  prompt_remote_session_duration || return $?
  prompt_selfupdate_settings || return $?
}

run_full_install_wizard() {
  local step_idx=1
  local max_step=10
  local rc=0
  local prev_back_hint="${WIZARD_SHOW_BACK_HINT:-0}"

  if [[ "${NON_INTERACTIVE:-0}" -eq 0 ]]; then
    WIZARD_SHOW_BACK_HINT=1
  else
    WIZARD_SHOW_BACK_HINT=0
  fi
  WIZARD_REPROMPT=0

  while (( step_idx <= max_step )); do
    rc=0
    case "$step_idx" in
      1)  wizard_step_repos || rc=$? ;;
      2)  select_install_branch || rc=$? ;;
      3)  wizard_step_access || rc=$? ;;
      4)  prompt_tls_certificate_configuration || rc=$? ;;
      5)  prompt_object_storage_configuration || rc=$? ;;
      6)  wizard_step_openapi || rc=$? ;;
      7)  prompt_postgres_configuration || rc=$? ;;
      8)  prompt_nats_configuration || rc=$? ;;
      9)  wizard_step_session_and_update || rc=$? ;;
      10) confirm_installation || rc=$? ;;
    esac

    if (( rc == ${WIZARD_BACK_RC:-10} )); then
      if (( step_idx > 1 )); then
        step_idx=$((step_idx - 1))
        WIZARD_REPROMPT=1
      else
        WIZARD_SHOW_BACK_HINT="$prev_back_hint"
        WIZARD_REPROMPT=0
        return "${WIZARD_BACK_RC:-10}"
      fi
      continue
    elif (( rc == 20 )); then
      step_idx="${WIZARD_JUMP_STEP:-1}"
      WIZARD_REPROMPT=1
      continue
    elif (( rc != 0 )); then
      WIZARD_SHOW_BACK_HINT="$prev_back_hint"
      WIZARD_REPROMPT=0
      return "$rc"
    fi

    WIZARD_REPROMPT=0
    step_idx=$((step_idx + 1))
  done

  WIZARD_SHOW_BACK_HINT="$prev_back_hint"
  WIZARD_REPROMPT=0
}

main() {
  local initial_mode_fixed=0
  if [[ "${UPDATE_NATS_CONFIG_ONLY:-0}" -eq 1 || "${UPDATE_STACK_ONLY:-0}" -eq 1 || "${MAINTENANCE_MODE:-0}" -eq 1 || -n "${INSTALL_MODE:-${DISCOVERY_INSTALL_MODE:-}}" ]]; then
    initial_mode_fixed=1
  fi

  local mode_rc=0
  while true; do
    initialize_log_context_from_requested_mode
    select_operation_mode

    mode_rc=0
    if [[ "$UPDATE_NATS_CONFIG_ONLY" -eq 1 ]]; then
      apply_nats_reconfiguration_only || mode_rc=$?
      if (( mode_rc == ${WIZARD_BACK_RC:-10} )); then
        if [[ "${DISCOVERY_FROM_BOOTSTRAP:-0}" == "1" ]]; then
          exit "${WIZARD_BACK_RC:-10}"
        fi
        if [[ "$initial_mode_fixed" -eq 1 ]]; then
          echo "Modo fixado via parametro; nao e possivel voltar ao menu de operacao." >&2
          continue
        fi
        INSTALL_MODE=""
        DISCOVERY_INSTALL_MODE=""
        UPDATE_NATS_CONFIG_ONLY=0
        UPDATE_STACK_ONLY=0
        MAINTENANCE_MODE=0
        continue
      fi
      return "$mode_rc"
    fi
    if [[ "$UPDATE_STACK_ONLY" -eq 1 ]]; then
      apply_stack_update_only || mode_rc=$?
      if (( mode_rc == ${WIZARD_BACK_RC:-10} )); then
        if [[ "${DISCOVERY_FROM_BOOTSTRAP:-0}" == "1" ]]; then
          exit "${WIZARD_BACK_RC:-10}"
        fi
        if [[ "$initial_mode_fixed" -eq 1 ]]; then
          echo "Modo fixado via parametro; nao e possivel voltar ao menu de operacao." >&2
          continue
        fi
        INSTALL_MODE=""
        DISCOVERY_INSTALL_MODE=""
        UPDATE_NATS_CONFIG_ONLY=0
        UPDATE_STACK_ONLY=0
        MAINTENANCE_MODE=0
        continue
      fi
      return "$mode_rc"
    fi
    if [[ "$MAINTENANCE_MODE" -eq 1 ]]; then
      apply_maintenance_mode || mode_rc=$?
      if (( mode_rc == ${WIZARD_BACK_RC:-10} )); then
        if [[ "${DISCOVERY_FROM_BOOTSTRAP:-0}" == "1" ]]; then
          exit "${WIZARD_BACK_RC:-10}"
        fi
        if [[ "$initial_mode_fixed" -eq 1 ]]; then
          echo "Modo fixado via parametro; nao e possivel voltar ao menu de operacao." >&2
          continue
        fi
        INSTALL_MODE=""
        DISCOVERY_INSTALL_MODE=""
        UPDATE_NATS_CONFIG_ONLY=0
        UPDATE_STACK_ONLY=0
        MAINTENANCE_MODE=0
        continue
      fi
      return "$mode_rc"
    fi

    run_full_install_wizard || mode_rc=$?
    if (( mode_rc == ${WIZARD_BACK_RC:-10} )); then
      if [[ "${DISCOVERY_FROM_BOOTSTRAP:-0}" == "1" ]]; then
        exit "${WIZARD_BACK_RC:-10}"
      fi
      if [[ "$initial_mode_fixed" -eq 1 ]]; then
        echo "Voce ja esta na primeira etapa." >&2
        continue
      fi
      INSTALL_MODE=""
      DISCOVERY_INSTALL_MODE=""
      UPDATE_NATS_CONFIG_ONLY=0
      UPDATE_STACK_ONLY=0
      MAINTENANCE_MODE=0
      continue
    elif (( mode_rc != 0 )); then
      return "$mode_rc"
    fi
    break
  done

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

  if [[ -z "${DISCOVERY_DOTNET_RUNTIME:-}" ]]; then DISCOVERY_DOTNET_RUNTIME="$detected_dotnet_runtime"; fi
  validate_dotnet_runtime "$DISCOVERY_DOTNET_RUNTIME"

  log "Arquitetura detectada: ${detected_arch:-desconhecida}"
  log "Runtime .NET da API: ${DISCOVERY_DOTNET_RUNTIME}"
  # O agente e SEMPRE Windows x86_64, independente da arquitetura do servidor:
  # o servidor ARM64 cross-compila via MinGW (gcc-mingw-w64-x86-64 existe para
  # arm64 nos repositorios Ubuntu/Debian). Os PCs dos clientes nao mudam.
  log "Build do Agent permanece Windows x86/x64 nesta fase (os PCs dos clientes nao mudam)"
  DISCOVERY_SITE_API_URL="${DISCOVERY_SITE_API_URL:-}"
  normalize_site_realtime_settings

  DISCOVERY_API_RELEASES="${DISCOVERY_API_BASE}/releases"
  DISCOVERY_API_SHARED="${DISCOVERY_API_BASE}/shared"
  DISCOVERY_API_SOURCE="${DISCOVERY_API_BASE}/source"
  DISCOVERY_API_CURRENT="${DISCOVERY_API_BASE}/current"
  DISCOVERY_SITE_RELEASES="${DISCOVERY_SITE_BASE}/releases"
  DISCOVERY_SITE_SOURCE="${DISCOVERY_SITE_BASE}/source"
  DISCOVERY_SITE_CURRENT="${DISCOVERY_SITE_BASE}/current"

  check_disk_space "${DISCOVERY_API_BASE:-/opt/discovery-api}" 2048
  check_disk_space "${DISCOVERY_SITE_BASE:-/opt/discovery-site}" 1024

  # Politica padrao das novas instalacoes: o instalador ja verifica/aplica os
  # updates do SO (DISCOVERY_APPLY_SYSTEM_UPDATES=1, definido no wizard) e grava
  # o flag no discovery.env em write_environment_file, para que os updates
  # manuais seguintes continuem atualizando os pacotes do SO.
  apply_system_updates
  install_apt_dependencies
  install_nsis_nsjson_plugin
  ensure_dotnet_sdk
  ensure_nodejs
  ensure_service_user
  ensure_service_user_home
  ensure_winget_clone_dir
  create_directories
  # NOTA: o CLI do Wails NAO e instalado aqui (pre-clone). No fresh install o
  # go.mod do agent ainda nao existe e usaria o fallback. A instalacao correta
  # acontece APOS o clone do agent (logo abaixo), lendo o go.mod real.

  setup_postgres
  setup_redis
  generate_nats_account_keys
  setup_nats
  # O env PRECISA ser escrito antes da emissao do certificado: os templates
  # ACME leem as credenciais (EAB, dominios, hook) do discovery.env — nunca
  # via argv (segredo visivel em /proc/*/cmdline).
  write_environment_file
  setup_proxy_certificate
  setup_jwt_signing_keys
  setup_cloudflare_tunnel
  setup_zerossl_renewal_timer
  setup_letsencrypt_renewal_timer

  trap cleanup_on_exit EXIT
  setup_git_askpass
  clone_or_update_repo "$DISCOVERY_GIT_REPO" "$DISCOVERY_API_SOURCE"
  clone_or_update_repo "$DISCOVERY_AGENT_GIT_REPO" "$DISCOVERY_AGENT_SRC"
  clone_or_update_repo "$DISCOVERY_SITE_GIT_REPO" "$DISCOVERY_SITE_SOURCE"
  # Revalida o CLI do Wails a partir do go.mod do agent recem-clonado.
  ensure_wails_toolchain

  publish_api
  publish_site
  # RustFS local (quando escolhido): instala o servico ANTES de publicar o
  # nginx. Nao atualiza instalacao existente (versao fixada).
  if [[ "${DISCOVERY_OBJECT_STORAGE_MODE:-skip}" == "rustfs" ]]; then
    setup_object_storage_rustfs
  fi
  # Persiste o bloco de storage no discovery.env AGORA: setup_object_storage_rustfs
  # acabou de carregar as credenciais geradas em /etc/default/rustfs, e o
  # write_environment_file (que roda antes) ainda nao as tinha. Sem esta chamada o
  # env fica sem DISCOVERY_OBJECT_STORAGE_ACCESS_KEY/SECRET_KEY (e ObjectStorage__*),
  # o CLI --set-object-storage grava config incompleta no banco e o console acusa
  # "AccessKey/SecretKey obrigatorios". Nos modos update/manutencao isto ja era feito.
  if [[ "${DISCOVERY_OBJECT_STORAGE_MODE:-skip}" != "skip" ]]; then
    update_object_storage_environment_file
  fi
  # Update e sempre manual: remove qualquer automacao remanescente de versoes antigas.
  remove_selfupdate_automation
  write_systemd_service
  write_site_proxy_config
  run_db_migrations
  # Configuracao do storage no banco via CLI da API (segredo criptografado).
  configure_object_storage_in_database
  ensure_nats_fanout_stream
  show_summary
}

main "$@"
