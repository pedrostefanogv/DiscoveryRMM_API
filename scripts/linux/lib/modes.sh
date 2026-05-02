# Discovery RMM installer – operation modes (update-stack, update-nats, maintenance)
# Requires: common.sh, install.sh, services.sh, deploy.sh, prompts.sh, normalize.sh, certs.sh

apply_stack_update_only() {
  set_log_context "update"
  log "Modo de update da stack: atualizando repositorios e rebuildando API/portal"

  DISCOVERY_GIT_REPO="${DISCOVERY_GIT_REPO:-https://github.com/pedrostefanogv/DiscoveryRMM_API}"
  DISCOVERY_AGENT_GIT_REPO="${DISCOVERY_AGENT_GIT_REPO:-https://github.com/pedrostefanogv/DiscoveryRMM_Agent}"
  DISCOVERY_SITE_GIT_REPO="${DISCOVERY_SITE_GIT_REPO:-https://github.com/pedrostefanogv/DiscoveryRMM_Site}"
  DISCOVERY_GIT_BRANCH="${DISCOVERY_GIT_BRANCH:-${DISCOVERY_RELEASE_CHANNEL:-release}}"
  log "Canal/branch selecionado para update: ${DISCOVERY_GIT_BRANCH}"

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

  DISCOVERY_SITE_API_URL="${DISCOVERY_SITE_API_URL:-}"
  if [[ -z "${DISCOVERY_CLEAN_BUILD:-}" ]] && sudo test -f /etc/discovery-api/discovery.env; then
    DISCOVERY_CLEAN_BUILD="$(sudo awk -F= '/^DISCOVERY_CLEAN_BUILD=/{sub("^[^=]*=",""); print; exit}' /etc/discovery-api/discovery.env 2>/dev/null || true)"
  fi
  case "${DISCOVERY_CLEAN_BUILD:-1}" in 0|1) ;; *) DISCOVERY_CLEAN_BUILD="1" ;; esac
  load_existing_site_realtime_defaults
  normalize_site_realtime_settings
  update_site_realtime_environment_file

  DISCOVERY_API_RELEASES="${DISCOVERY_API_BASE}/releases"
  DISCOVERY_API_SHARED="${DISCOVERY_API_BASE}/shared"
  DISCOVERY_API_SOURCE="${DISCOVERY_API_BASE}/source"
  DISCOVERY_API_CURRENT="${DISCOVERY_API_BASE}/current"
  DISCOVERY_SITE_RELEASES="${DISCOVERY_SITE_BASE}/releases"
  DISCOVERY_SITE_SOURCE="${DISCOVERY_SITE_BASE}/source"
  DISCOVERY_SITE_CURRENT="${DISCOVERY_SITE_BASE}/current"

  ensure_dotnet_sdk
  ensure_nodejs
  ensure_service_user
  create_directories

  trap cleanup_on_exit EXIT
  setup_git_askpass
  clone_or_update_repo "$DISCOVERY_GIT_REPO" "$DISCOVERY_API_SOURCE"
  clone_or_update_repo "$DISCOVERY_AGENT_GIT_REPO" "$DISCOVERY_AGENT_SRC"
  clone_or_update_repo "$DISCOVERY_SITE_GIT_REPO" "$DISCOVERY_SITE_SOURCE"

  publish_api
  publish_site
  install_selfupdate_script
  write_site_proxy_config

  if sudo systemctl list-unit-files discovery-api.service >/dev/null 2>&1; then
    sudo systemctl restart discovery-api || warn "Falha ao reiniciar discovery-api"
  else warn "Servico discovery-api nao encontrado; pulando restart"; fi
  if sudo systemctl list-unit-files nginx.service >/dev/null 2>&1; then
    sudo systemctl restart nginx || warn "Falha ao reiniciar nginx"
  fi

  log "Update da stack concluido"
}

apply_nats_reconfiguration_only() {
  set_log_context "update-nats"
  log "Modo de atualizacao NATS: reconfigurando NATS e variaveis da API"

  prompt_nats_configuration
  validate_security_inputs
  normalize_nats_settings
  normalize_site_realtime_settings
  load_existing_nats_defaults
  generate_nats_account_keys

  setup_nats
  update_nats_environment_file
  update_site_realtime_environment_file

  if sudo systemctl is-active --quiet discovery-api; then
    log "Reiniciando discovery-api para aplicar novas configuracoes NATS"
    sudo systemctl restart discovery-api
  fi

  log "Atualizacao de configuracao NATS concluida"
}

