# Discovery RMM installer – normalization functions
# Requires: common.sh (fail, resolve_fido2_server_domain, normalize_host_without_scheme)

normalize_access_mode() {
  ACCESS_MODE="${ACCESS_MODE:-internal}"
  ACCESS_MODE="$(printf '%s' "$ACCESS_MODE" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$ACCESS_MODE" in
    1|internal) ACCESS_MODE="internal" ;;
    2|external) ACCESS_MODE="external" ;;
    3|hybrid)   ACCESS_MODE="hybrid"   ;;
    *) fail "ACCESS_MODE invalido: $ACCESS_MODE (use 1/2/3 ou internal/external/hybrid)" ;;
  esac
}

normalize_openapi_enabled() {
  local normalized
  normalized="$(printf '%s' "${OPENAPI_ENABLED:-0}" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$normalized" in
    1|true|yes|y|sim|s)       OPENAPI_ENABLED=1 ;;
    0|false|no|n|nao|"")      OPENAPI_ENABLED=0 ;;
    *) fail "OPENAPI_ENABLED invalido: ${OPENAPI_ENABLED}. Use 1/0 (ou sim/nao)." ;;
  esac
}

normalize_openapi_scalar_enabled() {
  local normalized
  normalized="$(printf '%s' "${OPENAPI_SCALAR_ENABLED:-$OPENAPI_ENABLED}" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$normalized" in
    1|true|yes|y|sim|s)       OPENAPI_SCALAR_ENABLED=1 ;;
    0|false|no|n|nao|"")      OPENAPI_SCALAR_ENABLED=0 ;;
    *) fail "OPENAPI_SCALAR_ENABLED invalido: ${OPENAPI_SCALAR_ENABLED}. Use 1/0 (ou sim/nao)." ;;
  esac
}

normalize_zerossl_auto_renew_enabled() {
  local normalized
  normalized="$(printf '%s' "${ZEROSSL_AUTO_RENEW_ENABLED:-1}" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$normalized" in
    1|true|yes|y|sim|s)       ZEROSSL_AUTO_RENEW_ENABLED=1 ;;
    0|false|no|n|nao|"")      ZEROSSL_AUTO_RENEW_ENABLED=0 ;;
    *) fail "ZEROSSL_AUTO_RENEW_ENABLED invalido: ${ZEROSSL_AUTO_RENEW_ENABLED}. Use 1/0 (ou sim/nao)." ;;
  esac
}

normalize_nats_settings() {
  NATS_BIND_HOST="${NATS_BIND_HOST:-0.0.0.0}"
  NATS_MONITOR_HOST="${NATS_MONITOR_HOST:-127.0.0.1}"
  NATS_AUTH_USER="${NATS_AUTH_USER:-$NATS_USER}"
  NATS_AUTH_PASSWORD="${NATS_AUTH_PASSWORD:-$NATS_PASSWORD}"
  NATS_AUTH_CALLOUT_SUBJECT="${NATS_AUTH_CALLOUT_SUBJECT:-\$SYS.REQ.USER.AUTH}"
  NATS_AUTH_CALLOUT_ENABLED="${NATS_AUTH_CALLOUT_ENABLED:-1}"
  NATS_WS_PORT="${NATS_WS_PORT:-8081}"
  NATS_WS_HOST="${NATS_WS_HOST:-127.0.0.1}"
  NATS_WS_TLS_ENABLED="${NATS_WS_TLS_ENABLED:-false}"

  if [[ "$NATS_AUTH_CALLOUT_ENABLED" != "0" && -z "${NATS_AUTH_CALLOUT_ISSUER:-}" ]]; then
    NATS_AUTH_CALLOUT_ISSUER=""
  fi
}

normalize_tls_certificate_provider() {
  local provider
  provider="$(printf '%s' "${TLS_CERT_PROVIDER:-self-signed}" | tr '[:upper:]' '[:lower:]')"
  case "$provider" in
    self-signed|selfsigned|local) TLS_CERT_PROVIDER="self-signed" ;;
    zerossl|zerossl-acme|acme)    TLS_CERT_PROVIDER="zerossl-acme" ;;
    *) fail "TLS_CERT_PROVIDER invalido: $provider (use self-signed ou zerossl-acme)" ;;
  esac

  if [[ "$TLS_CERT_PROVIDER" == "zerossl-acme" ]]; then
    ZEROSSL_CERT_DOMAIN="$(normalize_host_without_scheme "${ZEROSSL_CERT_DOMAIN:-$(resolve_fido2_server_domain)}")"
    ZEROSSL_CERT_ALT_DOMAINS="${ZEROSSL_CERT_ALT_DOMAINS:-}"
    ZEROSSL_DNS_RESOLVERS="${ZEROSSL_DNS_RESOLVERS:-1.1.1.1,8.8.8.8}"
    ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS="${ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS:-600}"
    ZEROSSL_DNS_POLL_INTERVAL_SECONDS="${ZEROSSL_DNS_POLL_INTERVAL_SECONDS:-15}"
    ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY="${ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY:-30}"
    ZEROSSL_AUTO_RENEW_ENABLED="${ZEROSSL_AUTO_RENEW_ENABLED:-1}"
    normalize_zerossl_auto_renew_enabled
    ZEROSSL_DNS_AUTOMATION_HOOK="${ZEROSSL_DNS_AUTOMATION_HOOK:-}"
  fi
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

