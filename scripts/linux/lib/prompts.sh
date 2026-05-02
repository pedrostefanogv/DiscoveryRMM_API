# Discovery RMM installer – user prompts and interactive input
# Requires: common.sh (log, warn, fail, wizard_header, wizard_step_label, is_valid_repo_url, generate_random_password, generate_random_admin_login, resolve_fido2_server_domain)

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
    if [[ -n "$default_value" ]]; then
      read -r -s -p "$prompt_text (pressione Enter para usar o valor padrao): " input
      input="${input:-$default_value}"
    else
      read -r -s -p "$prompt_text" input
    fi
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

prompt_repo_url() {
  local var_name="$1"
  local prompt_text="$2"
  local default_value="${3:-}"
  local current_value="${!var_name:-}"

  if [[ -n "$current_value" ]]; then
    is_valid_repo_url "$current_value" || fail "URL invalida para $var_name: $current_value"
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    if [[ -n "$default_value" ]]; then
      printf -v "$var_name" '%s' "$default_value"
      return
    fi
    fail "Variavel obrigatoria ausente para modo nao interativo: $var_name"
  fi

  while true; do
    local input=""
    if [[ -n "$default_value" ]]; then
      read -r -p "$prompt_text [$default_value]: " input
      input="${input:-$default_value}"
    else
      read -r -p "$prompt_text: " input
    fi
    [[ -n "$input" ]] || { echo "Valor obrigatorio nao informado para $var_name" >&2; continue; }
    if is_valid_repo_url "$input"; then
      printf -v "$var_name" '%s' "$input"
      return
    fi
    echo "URL invalida. Use https://.../repo.git ou git@host:org/repo.git" >&2
  done
}

prompt_postgres_password() {
  if [[ -z "${POSTGRES_PASSWORD:-}" ]] && sudo test -f /etc/discovery-api/discovery.env; then
    local existing_password
    existing_password="$(sudo sed -n 's/^ConnectionStrings__DefaultConnection=.*Password=\([^;]*\).*/\1/p' /etc/discovery-api/discovery.env | head -n 1)"
    if [[ -n "$existing_password" ]]; then
      POSTGRES_PASSWORD="$existing_password"
      POSTGRES_PASSWORD_AUTO=0
      log "Senha PostgreSQL carregada do discovery.env existente."
      return
    fi
  fi

  if [[ -n "${POSTGRES_PASSWORD:-}" ]]; then
    POSTGRES_PASSWORD_AUTO=0
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    POSTGRES_PASSWORD="$(generate_random_password 24)"
    POSTGRES_PASSWORD_AUTO=1
    log "POSTGRES_PASSWORD nao informado; gerando senha aleatoria."
    return
  fi

  local generated_password
  generated_password="$(generate_random_password 24)"
  echo "Entrada oculta. Pressione Enter para usar a senha gerada automaticamente."
  local input=""
  read -r -s -p "Senha PostgreSQL: " input
  printf '\n'
  if [[ -z "$input" ]]; then
    POSTGRES_PASSWORD="$generated_password"
    POSTGRES_PASSWORD_AUTO=1
    log "Senha PostgreSQL nao informada; gerada automaticamente."
    return
  fi

  POSTGRES_PASSWORD_AUTO=0
  POSTGRES_PASSWORD="$input"
}

generate_random_nats_user() {
  local generated_user
  generated_user="discovery_nats_$(generate_random_password 10)"
  generated_user="$(printf '%s' "$generated_user" | tr '[:upper:]' '[:lower:]')"
  printf '%s' "$generated_user"
}

