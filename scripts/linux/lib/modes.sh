# Discovery RMM installer – operation modes (update-stack, update-nats, maintenance)
# Requires: common.sh, install.sh, services.sh, deploy.sh, prompts.sh, normalize.sh, certs.sh

# ── Update: shared defaults loader ────────────────────────────────────────

load_update_defaults() {
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
  ensure_service_user_home
  ensure_winget_clone_dir
  create_directories
  # NOTA: o CLI do Wails NAO e instalado aqui (pre-clone) para evitar resolver
  # o go.mod antigo do agent. Ele e instalado/revalidado APOS o clone do agent,
  # em update_agent() e update_all_components(), onde o go.mod ja reflete a
  # branch atual do servidor (upgrade/downgrade v2<->v3 corretos).

  trap cleanup_on_exit EXIT
  setup_git_askpass
}

# ── Update: individual component updaters ──────────────────────────────────

update_api() {
  clone_or_update_repo "$DISCOVERY_GIT_REPO" "$DISCOVERY_API_SOURCE"
  publish_api
  update_remote_access_environment_file
  if [[ "${DISCOVERY_REFRESH_INFRA_CONFIG:-0}" == "1" ]]; then
    log "Atualizando tambem infraestrutura auxiliar (self-update + Nginx) por solicitacao explicita"
    install_selfupdate_script
    write_site_proxy_config
  fi
  if sudo systemctl list-unit-files discovery-api.service >/dev/null 2>&1; then
    sudo systemctl restart discovery-api || warn "Falha ao reiniciar discovery-api"
  else warn "Servico discovery-api nao encontrado; pulando restart"; fi
}

update_site() {
  clone_or_update_repo "$DISCOVERY_SITE_GIT_REPO" "$DISCOVERY_SITE_SOURCE"
  publish_site
  if sudo systemctl list-unit-files nginx.service >/dev/null 2>&1; then
    sudo systemctl restart nginx || warn "Falha ao reiniciar nginx"
  fi
}

update_agent() {
  clone_or_update_repo "$DISCOVERY_AGENT_GIT_REPO" "$DISCOVERY_AGENT_SRC"
  log "Repositorio do agent atualizado em $DISCOVERY_AGENT_SRC"
  # Revalida o CLI do Wails a partir do go.mod atualizado do agent (v2<->v3 e
  # upgrade/downgrade de versao beta).
  ensure_wails_toolchain
  _trigger_agent_rebuild_via_api
}

# ── Trigger agent rebuild via the HTTP rebuild endpoint ─────────────────