normalize_site_realtime_settings() {
  DISCOVERY_SITE_REALTIME_PROVIDER="${DISCOVERY_SITE_REALTIME_PROVIDER:-both}"
  DISCOVERY_SITE_NATS_ENABLED="${DISCOVERY_SITE_NATS_ENABLED:-true}"
  NATS_WS_PORT="${NATS_WS_PORT:-8081}"
  NATS_WS_HOST="${NATS_WS_HOST:-127.0.0.1}"
  NATS_WS_TLS_ENABLED="${NATS_WS_TLS_ENABLED:-false}"
  DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS="${DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS:-60000}"

  if [[ -z "${DISCOVERY_SITE_NATS_URL:-}" ]]; then
    local nats_public_host="${DISCOVERY_SITE_NATS_PUBLIC_HOST:-${Authentication__Fido2__ServerDomain:-}}"
    if [[ -z "$nats_public_host" && -n "${ACCESS_MODE:-}" ]]; then
      nats_public_host="$(resolve_fido2_server_domain)"
    fi
    if [[ -z "$nats_public_host" ]] && sudo test -f /etc/discovery-api/discovery.env; then
      nats_public_host="$(sudo awk -F= '/^Authentication__Fido2__ServerDomain=/{sub("^[^=]*=",""); print; exit}' /etc/discovery-api/discovery.env 2>/dev/null || true)"
    fi
    nats_public_host="$(normalize_host_without_scheme "$nats_public_host")"
    if [[ -n "$nats_public_host" ]]; then
      DISCOVERY_SITE_NATS_URL="wss://${nats_public_host}/nats/"
    else
      DISCOVERY_SITE_NATS_URL="/nats/"
    fi
  fi
}

normalize_install_branch_input() {
  local raw_branch="$1"
  local normalized_branch
  normalized_branch="$(printf '%s' "$raw_branch" | tr '[:upper:]' '[:lower:]')"
  case "$normalized_branch" in
    lts|release|beta|dev) printf '%s' "$normalized_branch"; return ;;
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
    wizard_header "Canal/branch de instalacao" "$(wizard_step_label "4/8" "3/7")"
    echo "Selecione o canal/branch que sera clonado."
    echo "Isso define a versao da API instalada."
    echo " 1) lts     - suporte longo prazo"
    echo " 2) release - canal estavel"
    echo " 3) beta    - novidades em teste"
    echo " 4) dev     - desenvolvimento"
    echo " 5) custom  - informar branch manualmente"
    echo "----------------------------------------"
    echo "Dica: digite o numero ou o nome (ex: release)."
    echo "Padrao: [2] release (pressione Enter)."

    local selected_option
    read -r -p "Opcao [2]: " selected_option
    selected_option="${selected_option:-2}"
    selected_option="$(printf '%s' "$selected_option" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"

    case "$(printf '%s' "$selected_option" | tr '[:upper:]' '[:lower:]')" in
      1|lts)             DISCOVERY_GIT_BRANCH="lts";     return ;;
      2|release)         DISCOVERY_GIT_BRANCH="release"; return ;;
      3|beta)            DISCOVERY_GIT_BRANCH="beta";    return ;;
      4|dev)             DISCOVERY_GIT_BRANCH="dev";     return ;;
      5|custom)
        local custom_branch
        read -r -p "Informe a branch custom: " custom_branch
        custom_branch="$(printf '%s' "$custom_branch" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
        [[ -n "$custom_branch" ]] || { echo "Branch custom nao informada. Tente novamente." >&2; continue; }
        DISCOVERY_GIT_BRANCH="$(normalize_install_branch_input "$custom_branch")"
        return ;;
      *) echo "Opcao invalida: $selected_option. Use 1-5 ou o nome da branch (lts/release/beta/dev)." >&2 ;;
    esac
  done
}