prompt_nats_user() {
  if [[ -n "${NATS_USER:-}" ]]; then
    NATS_USER_AUTO="${NATS_USER_AUTO:-0}"
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    NATS_USER="$(generate_random_nats_user)"
    NATS_USER_AUTO=1
    log "NATS_USER nao informado; gerando usuario aleatorio."
    return
  fi

  local generated_user
  generated_user="$(generate_random_nats_user)"
  echo "Pressione Enter para usar um usuario NATS gerado automaticamente."
  local input=""
  read -r -p "Usuario NATS [$generated_user]: " input
  input="$(printf '%s' "$input" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  if [[ -z "$input" ]]; then
    NATS_USER="$generated_user"
    NATS_USER_AUTO=1
    log "Usuario NATS nao informado; gerado automaticamente."
    return
  fi

  NATS_USER_AUTO=0
  NATS_USER="$input"
}

prompt_nats_password() {
  if [[ -n "${NATS_PASSWORD:-}" ]]; then
    NATS_PASSWORD_AUTO="${NATS_PASSWORD_AUTO:-0}"
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    NATS_PASSWORD="$(generate_random_password 24)"
    NATS_PASSWORD_AUTO=1
    log "NATS_PASSWORD nao informado; gerando senha aleatoria."
    return
  fi

  local generated_password
  generated_password="$(generate_random_password 24)"
  echo "Entrada oculta. Pressione Enter para usar uma senha NATS gerada automaticamente."
  local input=""
  read -r -s -p "Senha NATS: " input
  printf '\n'
  if [[ -z "$input" ]]; then
    NATS_PASSWORD="$generated_password"
    NATS_PASSWORD_AUTO=1
    log "Senha NATS nao informada; gerada automaticamente."
    return
  fi

  NATS_PASSWORD_AUTO=0
  NATS_PASSWORD="$input"
}

# ── NATS configuration wizard ──────────────────────────────────────────────

prompt_nats_configuration() {
  load_existing_nats_defaults
  wizard_header "Mensageria (NATS)" "$(wizard_step_label "8/9" "7/8")"
  echo "Configure as credenciais e hosts do NATS local."
  echo "Esses dados sao usados pela API e agentes para mensagens."
  echo "Se usuario ou senha ficarem em branco, o instalador gera valores aleatorios."
  echo "----------------------------------------"

  prompt_nats_user
  prompt_nats_password
  prompt_if_empty NATS_AUTH_USER "Usuario de bypass da API no NATS (auth_users)" 0 "$NATS_USER"
  prompt_if_empty NATS_AUTH_PASSWORD "Senha de bypass da API no NATS" 1 "$NATS_PASSWORD"
  prompt_if_empty NATS_BIND_HOST "Host de bind do NATS (ex: 0.0.0.0 ou 127.0.0.1)" 0 "0.0.0.0"
  prompt_if_empty NATS_MONITOR_HOST "Host do monitor HTTP do NATS (porta 8222)" 0 "127.0.0.1"
  prompt_if_empty NATS_AUTH_CALLOUT_ENABLED "Habilitar NATS auth callout? (1/0)" 0 "1"
  prompt_if_empty NATS_AUTH_CALLOUT_SUBJECT "Subject do auth callout" 0 "\$SYS.REQ.USER.AUTH"
}

# ── Self-update wizard ─────────────────────────────────────────────────────