load_existing_maintenance_defaults() {
  local env_file="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"

  if sudo test -f "$env_file"; then
    local env_api_base env_api_current
    env_api_base="$(sudo awk -F= '/^DISCOVERY_API_BASE=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)"
    env_api_current="$(sudo awk -F= '/^DISCOVERY_API_CURRENT=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)"

    if [[ -z "${DISCOVERY_API_BASE:-}" && -n "$env_api_base" ]]; then DISCOVERY_API_BASE="$env_api_base"; fi
    if [[ -z "${DISCOVERY_API_CURRENT:-}" && -n "$env_api_current" ]]; then DISCOVERY_API_CURRENT="$env_api_current"; fi
  fi

  DISCOVERY_API_BASE="${DISCOVERY_API_BASE:-/opt/discovery-api}"
  DISCOVERY_API_CURRENT="${DISCOVERY_API_CURRENT:-${DISCOVERY_API_BASE}/current}"
}

prompt_maintenance_login() {
  local prompt_text="${1:-Login alvo [admin]: }"
  local default_login="${2:-admin}"
  local input

  read -r -p "$prompt_text" input
  input="$(printf '%s' "$input" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  if [[ -z "$input" ]]; then input="$default_login"; fi
  printf '%s' "$input"
}

prompt_maintenance_password_optional() {
  local password_input
  read -r -s -p "Nova senha (Enter = gerar automaticamente): " password_input
  echo
  printf '%s' "$password_input"
}

run_recover_admin_maintenance() {
  local target_login="$1"
  local target_password="${2:-}"
  local reset_mfa="${3:-1}"
  local create_if_missing="${4:-1}"
  local reactivate_user="${5:-1}"
  local api_env_file="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"
  local api_current="${DISCOVERY_API_CURRENT:-/opt/discovery-api/current}"

  [[ -n "$target_login" ]] || fail "Login alvo nao informado para recover-admin."
  if ! sudo -u discovery-api test -x "$api_current/Discovery.Api"; then
    fail "Binario da API nao encontrado em $api_current/Discovery.Api"
  fi
  if ! sudo -u discovery-api test -r "$api_env_file"; then
    fail "Arquivo de ambiente da API nao encontrado ou sem leitura para discovery-api: $api_env_file"
  fi

  local output
  output="$(sudo -u discovery-api env \
    DISCOVERY_API_MAINTENANCE_ENV_FILE="$api_env_file" \
    DISCOVERY_API_MAINTENANCE_CURRENT="$api_current" \
    DISCOVERY_API_BOOTSTRAP_LOGIN="$target_login" \
    DISCOVERY_API_BOOTSTRAP_PASSWORD="$target_password" \
    DISCOVERY_API_RECOVER_RESET_MFA="$reset_mfa" \
    DISCOVERY_API_RECOVER_CREATE_IF_MISSING="$create_if_missing" \
    DISCOVERY_API_RECOVER_REACTIVATE="$reactivate_user" \
    bash -lc '
      set -euo pipefail
      while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'"'"'\r'"'"'}"
        case "$line" in ""|\#*) continue ;; esac
        export "$line"
      done < "$DISCOVERY_API_MAINTENANCE_ENV_FILE"

      cd "$DISCOVERY_API_MAINTENANCE_CURRENT"
      export Logging__LogLevel__Default=Warning
      export Logging__LogLevel__Microsoft=Warning
      export Logging__LogLevel__Microsoft__EntityFrameworkCore=Warning
      export Logging__LogLevel__FluentMigrator=Warning

      recover_args=("$DISCOVERY_API_MAINTENANCE_CURRENT/Discovery.Api" --recover-admin --login "$DISCOVERY_API_BOOTSTRAP_LOGIN")
      if [[ "$DISCOVERY_API_RECOVER_RESET_MFA" == "1" ]]; then recover_args+=(--reset-mfa); else recover_args+=(--keep-mfa); fi
      if [[ "$DISCOVERY_API_RECOVER_CREATE_IF_MISSING" == "1" ]]; then recover_args+=(--create-if-missing); else recover_args+=(--no-create-if-missing); fi
      if [[ "$DISCOVERY_API_RECOVER_REACTIVATE" == "1" ]]; then recover_args+=(--reactivate); else recover_args+=(--no-reactivate); fi

      if [[ -n "${DISCOVERY_API_BOOTSTRAP_PASSWORD:-}" ]]; then
        printf "%s\n" "$DISCOVERY_API_BOOTSTRAP_PASSWORD" | "${recover_args[@]}" --password-stdin
      else
        "${recover_args[@]}"
      fi
    ' 2>&1)" || {
    local exit_code=$?
    fail "recover-admin falhou com codigo ${exit_code}:\n${output}"
  }

  printf '%s\n' "$output"
}