select_operation_mode() {
  if [[ "${UPDATE_NATS_CONFIG_ONLY:-0}" -eq 1 || "${UPDATE_STACK_ONLY:-0}" -eq 1 || "${MAINTENANCE_MODE:-0}" -eq 1 ]]; then return; fi

  local requested_mode="${INSTALL_MODE:-}"
  if [[ -z "$requested_mode" && -n "${DISCOVERY_INSTALL_MODE:-}" ]]; then
    requested_mode="$DISCOVERY_INSTALL_MODE"
  fi

  if [[ -n "$requested_mode" ]]; then
    requested_mode="$(printf '%s' "$requested_mode" | tr '[:upper:]' '[:lower:]')"
    case "$requested_mode" in
      full|install|complete)
        UPDATE_NATS_CONFIG_ONLY=0
        UPDATE_STACK_ONLY=0
        MAINTENANCE_MODE=0
        return ;;
      nats|nats-only|update-nats)
        UPDATE_NATS_CONFIG_ONLY=1
        UPDATE_STACK_ONLY=0
        MAINTENANCE_MODE=0
        return ;;
      update|update-stack|rebuild|upgrade)
        UPDATE_NATS_CONFIG_ONLY=0
        UPDATE_STACK_ONLY=1
        MAINTENANCE_MODE=0
        return ;;
      maintenance|advanced|manutencao)
        UPDATE_NATS_CONFIG_ONLY=0
        UPDATE_STACK_ONLY=0
        MAINTENANCE_MODE=1
        return ;;
      *) fail "Modo invalido: $requested_mode (use full, nats, update, upgrade ou maintenance)" ;;
    esac
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then return; fi

  while true; do
    wizard_header "Modo de Operacao" "$(wizard_step_label "2/8" "1/7")"
    echo "Escolha o que sera executado neste momento:"
    echo "1) Instalacao completa (API + portal web + Postgres + NATS + servicos)"
    echo "2) Atualizar somente configuracao do NATS (inclui issuer/auth_callout)"
    echo "3) Atualizar instalacao existente (repositorios + rebuild API/portal + restart servicos)"
    echo "4) Ver dados do servidor (instalacao atual, usuario, senha, chaves e afins)"
    echo "5) Manutencao avancada (reset senha/MFA e recovery de usuario admin)"
    echo "----------------------------------------"

    local selected_option
    read -r -p "Opcao [1]: " selected_option
    selected_option="${selected_option:-1}"
    selected_option="$(printf '%s' "$selected_option" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    case "$selected_option" in
      1|full|install|complete)
        UPDATE_NATS_CONFIG_ONLY=0
        UPDATE_STACK_ONLY=0
        MAINTENANCE_MODE=0
        return ;;
      2|nats|nats-only|update-nats)
        UPDATE_NATS_CONFIG_ONLY=1
        UPDATE_STACK_ONLY=0
        MAINTENANCE_MODE=0
        return ;;
      3|update|update-stack|rebuild|upgrade)
        UPDATE_NATS_CONFIG_ONLY=0
        UPDATE_STACK_ONLY=1
        MAINTENANCE_MODE=0
        return ;;
      4) print_server_installation_data ;;
      5|maintenance|advanced|manutencao)
        UPDATE_NATS_CONFIG_ONLY=0
        UPDATE_STACK_ONLY=0
        MAINTENANCE_MODE=1
        return ;;
      *) echo "Opcao invalida: $selected_option" >&2 ;;
    esac
  done
}

print_server_installation_data() {
  wizard_header "Dados do servidor/instalacao" "$(wizard_step_label "2/8" "1/7")"
  echo "Leitura do ambiente local para consulta e replicacao."
  echo "----------------------------------------"
  echo "- Hostname: $(hostname 2>/dev/null || echo desconhecido)"
  echo "- Kernel: $(uname -sr 2>/dev/null || echo desconhecido)"

  local internal_ip
  internal_ip="$(detect_internal_ipv4)"
  if [[ -n "$internal_ip" ]]; then echo "- IP interno detectado: $internal_ip"; fi

  echo; echo "[discovery.env]"
  if sudo test -f /etc/discovery-api/discovery.env; then sudo sed 's/^/  /' /etc/discovery-api/discovery.env
  else echo "  Arquivo /etc/discovery-api/discovery.env nao encontrado."; fi

  echo; echo "[paths de chaves/certificados]"
  if sudo test -f /etc/discovery-api/certs/jwt-public.pem; then echo "  JWT public key: /etc/discovery-api/certs/jwt-public.pem"
  else echo "  JWT public key: ausente"; fi
  if sudo test -f /etc/discovery-api/certs/jwt-private.pem; then echo "  JWT private key: /etc/discovery-api/certs/jwt-private.pem"
  else echo "  JWT private key: ausente"; fi
  if sudo test -f /etc/discovery-api/certs/api-internal.crt; then echo "  TLS cert: /etc/discovery-api/certs/api-internal.crt"
  else echo "  TLS cert: ausente"; fi
  if sudo test -f /etc/discovery-api/certs/api-internal.key; then echo "  TLS key: /etc/discovery-api/certs/api-internal.key"
  else echo "  TLS key: ausente"; fi

  echo; echo "Pressione Enter para voltar ao menu de modo de operacao..."
  read -r _
}