_trigger_agent_rebuild_via_api() {
  local api_url="http://127.0.0.1:8080"
  local rebuild_endpoint="${api_url}/api/v1/agent-updates/build/rebuild"
  local curl_timeout=300

  if sudo systemctl is-active --quiet discovery-api.service 2>/dev/null; then :; else
    warn "Servico discovery-api nao esta rodando; impossivel disparar rebuild do agent"
    return
  fi

  log "Disparando rebuild do agent via API (sem restart)..."
  local response http_code retry=0 max_retries=5

  while [[ $retry -lt $max_retries ]]; do
    response="$(curl -s -w '\n%{http_code}' -X POST "$rebuild_endpoint" \
      -H "Content-Type: application/json" \
      --max-time "$curl_timeout" \
      --connect-timeout 5 \
      2>&1)" && break

    retry=$((retry + 1))
    if [[ $retry -lt $max_retries ]]; then
      log "API ainda nao respondeu (tentativa $retry/$max_retries). Aguardando 3s..."
      sleep 3
    fi
  done

  if [[ -z "$response" ]]; then
    warn "Falha ao conectar na API em $api_url para rebuild do agent apos $max_retries tentativas"
    return
  fi

  http_code="$(printf '%s' "$response" | tail -n 1)"
  local body; body="$(printf '%s' "$response" | sed '$d')"

  if [[ "$http_code" == "200" ]]; then
    local build_id build_version build_file build_platform build_arch
    build_id="$(echo "$body" | grep -o '"buildId":"[^"]*"' | head -1 | cut -d'"' -f4 || true)"
    build_version="$(echo "$body" | grep -o '"version":"[^"]*"' | head -1 | cut -d'"' -f4 || true)"
    build_file="$(echo "$body" | grep -o '"fileName":"[^"]*"' | head -1 | cut -d'"' -f4 || true)"
    build_platform="$(echo "$body" | grep -o '"platform":"[^"]*"' | head -1 | cut -d'"' -f4 || true)"
    build_arch="$(echo "$body" | grep -o '"architecture":"[^"]*"' | head -1 | cut -d'"' -f4 || true)"
    log "Agent rebuild concluido com sucesso (sem downtime da API)"
    log "Build publicado: version=${build_version:-?}, platform=${build_platform:-?}/${build_arch:-?}, file=${build_file:-?}, id=${build_id:-?}"
  elif [[ "$http_code" == "403" ]]; then
    warn "Rebuild do agent rejeitado (HTTP 403) — endpoint so aceita localhost. Resposta: ${body}"
  else
    warn "Rebuild do agent falhou (HTTP ${http_code}). Resposta: ${body}"
  fi
}

update_all_components() {
  clone_or_update_repo "$DISCOVERY_GIT_REPO" "$DISCOVERY_API_SOURCE"
  clone_or_update_repo "$DISCOVERY_SITE_GIT_REPO" "$DISCOVERY_SITE_SOURCE"
  clone_or_update_repo "$DISCOVERY_AGENT_GIT_REPO" "$DISCOVERY_AGENT_SRC"
  # Revalida o CLI do Wails a partir do go.mod atualizado do agent antes do rebuild.
  ensure_wails_toolchain
  publish_api
  update_remote_access_environment_file || warn "Falha ao atualizar variaveis RemoteAccess (nao-bloqueante)"
  publish_site
  if [[ "${DISCOVERY_REFRESH_INFRA_CONFIG:-0}" == "1" ]]; then
    log "Atualizando tambem infraestrutura auxiliar (self-update + Nginx) por solicitacao explicita"
    install_selfupdate_script || warn "Falha ao instalar script de self-update (nao-bloqueante)"
    write_site_proxy_config || warn "Falha ao escrever config do Nginx (nao-bloqueante)"
  fi
  log "Reiniciando servicos..."
  if sudo systemctl list-unit-files discovery-api.service >/dev/null 2>&1; then
    sudo systemctl restart discovery-api || warn "Falha ao reiniciar discovery-api"
  else warn "Servico discovery-api nao encontrado; pulando restart"; fi
  if sudo systemctl list-unit-files nginx.service >/dev/null 2>&1; then
    sudo systemctl restart nginx || warn "Falha ao reiniciar nginx"
  fi
  _trigger_agent_rebuild_via_api || warn "Falha ao disparar rebuild do agent (nao-bloqueante)"
}

# ── Update: scope selector ────────────────────────────────────────────────

prompt_update_scope() {
  if [[ "${NON_INTERACTIVE:-0}" -eq 1 ]]; then
    printf 'all'
    return
  fi

  while true; do
    echo >&2
    echo "----------------------------------------" >&2
    echo " Escopo do update" >&2
    echo "----------------------------------------" >&2
    echo "Escolha quais componentes atualizar:" >&2
    echo "1) Tudo (API + portal web + agent)" >&2
    echo "2) Somente API (backend .NET)" >&2
    echo "3) Somente portal web (frontend)" >&2
    echo "4) Somente agent (repositorio do instalador Windows + rebuild via API sem downtime)" >&2
    echo "----------------------------------------" >&2

    local selected_option
    read -r -p "Opcao [1]: " selected_option
    selected_option="${selected_option:-1}"
    selected_option="$(printf '%s' "$selected_option" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"

    case "$selected_option" in
      1|all|tudo)         printf 'all';   return ;;
      2|api|backend)      printf 'api';   return ;;
      3|site|portal|web)  printf 'site';  return ;;
      4|agent)            printf 'agent'; return ;;
      *) echo "Opcao invalida: $selected_option. Use 1-4." >&2 ;;
    esac
  done
}