prompt_selfupdate_settings() {
  wizard_header "Self-Update Automatico" "$(wizard_step_label "9/10" "8/9")"
  echo "O Discovery pode verificar e aplicar atualizacoes automaticamente."
  echo "O script compara o commit local com o remoto e faz dotnet publish/npm build apenas se houver mudanca."
  echo "----------------------------------------"

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    SELFUPDATE_ENABLED="${SELFUPDATE_ENABLED:-1}"
    SELFUPDATE_INTERVAL="${SELFUPDATE_INTERVAL:-24h}"
    DISCOVERY_CLEAN_BUILD="${DISCOVERY_CLEAN_BUILD:-1}"
    return
  fi

  if [[ -z "${SELFUPDATE_ENABLED:-}" ]]; then
    local selfupdate_choice
    read -r -p "Ativar self-update automatico? (1=Sim / 0=Nao) [1]: " selfupdate_choice
    selfupdate_choice="${selfupdate_choice:-1}"
    if [[ "$selfupdate_choice" != "1" ]]; then
      SELFUPDATE_ENABLED=0; SELFUPDATE_INTERVAL=""
    else
      SELFUPDATE_ENABLED=1
    fi
  fi

  if [[ "${SELFUPDATE_ENABLED:-1}" == "1" && -z "${SELFUPDATE_INTERVAL:-}" ]]; then
    echo
    echo "Intervalo de verificacao:"
    echo "  1) 5 minutos   [dev]"
    echo "  2) 10 minutos  [dev]"
    echo "  3) 15 minutos  [dev]"
    echo "  4) 24 horas    (recomendado para producao)"
    echo "  5) 48 horas"
    echo "  6) 72 horas"
    echo
    local interval_choice
    read -r -p "Escolha [4]: " interval_choice
    interval_choice="${interval_choice:-4}"
    case "$interval_choice" in
      1) SELFUPDATE_INTERVAL="5min" ;;
      2) SELFUPDATE_INTERVAL="10min" ;;
      3) SELFUPDATE_INTERVAL="15min" ;;
      4) SELFUPDATE_INTERVAL="24h" ;;
      5) SELFUPDATE_INTERVAL="48h" ;;
      6) SELFUPDATE_INTERVAL="72h" ;;
      *) SELFUPDATE_INTERVAL="24h" ;;
    esac
    log "Self-update ativado com intervalo: ${SELFUPDATE_INTERVAL}"
  fi

  if [[ -z "${DISCOVERY_CLEAN_BUILD:-}" ]]; then
    echo
    echo "Modo de build para updates (manual e automatico):"
    echo "  1) Limpo (remove cache antes do build)"
    echo "  2) Incremental (mais rapido, sem limpeza de cache)"
    local clean_build_choice
    read -r -p "Escolha [1]: " clean_build_choice
    clean_build_choice="${clean_build_choice:-1}"
    case "$clean_build_choice" in
      2) DISCOVERY_CLEAN_BUILD="0" ;;
      *) DISCOVERY_CLEAN_BUILD="1" ;;
    esac
  fi
}

# ── TLS certificate wizard ─────────────────────────────────────────────────

prompt_tls_certificate_configuration() {
  load_existing_tls_defaults
  wizard_header "Certificado TLS" "$(wizard_step_label "6/9" "5/8")"
  echo "1) self-signed  - certificado local gerado pelo instalador"
  echo "2) zerossl-acme - ZeroSSL via ACME com validacao DNS manual"
  echo "----------------------------------------"

  prompt_if_empty TLS_CERT_PROVIDER "Modo de certificado TLS (1/2 ou self-signed/zerossl-acme)" 0 "self-signed"
  case "$(printf '%s' "$TLS_CERT_PROVIDER" | tr '[:upper:]' '[:lower:]')" in
    1) TLS_CERT_PROVIDER="self-signed" ;;
    2) TLS_CERT_PROVIDER="zerossl-acme" ;;
  esac
  normalize_tls_certificate_provider

  if [[ "$TLS_CERT_PROVIDER" != "zerossl-acme" ]]; then
    return
  fi

  echo
  echo "ZeroSSL ACME usa desafio DNS-01. O instalador exibira o registro TXT,"
  echo "aguardara sua confirmacao e validara o DNS com dig antes de continuar."
  echo
  prompt_if_empty ZEROSSL_CERT_DOMAIN "Dominio do certificado" 0 "$(resolve_fido2_server_domain)"
  if [[ -z "${ZEROSSL_CERT_ALT_DOMAINS:-}" && "$NON_INTERACTIVE" -eq 0 ]]; then
    read -r -p "SANs adicionais separados por virgula (opcional): " ZEROSSL_CERT_ALT_DOMAINS
  fi
  ZEROSSL_CERT_ALT_DOMAINS="${ZEROSSL_CERT_ALT_DOMAINS:-}"
  prompt_if_empty ZEROSSL_ACME_EMAIL "Email da conta ZeroSSL/ACME"
  prompt_if_empty ZEROSSL_ACME_EAB_KID "ZeroSSL EAB KID"
  prompt_if_empty ZEROSSL_ACME_EAB_HMAC_KEY "ZeroSSL EAB HMAC Key"
  prompt_if_empty ZEROSSL_DNS_RESOLVERS "Resolvers para validar DNS (separados por virgula)" 0 "1.1.1.1,8.8.8.8"
  prompt_if_empty ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS "Timeout de propagacao DNS em segundos" 0 "600"
  prompt_if_empty ZEROSSL_DNS_POLL_INTERVAL_SECONDS "Intervalo de consulta DNS em segundos" 0 "15"
  ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY="${ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY:-30}"
  echo "Renovacao recomendada: checar diariamente e renovar quando faltar ate ${ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY} dias."
  prompt_if_empty ZEROSSL_AUTO_RENEW_ENABLED "Criar timer automatico de renovacao ZeroSSL? (1/0)" 0 "1"
  normalize_zerossl_auto_renew_enabled
  normalize_tls_certificate_provider
}