print_recover_admin_help() {
  local api_env_file="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"
  local api_current="${DISCOVERY_API_CURRENT:-/opt/discovery-api/current}"

  if ! sudo -u discovery-api test -x "$api_current/Discovery.Api"; then
    fail "Binario da API nao encontrado em $api_current/Discovery.Api"
  fi
  if ! sudo -u discovery-api test -r "$api_env_file"; then
    fail "Arquivo de ambiente da API nao encontrado ou sem leitura para discovery-api: $api_env_file"
  fi

  sudo -u discovery-api env \
    DISCOVERY_API_MAINTENANCE_ENV_FILE="$api_env_file" \
    DISCOVERY_API_MAINTENANCE_CURRENT="$api_current" \
    bash -lc '
      set -euo pipefail
      while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'"'"'\r'"'"'}"
        case "$line" in ""|\#*) continue ;; esac
        export "$line"
      done < "$DISCOVERY_API_MAINTENANCE_ENV_FILE"
      cd "$DISCOVERY_API_MAINTENANCE_CURRENT"
      "$DISCOVERY_API_MAINTENANCE_CURRENT/Discovery.Api" --recover-admin-help
    '
}

pause_maintenance_menu() {
  echo
  echo "Pressione Enter para voltar ao menu de manutencao..."
  read -r _
}

apply_maintenance_mode() {
  set_log_context "maintenance"
  load_existing_maintenance_defaults

  local api_env_file="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"
  if ! sudo test -f "$api_env_file"; then
    fail "Arquivo de ambiente da API nao encontrado: $api_env_file. Execute a instalacao completa antes do modo de manutencao."
  fi

  if [[ "${NON_INTERACTIVE:-0}" -eq 1 ]]; then
    fail "Modo de manutencao exige terminal interativo. Remova --non-interactive para usar esse menu."
  fi

  while true; do
    wizard_header "Manutencao avancada" "$(wizard_step_label "3/8" "2/7")"
    echo "Escolha a acao administrativa desejada:"
    echo "1) Resetar senha + MFA de um usuario (reativa e cria se ausente)"
    echo "2) Resetar senha mantendo MFA (reativa e cria se ausente)"
    echo "3) Recriar/garantir admin padrao (login admin, senha automatica, reset MFA)"
    echo "4) Ver ajuda completa do recover-admin"
    echo "0) Sair"
    echo "----------------------------------------"

    local selected_option
    read -r -p "Opcao [1]: " selected_option
    selected_option="${selected_option:-1}"
    selected_option="$(printf '%s' "$selected_option" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"

    case "$selected_option" in
      1)
        local target_login target_password
        target_login="$(prompt_maintenance_login "Login alvo [admin]: " "admin")"
        target_password="$(prompt_maintenance_password_optional)"
        log "Executando recover-admin para '$target_login' com reset de MFA"
        run_recover_admin_maintenance "$target_login" "$target_password" "1" "1" "1"
        pause_maintenance_menu ;;
      2)
        local keep_login keep_password
        keep_login="$(prompt_maintenance_login "Login alvo [admin]: " "admin")"
        keep_password="$(prompt_maintenance_password_optional)"
        log "Executando recover-admin para '$keep_login' mantendo MFA atual"
        run_recover_admin_maintenance "$keep_login" "$keep_password" "0" "1" "1"
        pause_maintenance_menu ;;
      3)
        log "Executando recover-admin para login admin (cria se ausente)"
        run_recover_admin_maintenance "admin" "" "1" "1" "1"
        pause_maintenance_menu ;;
      4)
        print_recover_admin_help
        pause_maintenance_menu ;;
      0|sair|exit|q|quit)
        log "Saindo do modo de manutencao"
        return ;;
      *)
        echo "Opcao invalida: $selected_option" >&2 ;;
    esac
  done
}