# ── Update: main entry point ──────────────────────────────────────────────

apply_stack_update_only() {
  set_log_context "update"
  log "Modo de update da stack"

  # Atualiza o SO (apt-get update + upgrade) ANTES de qualquer build.
  apply_system_updates

  load_update_defaults

  local update_scope
  update_scope="$(prompt_update_scope)"

  case "$update_scope" in
    all)
      log "Atualizando todos os componentes (API + portal + agent)"
      update_all_components ;;
    api)
      log "Atualizando somente a API (backend .NET)"
      update_api ;;
    site)
      log "Atualizando somente o portal web (frontend)"
      update_site ;;
    agent)
      log "Atualizando somente o repositorio do agent (rebuild via API, sem downtime)"
      update_agent ;;
    *) fail "Escopo de update invalido: $update_scope" ;;
  esac

  log "Update da stack concluido (escopo: ${update_scope})"
}

apply_nats_reconfiguration_only() {
  set_log_context "update-nats"
  log "Modo de atualizacao NATS: reconfigurando NATS e variaveis da API"

  DISCOVERY_OPS_DIR="${DISCOVERY_OPS_DIR:-/opt/discovery-ops}"

  prompt_nats_configuration
  validate_security_inputs
  normalize_nats_settings
  normalize_site_realtime_settings
  load_existing_nats_defaults
  generate_nats_account_keys

  setup_nats
  update_nats_environment_file
  update_site_realtime_environment_file

  if sudo systemctl list-unit-files discovery-api.service >/dev/null 2>&1; then
    log "Reiniciando discovery-api para aplicar novas configuracoes NATS"
    sudo systemctl restart discovery-api
    wait_for_discovery_api_ready
  fi

  ensure_nats_fanout_stream

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
  echo >&2
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

run_reset_mfa_only() {
  local target_login="$1"
  local api_env_file="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"
  local api_current="${DISCOVERY_API_CURRENT:-/opt/discovery-api/current}"

  [[ -n "$target_login" ]] || fail "Login alvo nao informado para reset MFA."
  if ! sudo -u discovery-api test -x "$api_current/Discovery.Api"; then
    fail "Binario da API nao encontrado em $api_current/Discovery.Api"
  fi
  if ! sudo -u discovery-api test -r "$api_env_file"; then
    fail "Arquivo de ambiente da API nao encontrado ou sem leitura para discovery-api: $api_env_file"
  fi

  local confirm
  read -r -p "Confirmar reset SOMENTE do MFA de '$target_login'? A senha NAO sera alterada. (S/n): " confirm
  confirm="$(printf '%s' "${confirm:-s}" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$confirm" in
    s|sim|y|yes|1) ;;
    *) echo "Reset de MFA cancelado."; return 0 ;;
  esac

  log "Executando reset MFA-only para '$target_login' (senha inalterada)..."

  local output
  output="$(sudo -u discovery-api env \
    DISCOVERY_API_MAINTENANCE_ENV_FILE="$api_env_file" \
    DISCOVERY_API_MAINTENANCE_CURRENT="$api_current" \
    DISCOVERY_API_BOOTSTRAP_LOGIN="$target_login" \
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
      "$DISCOVERY_API_MAINTENANCE_CURRENT/Discovery.Api" --recover-admin --login "$DISCOVERY_API_BOOTSTRAP_LOGIN" --reset-mfa-only
    ' 2>&1)" || {
    local exit_code=$?
    echo
    echo "Output do recover-admin:"
    printf '%s\n' "$output"
    echo
    if (( exit_code == 1 )); then
      log "O usuario '$target_login' nao foi encontrado (nem por login, nem por email)."
      log "Dica: confirme se '$target_login' e o login OU o email correto da conta."
      log "Para criar um usuario, use as opcoes 1, 2 ou 4 do menu."
    fi
    return "$exit_code"
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
    echo "3) Resetar SOMENTE o MFA (senha inalterada, usuario deve existir)"
    echo "4) Recriar/garantir admin padrao (login admin, senha automatica, reset MFA)"
    echo "5) Ver ajuda completa do recover-admin"
    echo "6) Trocar provedor de certificado TLS (self-signed/ZeroSSL/Let's Encrypt)"
    echo "0) Sair"
    echo "----------------------------------------"

    local selected_option
    read -r -p "Opcao [1]: " selected_option
    selected_option="${selected_option:-1}"
    selected_option="$(printf '%s' "$selected_option" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"

    case "$selected_option" in
      1)
        local target_login target_password
        target_login="$(prompt_maintenance_login "Login ou email alvo [admin]: " "admin")"
        target_password="$(prompt_maintenance_password_optional)"
        log "Executando recover-admin para '$target_login' com reset de MFA"
        run_recover_admin_maintenance "$target_login" "$target_password" "1" "1" "1"
        pause_maintenance_menu ;;
      2)
        local keep_login keep_password
        keep_login="$(prompt_maintenance_login "Login ou email alvo [admin]: " "admin")"
        keep_password="$(prompt_maintenance_password_optional)"
        log "Executando recover-admin para '$keep_login' mantendo MFA atual"
        run_recover_admin_maintenance "$keep_login" "$keep_password" "0" "1" "1"
        pause_maintenance_menu ;;
      3)
        local mfa_login
        mfa_login="$(prompt_maintenance_login "Login ou email alvo [admin]: " "admin")"
        run_reset_mfa_only "$mfa_login" || true
        pause_maintenance_menu ;;
      4)
        log "Executando recover-admin para login admin (cria se ausente)"
        run_recover_admin_maintenance "admin" "" "1" "1" "1"
        pause_maintenance_menu ;;
      5)
        print_recover_admin_help
        pause_maintenance_menu ;;
      6)
        switch_tls_provider
        pause_maintenance_menu ;;
      0|sair|exit|q|quit)
        log "Saindo do modo de manutencao"
        return ;;
      *)
        echo "Opcao invalida: $selected_option" >&2 ;;
    esac
  done
}