# ── Admin bootstrap wizard ─────────────────────────────────────────────────

prepare_bootstrap_admin_login() {
  local step_label="${1:-$(wizard_step_label "9/10" "8/9")}"
  local existing_login="${DISCOVERY_BOOTSTRAP_ADMIN_LOGIN:-}"
  local login_input=""
  local password_input=""

  DISCOVERY_BOOTSTRAP_ADMIN_LOGIN_AUTO=0

  if [[ -z "$existing_login" ]] && sudo test -f /etc/discovery-api/discovery.env; then
    existing_login="$(sudo awk -F= '/^DISCOVERY_BOOTSTRAP_ADMIN_LOGIN=/{sub("^[^=]*=","""); print; exit}' /etc/discovery-api/discovery.env 2>/dev/null || true)"
  fi

  if [[ "$NON_INTERACTIVE" -eq 0 && -z "${DISCOVERY_BOOTSTRAP_ADMIN_LOGIN:-}" ]]; then
    wizard_header "Primeiro acesso administrativo" "$step_label"
    echo "Informe o usuario administrador do primeiro acesso."
    echo "Se deixar em branco, o instalador gera um login temporario automaticamente."
    echo "A senha tambem pode ficar em branco para ser gerada pelo recover-admin."
    echo "----------------------------------------"

    if [[ -n "$existing_login" ]]; then
      read -r -p "Usuario administrador inicial [$existing_login]: " login_input
      login_input="${login_input:-$existing_login}"
    else
      read -r -p "Usuario administrador inicial [gerar automaticamente]: " login_input
    fi

    read -r -s -p "Senha administrativa inicial [gerar automaticamente]: " password_input
    printf '\n'

    if [[ -n "$login_input" ]]; then
      DISCOVERY_BOOTSTRAP_ADMIN_LOGIN="$login_input"
    fi

    if [[ -n "$password_input" ]]; then
      DISCOVERY_BOOTSTRAP_ADMIN_PASSWORD="$password_input"
    fi
  fi

  if [[ -z "${DISCOVERY_BOOTSTRAP_ADMIN_LOGIN:-}" ]]; then
    if [[ -n "$existing_login" ]]; then
      DISCOVERY_BOOTSTRAP_ADMIN_LOGIN="$existing_login"
    else
      DISCOVERY_BOOTSTRAP_ADMIN_LOGIN="$(generate_random_admin_login)"
      DISCOVERY_BOOTSTRAP_ADMIN_LOGIN_AUTO=1
      log "Login do primeiro acesso nao informado; gerando usuario administrador temporario: $DISCOVERY_BOOTSTRAP_ADMIN_LOGIN"
    fi
  fi

  if [[ -n "${DISCOVERY_BOOTSTRAP_ADMIN_PASSWORD:-}" ]]; then
    DISCOVERY_BOOTSTRAP_ADMIN_PASSWORD_AUTO=0
  else
    DISCOVERY_BOOTSTRAP_ADMIN_PASSWORD_AUTO=1
  fi

  ADMIN_RECOVERY_LOGIN="$DISCOVERY_BOOTSTRAP_ADMIN_LOGIN"
}

# ── Confirmation / summary ─────────────────────────────────────────────────

print_prerequisite_plan_summary() {
  local -a base_packages=(
    apt-transport-https ca-certificates curl git gnupg jq lsb-release
    dnsutils nginx openssl postgresql postgresql-contrib redis-server nats-server
  )
  local -a missing_base_packages=()
  local -a planned_actions=()
  local pkg

  for pkg in "${base_packages[@]}"; do
    if ! dpkg -s "$pkg" >/dev/null 2>&1; then
      missing_base_packages+=("$pkg")
    fi
  done

  if (( ${#missing_base_packages[@]} > 0 )); then
    planned_actions+=("Pacotes base via apt: ${missing_base_packages[*]}")
  fi

  if ! command -v dotnet >/dev/null 2>&1; then
    planned_actions+=("dotnet SDK 10.0 (apt Microsoft ou fallback dotnet-install.sh)")
  fi

  local node_needs_install=0
  local current_node_major=""
  if command -v node >/dev/null 2>&1; then
    current_node_major="$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || true)"
    if [[ ! "$current_node_major" =~ ^[0-9]+$ ]] || (( current_node_major < 20 )); then
      node_needs_install=1
    fi
  else
    node_needs_install=1
  fi
  if (( node_needs_install == 1 )); then
    planned_actions+=("Node.js 22 + npm")
  fi

  if ! command -v nk >/dev/null 2>&1; then
    planned_actions+=("nk para gerar chaves NATS")
  fi

  if [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]]; then
    if ! command -v cloudflared >/dev/null 2>&1; then
      planned_actions+=("cloudflared para o tunnel externo")
    fi
  fi

  local pg_major="" vector_pkg=""
  if command -v psql >/dev/null 2>&1; then
    pg_major="$(psql --version | sed -n 's/.* \([0-9][0-9]*\)\..*/\1/p' | head -n 1)"
    if [[ -n "$pg_major" ]]; then vector_pkg="postgresql-${pg_major}-pgvector"; fi
  fi
  if [[ -n "$vector_pkg" ]] && ! dpkg -s "$vector_pkg" >/dev/null 2>&1; then
    planned_actions+=("Pacote opcional ${vector_pkg} para embeddings")
  fi

  if (( ${#planned_actions[@]} == 0 )); then
    echo "- Pre-requisitos ausentes: nenhum detectado"
    return
  fi

  echo "- Pre-requisitos ausentes/acoes planejadas:"
  for pkg in "${planned_actions[@]}"; do
    echo "  - $pkg"
  done
}

print_selected_configuration_summary() {
  local internal_portal_host="" internal_portal_url="" primary_portal_host="" primary_portal_url=""
  internal_portal_host="$(resolve_internal_portal_domain)"
  internal_portal_url="$(build_portal_access_url "$internal_portal_host")"
  primary_portal_host="$(resolve_fido2_server_domain)"
  primary_portal_url="$(build_portal_access_url "$primary_portal_host")"

  echo "Resumo das configuracoes selecionadas:"
  echo "- Branch: $DISCOVERY_GIT_BRANCH"
  echo "- Repo API: $DISCOVERY_GIT_REPO"
  echo "- Repo Agent: $DISCOVERY_AGENT_GIT_REPO"
  echo "- Repo Site: $DISCOVERY_SITE_GIT_REPO"
  echo "- Access mode: $ACCESS_MODE"
  if [[ "$ACCESS_MODE" == "internal" || "$ACCESS_MODE" == "hybrid" ]]; then
    echo "- Host interno: $INTERNAL_API_HOST"
    if [[ -n "$internal_portal_url" ]]; then echo "- Portal interno: $internal_portal_url"; fi
  fi
  if [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]]; then
    echo "- Host externo: $EXTERNAL_API_HOST"
    echo "- Portal externo: https://$EXTERNAL_API_HOST/"
  fi
  if [[ -n "$primary_portal_url" ]]; then
    echo "- Portal web principal: $primary_portal_url"
  else
    echo "- Portal web: publicado no mesmo host da API via Nginx"
  fi
  if [[ "${OPENAPI_ENABLED:-0}" == "1" ]]; then
    echo "- OpenAPI: habilitado"
    if [[ "${OPENAPI_SCALAR_ENABLED:-$OPENAPI_ENABLED}" == "1" ]]; then echo "- Scalar: habilitado"; else echo "- Scalar: desativado"; fi
  else
    echo "- OpenAPI: desativado"; echo "- Scalar: desativado"
  fi
  echo "- PostgreSQL DB: $POSTGRES_DB"; echo "- PostgreSQL user: $POSTGRES_USER"
  if [[ "${POSTGRES_PASSWORD_AUTO:-0}" -eq 1 ]]; then
    echo "- PostgreSQL senha: gerada automaticamente (veja /etc/discovery-api/discovery.env)"
  else echo "- PostgreSQL senha: informada"; fi
  if [[ "${NATS_USER_AUTO:-0}" -eq 1 ]]; then echo "- NATS user: $NATS_USER (gerado automaticamente)"; else echo "- NATS user: $NATS_USER"; fi
  if [[ "${NATS_PASSWORD_AUTO:-0}" -eq 1 ]]; then
    echo "- NATS senha: gerada automaticamente (veja /etc/discovery-api/discovery.env)"
  else echo "- NATS senha: informada"; fi
  echo "- NATS auth user: $NATS_AUTH_USER"
  echo "- TLS: ${TLS_CERT_PROVIDER:-self-signed}"
  if [[ "${TLS_CERT_PROVIDER:-self-signed}" == "zerossl-acme" ]]; then
    echo "- TLS dominio: ${ZEROSSL_CERT_DOMAIN:-$(resolve_fido2_server_domain)}"
  fi
  if [[ -n "${DISCOVERY_BOOTSTRAP_ADMIN_LOGIN:-}" ]]; then
    if [[ "${DISCOVERY_BOOTSTRAP_ADMIN_LOGIN_AUTO:-0}" -eq 1 ]]; then
      echo "- Admin bootstrap login: $DISCOVERY_BOOTSTRAP_ADMIN_LOGIN (gerado automaticamente)"
    else echo "- Admin bootstrap login: $DISCOVERY_BOOTSTRAP_ADMIN_LOGIN"; fi
  elif [[ "$NON_INTERACTIVE" -eq 0 ]]; then
    echo "- Admin bootstrap login: sera solicitado na etapa final (apos API pronta)"
  fi
  if [[ "${DISCOVERY_BOOTSTRAP_ADMIN_PASSWORD_AUTO:-1}" -eq 1 ]]; then
    echo "- Admin bootstrap senha: gerada automaticamente pelo recover-admin"
  else echo "- Admin bootstrap senha: informada pelo operador"; fi
}

confirm_installation() {
  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then return; fi

  wizard_header "Confirmacao" "$(wizard_step_label "10/10" "9/9")"
  print_selected_configuration_summary
  print_prerequisite_plan_summary
  echo

  local confirm
  read -r -p "Confirmar e iniciar instalacao com as acoes acima? (s/N): " confirm
  confirm="$(printf '%s' "$confirm" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$confirm" in
    s|sim|y|yes) ;;
    *) fail "Instalacao cancelada pelo usuario." ;;
  esac
}