# ── Troca de provedor de certificado TLS ───────────────────────────────────

# Troca o provedor de certificado TLS (self-signed / zerossl-acme / letsencrypt-acme)
# em uma instalacao existente. Emite o novo certificado, atualiza o discovery.env
# e ajusta os timers de renovacao.
switch_tls_provider() {
  local env_file="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"
  sudo test -f "$env_file" || fail "Arquivo de ambiente da API nao encontrado: $env_file"

  # Carrega defaults existentes para preservar credenciais.
  load_existing_tls_defaults

  local current_provider="${TLS_CERT_PROVIDER:-self-signed}"
  echo
  echo "Provedor de certificado TLS atual: $current_provider"
  echo "1) self-signed  - certificado local gerado pelo instalador"
  echo "2) zerossl-acme - ZeroSSL via ACME com validacao DNS"
  echo "3) letsencrypt-acme - Let's Encrypt via ACME com validacao DNS"
  echo "0) Cancelar"
  echo "----------------------------------------"

  local new_provider
  read -r -p "Novo provedor [0]: " new_provider
  new_provider="${new_provider:-0}"
  new_provider="$(printf '%s' "$new_provider" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"

  case "$new_provider" in
    1|self-signed) new_provider="self-signed" ;;
    2|zerossl|zerossl-acme) new_provider="zerossl-acme" ;;
    3|letsencrypt|lets-encrypt|letsencrypt-acme) new_provider="letsencrypt-acme" ;;
    0|cancelar|sair|exit|q|quit) log "Troca de TLS cancelada."; return ;;
    *) echo "Opcao invalida: $new_provider" >&2; return ;;
  esac

  if [[ "$new_provider" == "$current_provider" ]]; then
    log "O provedor ja e $new_provider; nada a fazer."
    return
  fi

  # Coleta configuracao do novo provider (se ACME).
  if [[ "$new_provider" == "zerossl-acme" ]]; then
    prompt_zerossl_acme_configuration
  elif [[ "$new_provider" == "letsencrypt-acme" ]]; then
    prompt_letsencrypt_acme_configuration
  fi

  TLS_CERT_PROVIDER="$new_provider"
  normalize_tls_certificate_provider

  log "Trocando provedor TLS para $TLS_CERT_PROVIDER"

  # Emite o novo certificado.
  setup_proxy_certificate

  # Atualiza o discovery.env com o novo provider.
  local tmp_file; tmp_file="$(mktemp)"
  cp "$env_file" "$tmp_file"

  _set_env_key() {
    local key="$1" value="$2"
    if grep -q "^${key}=" "$tmp_file" 2>/dev/null; then
      sed -i "s|^${key}=.*|${key}=${value}|" "$tmp_file"
    else
      printf '%s=%s\n' "$key" "$value" >> "$tmp_file"
    fi
  }

  _set_env_key "TLS_CERT_PROVIDER" "$TLS_CERT_PROVIDER"
  _set_env_key "ZEROSSL_CERT_DOMAIN" "${ZEROSSL_CERT_DOMAIN:-}"
  _set_env_key "ZEROSSL_CERT_ALT_DOMAINS" "${ZEROSSL_CERT_ALT_DOMAINS:-}"
  _set_env_key "ZEROSSL_ACME_EMAIL" "${ZEROSSL_ACME_EMAIL:-}"
  _set_env_key "ZEROSSL_ACME_EAB_KID" "${ZEROSSL_ACME_EAB_KID:-}"
  _set_env_key "ZEROSSL_ACME_EAB_HMAC_KEY" "${ZEROSSL_ACME_EAB_HMAC_KEY:-}"
  _set_env_key "ZEROSSL_DNS_RESOLVERS" "${ZEROSSL_DNS_RESOLVERS:-1.1.1.1,8.8.8.8}"
  _set_env_key "ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS" "${ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS:-600}"
  _set_env_key "ZEROSSL_DNS_POLL_INTERVAL_SECONDS" "${ZEROSSL_DNS_POLL_INTERVAL_SECONDS:-15}"
  _set_env_key "ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY" "${ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY:-30}"
  _set_env_key "ZEROSSL_AUTO_RENEW_ENABLED" "${ZEROSSL_AUTO_RENEW_ENABLED:-1}"
  _set_env_key "ZEROSSL_DNS_AUTOMATION_HOOK" "${ZEROSSL_DNS_AUTOMATION_HOOK:-}"
  _set_env_key "LETSENCRYPT_CERT_DOMAIN" "${LETSENCRYPT_CERT_DOMAIN:-}"
  _set_env_key "LETSENCRYPT_CERT_ALT_DOMAINS" "${LETSENCRYPT_CERT_ALT_DOMAINS:-}"
  _set_env_key "LETSENCRYPT_ACME_EMAIL" "${LETSENCRYPT_ACME_EMAIL:-}"
  _set_env_key "LETSENCRYPT_DNS_RESOLVERS" "${LETSENCRYPT_DNS_RESOLVERS:-1.1.1.1,8.8.8.8}"
  _set_env_key "LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS" "${LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS:-600}"
  _set_env_key "LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS" "${LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS:-15}"
  _set_env_key "LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY" "${LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY:-30}"
  _set_env_key "LETSENCRYPT_AUTO_RENEW_ENABLED" "${LETSENCRYPT_AUTO_RENEW_ENABLED:-1}"
  _set_env_key "LETSENCRYPT_DNS_AUTOMATION_HOOK" "${LETSENCRYPT_DNS_AUTOMATION_HOOK:-}"

  sudo install -m 640 -o root -g discovery-api "$tmp_file" "$env_file"
  rm -f "$tmp_file"

  # Ajusta os timers de renovacao (desativa o antigo, ativa o novo).
  sudo systemctl disable --now discovery-zerossl-renew.timer >/dev/null 2>&1 || true
  sudo systemctl disable --now discovery-letsencrypt-renew.timer >/dev/null 2>&1 || true
  setup_zerossl_renewal_timer
  setup_letsencrypt_renewal_timer

  log "Provedor TLS alterado para $TLS_CERT_PROVIDER com sucesso."
}
