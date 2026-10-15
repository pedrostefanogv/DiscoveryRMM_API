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

# O painel da Cloudflare entrega o token embutido em um comando. Extrai o token
# de um texto colado: comando com `--token <valor>`, comando com o token solto,
# ou o proprio token. Nao executa nada do texto colado.
extract_cloudflare_tunnel_token() {
  local raw="${1:-}"
  local token=""

  # 1) Formato explicito `--token <valor>` (com ou sem quotes).
  if [[ "$raw" =~ --token[=\ ]+\"?([A-Za-z0-9._=-]+)\"? ]]; then
    token="${BASH_REMATCH[1]}"
  fi

  # 2) Fallback: primeiro token no formato do Cloudflare (comeca com "eyJ").
  if [[ -z "$token" ]]; then
    token="$(printf '%s' "$raw" | grep -oE 'eyJ[A-Za-z0-9_.-]{20,}=*' | head -n 1 || true)"
  fi

  # 3) Ultimo recurso: primeira palavra nao vazia (token puro colado).
  if [[ -z "$token" ]]; then
    token="$(printf '%s' "$raw" | tr -s '[:space:]' '\n' | grep -v '^$' | head -n 1 || true)"
  fi

  token="${token%\"}"; token="${token#\"}"
  token="${token%\'}"; token="${token#\'}"
  printf '%s' "$token"
}

is_valid_cloudflare_tunnel_token() {
  local token="${1:-}"
  [[ "$token" =~ ^eyJ[A-Za-z0-9_.-]{20,}=*$ ]]
}

# Normaliza/revalida CLOUDFLARE_TUNNEL_TOKEN (aceita o comando colado do painel).
normalize_cloudflare_tunnel_token() {
  local raw="${CLOUDFLARE_TUNNEL_TOKEN:-}"
  CLOUDFLARE_TUNNEL_TOKEN="$(extract_cloudflare_tunnel_token "$raw")"
  if ! is_valid_cloudflare_tunnel_token "$CLOUDFLARE_TUNNEL_TOKEN"; then
    fail "CLOUDFLARE_TUNNEL_TOKEN invalido. Informe o token (comeca com 'eyJ') ou o comando do cloudflared que o contem."
  fi
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

normalize_letsencrypt_auto_renew_enabled() {
  local normalized
  normalized="$(printf '%s' "${LETSENCRYPT_AUTO_RENEW_ENABLED:-1}" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$normalized" in
    1|true|yes|y|sim|s)       LETSENCRYPT_AUTO_RENEW_ENABLED=1 ;;
    0|false|no|n|nao|"")      LETSENCRYPT_AUTO_RENEW_ENABLED=0 ;;
    *) fail "LETSENCRYPT_AUTO_RENEW_ENABLED invalido: ${LETSENCRYPT_AUTO_RENEW_ENABLED}. Use 1/0 (ou sim/nao)." ;;
  esac
}

normalize_nats_settings() {
  NATS_BIND_HOST="${NATS_BIND_HOST:-0.0.0.0}"
  NATS_MONITOR_HOST="${NATS_MONITOR_HOST:-127.0.0.1}"
  NATS_AUTH_USER="${NATS_AUTH_USER:-$NATS_USER}"
  NATS_AUTH_PASSWORD="${NATS_AUTH_PASSWORD:-$NATS_PASSWORD}"
  NATS_AUTH_CALLOUT_SUBJECT="${NATS_AUTH_CALLOUT_SUBJECT:-\$SYS.REQ.USER.AUTH}"
  NATS_AUTH_CALLOUT_ENABLED="${NATS_AUTH_CALLOUT_ENABLED:-1}"

  # Aceita y/n (padrao dos prompts de wizard) alem de 1/0/true/false.
  local normalized_callout
  normalized_callout="$(printf '%s' "$NATS_AUTH_CALLOUT_ENABLED" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$normalized_callout" in
    1|true|yes|y|sim|s) NATS_AUTH_CALLOUT_ENABLED="1" ;;
    0|false|no|n|nao)   NATS_AUTH_CALLOUT_ENABLED="0" ;;
    *) fail "NATS_AUTH_CALLOUT_ENABLED invalido: ${NATS_AUTH_CALLOUT_ENABLED}. Use y/n (ou 1/0)." ;;
  esac

  NATS_WS_PORT="${NATS_WS_PORT:-8081}"
  NATS_WS_HOST="${NATS_WS_HOST:-127.0.0.1}"
  NATS_WS_TLS_ENABLED="${NATS_WS_TLS_ENABLED:-false}"
  NATS_JS_ENABLED="${NATS_JS_ENABLED:-1}"
  NATS_JS_STORE_DIR="${NATS_JS_STORE_DIR:-${DISCOVERY_OPS_DIR:-/opt/discovery-ops}/nats/jetstream}"
  NATS_JS_MAX_MEMORY_STORE="${NATS_JS_MAX_MEMORY_STORE:-64M}"
  NATS_JS_MAX_FILE_STORE="${NATS_JS_MAX_FILE_STORE:-512M}"
  NATS_JS_FANOUT_STREAM_NAME="${NATS_JS_FANOUT_STREAM_NAME:-DISCOVERY_FANOUT_COMMANDS}"
  NATS_JS_FANOUT_STREAM_SUBJECTS="${NATS_JS_FANOUT_STREAM_SUBJECTS:-tenant.*.site.*.agents.command,tenant.*.agents.command}"
  NATS_JS_FANOUT_STREAM_MAX_AGE="${NATS_JS_FANOUT_STREAM_MAX_AGE:-24h}"
  NATS_JS_FANOUT_STREAM_MAX_BYTES="${NATS_JS_FANOUT_STREAM_MAX_BYTES:-134217728}"
  NATS_JS_FANOUT_STREAM_DUPE_WINDOW="${NATS_JS_FANOUT_STREAM_DUPE_WINDOW:-2m}"

  local normalized_js_enabled
  normalized_js_enabled="$(printf '%s' "$NATS_JS_ENABLED" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$normalized_js_enabled" in
    1|true|yes|y|sim|s) NATS_JS_ENABLED="1" ;;
    0|false|no|n|nao) NATS_JS_ENABLED="0" ;;
    *) fail "NATS_JS_ENABLED invalido: ${NATS_JS_ENABLED}. Use 1/0 (ou sim/nao)." ;;
  esac

  # O fan-out depende do JetStream: se o operador nao definir
  # NATS_JS_FANOUT_STREAM_ENABLED, ele segue o NATS_JS_ENABLED. Antes o default
  # era sempre 1 e, num host com JetStream desabilitado (ex.: nats-server.conf
  # antiga/sem bloco "jetstream"), o wizard falhava no final com
  # "NATS_JS_FANOUT_STREAM_ENABLED=1 exige NATS_JS_ENABLED=1".
  NATS_JS_FANOUT_STREAM_ENABLED="${NATS_JS_FANOUT_STREAM_ENABLED:-$NATS_JS_ENABLED}"

  local normalized_stream_enabled
  normalized_stream_enabled="$(printf '%s' "$NATS_JS_FANOUT_STREAM_ENABLED" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$normalized_stream_enabled" in
    1|true|yes|y|sim|s) NATS_JS_FANOUT_STREAM_ENABLED="1" ;;
    0|false|no|n|nao) NATS_JS_FANOUT_STREAM_ENABLED="0" ;;
    *) fail "NATS_JS_FANOUT_STREAM_ENABLED invalido: ${NATS_JS_FANOUT_STREAM_ENABLED}. Use 1/0 (ou sim/nao)." ;;
  esac

  NATS_JS_MAX_MEMORY_STORE="$(printf '%s' "$NATS_JS_MAX_MEMORY_STORE" | tr '[:lower:]' '[:upper:]')"
  NATS_JS_MAX_FILE_STORE="$(printf '%s' "$NATS_JS_MAX_FILE_STORE" | tr '[:lower:]' '[:upper:]')"

  [[ "$NATS_JS_STORE_DIR" = /* ]] || fail "NATS_JS_STORE_DIR deve ser caminho absoluto: ${NATS_JS_STORE_DIR}"
  [[ "$NATS_JS_FANOUT_STREAM_MAX_BYTES" =~ ^[0-9]+$ ]] || fail "NATS_JS_FANOUT_STREAM_MAX_BYTES deve ser inteiro em bytes: ${NATS_JS_FANOUT_STREAM_MAX_BYTES}"

  if [[ "$NATS_JS_FANOUT_STREAM_ENABLED" == "1" && "$NATS_JS_ENABLED" != "1" ]]; then
    fail "NATS_JS_FANOUT_STREAM_ENABLED=1 exige NATS_JS_ENABLED=1 (defina NATS_JS_ENABLED=1 ou NATS_JS_FANOUT_STREAM_ENABLED=0)"
  fi

  if [[ "$NATS_JS_ENABLED" != "1" ]]; then
    warn "NATS JetStream desabilitado (NATS_JS_ENABLED=0): o stream de fan-out nao sera criado e comandos fan-out nao terao replay."
  fi

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
    letsencrypt|lets-encrypt|le|letsencrypt-acme) TLS_CERT_PROVIDER="letsencrypt-acme" ;;
    *) fail "TLS_CERT_PROVIDER invalido: $provider (use self-signed, zerossl-acme ou letsencrypt-acme)" ;;
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

  if [[ "$TLS_CERT_PROVIDER" == "letsencrypt-acme" ]]; then
    LETSENCRYPT_CERT_DOMAIN="$(normalize_host_without_scheme "${LETSENCRYPT_CERT_DOMAIN:-$(resolve_fido2_server_domain)}")"
    LETSENCRYPT_CERT_ALT_DOMAINS="${LETSENCRYPT_CERT_ALT_DOMAINS:-}"
    LETSENCRYPT_DNS_RESOLVERS="${LETSENCRYPT_DNS_RESOLVERS:-1.1.1.1,8.8.8.8}"
    LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS="${LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS:-600}"
    LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS="${LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS:-15}"
    LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY="${LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY:-30}"
    LETSENCRYPT_AUTO_RENEW_ENABLED="${LETSENCRYPT_AUTO_RENEW_ENABLED:-1}"
    normalize_letsencrypt_auto_renew_enabled
    LETSENCRYPT_DNS_AUTOMATION_HOOK="${LETSENCRYPT_DNS_AUTOMATION_HOOK:-}"
  fi
}

validate_security_inputs() {
  # Senha Postgres: comprimento minimo + rejeicao de placeholders de exemplo.
  if [[ -n "${POSTGRES_PASSWORD:-}" ]]; then
    if (( ${#POSTGRES_PASSWORD} < 12 )); then
      fail "POSTGRES_PASSWORD precisa ter pelo menos 12 caracteres."
    fi
    local pw_lower
    pw_lower="$(printf '%s' "$POSTGRES_PASSWORD" | tr '[:upper:]' '[:lower:]')"
    local weak_password
    for weak_password in "troque-esta-senha" "changeme" "changeme123" "password" "password1" "password123" "123456" "12345678" "admin123" "discovery" "discovery123"; do
      if [[ "$pw_lower" == "$weak_password" ]]; then
        fail "POSTGRES_PASSWORD usa um valor de exemplo/placeholder; defina uma senha forte e unica."
      fi
    done
  fi

  local effective_nats_password
  effective_nats_password="${NATS_AUTH_PASSWORD:-${NATS_PASSWORD:-}}"
  [[ -n "$effective_nats_password" ]] || fail "Senha NATS nao informada."

  if (( ${#effective_nats_password} < 12 )); then
    fail "NATS_PASSWORD precisa ter pelo menos 12 caracteres."
  fi

  # Charset da senha NATS: a senha entra em heredoc do nats-server.conf e na
  # CLI — caracteres como aspas, $, backtick ou espaco quebram/injetam config.
  if [[ ! "$effective_nats_password" =~ ^[A-Za-z0-9._+!@#%^*-]{12,128}$ ]]; then
    fail "NATS_PASSWORD invalida: use 12-128 caracteres de [A-Za-z0-9._+!@#%^*-] (sem aspas, espacos, \$ ou backtick)."
  fi

  # Usuario NATS entra no conf do servidor entre aspas.
  local effective_nats_user="${NATS_AUTH_USER:-${NATS_USER:-}}"
  if [[ -n "$effective_nats_user" && ! "$effective_nats_user" =~ ^[A-Za-z0-9._-]{1,64}$ ]]; then
    fail "NATS_USER invalido: use 1-64 caracteres de [A-Za-z0-9._-]."
  fi

  # Identificadores PostgreSQL entram interpolados em SQL — exigir identificador
  # valido elimina quebra/injecao via aspas/semicolon.
  if [[ -n "${POSTGRES_USER:-}" && ! "${POSTGRES_USER}" =~ ^[A-Za-z_][A-Za-z0-9_]{0,62}$ ]]; then
    fail "POSTGRES_USER invalido: use letras/digito/underscore (ate 64, iniciando por letra ou _)."
  fi
  if [[ -n "${POSTGRES_DB:-}" && ! "${POSTGRES_DB}" =~ ^[A-Za-z_][A-Za-z0-9_]{0,62}$ ]]; then
    fail "POSTGRES_DB invalido: use letras/digitos/underscore (ate 64, iniciando por letra ou _)."
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


# ── Object storage (arquivos/anexos) ───────────────────────────────────────

# Le valores ja persistidos (adoption em reinstall/update): env da API em
# primeiro lugar (e a fonte de verdade para o modo escolhido) e o
# /etc/default/rustfs para reutilizar as credenciais do RustFS ja instalado.
load_existing_object_storage_defaults() {
  local env_file="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"
  if sudo test -f "$env_file"; then
    local key value
    for key in DISCOVERY_OBJECT_STORAGE_MODE DISCOVERY_OBJECT_STORAGE_BUCKET \
               DISCOVERY_OBJECT_STORAGE_ENDPOINT DISCOVERY_OBJECT_STORAGE_REGION \
               DISCOVERY_OBJECT_STORAGE_ACCESS_KEY DISCOVERY_OBJECT_STORAGE_SECRET_KEY \
               DISCOVERY_OBJECT_STORAGE_USE_PATH_STYLE DISCOVERY_OBJECT_STORAGE_SSL_VERIFY \
               DISCOVERY_OBJECT_STORAGE_URL_TTL_HOURS DISCOVERY_OBJECT_STORAGE_NGINX_ENABLED \
               DISCOVERY_OBJECT_STORAGE_MAX_UPLOAD RUSTFS_VERSION; do
      [[ -n "${!key:-}" ]] && continue
      value="$(sudo awk -F= -v k="$key" '$1==k{sub("^[^=]*=","");print;exit}' "$env_file" 2>/dev/null || true)"
      [[ -n "$value" ]] && printf -v "$key" '%s' "$value"
    done
  fi

  if sudo test -f /etc/default/rustfs; then
    local rust_access rust_secret
    rust_access="$(sudo awk -F= '/^RUSTFS_ACCESS_KEY=/{sub("^[^=]*=","");print;exit}' /etc/default/rustfs 2>/dev/null || true)"
    rust_secret="$(sudo awk -F= '/^RUSTFS_SECRET_KEY=/{sub("^[^=]*=","");print;exit}' /etc/default/rustfs 2>/dev/null || true)"
    [[ -z "${DISCOVERY_OBJECT_STORAGE_ACCESS_KEY:-}" && -n "$rust_access" ]] && DISCOVERY_OBJECT_STORAGE_ACCESS_KEY="$rust_access"
    [[ -z "${DISCOVERY_OBJECT_STORAGE_SECRET_KEY:-}" && -n "$rust_secret" ]] && DISCOVERY_OBJECT_STORAGE_SECRET_KEY="$rust_secret"
  fi

  # ADOPTION: RustFS instalado nesta maquina mas modo nao persistido (env antigo).
  # Sem esta inferencia o update trataria como "skip": nao publicaria o caminho do
  # bucket no nginx nem garantiria a configuracao no banco. Nao instala nem
  # atualiza nada — apenas reconhece o que ja existe.
  local current_mode="${DISCOVERY_OBJECT_STORAGE_MODE:-}"
  current_mode="$(printf '%s' "$current_mode" | tr '[:upper:]' '[:lower:]')"
  if [[ -z "$current_mode" || "$current_mode" == "skip" ]]; then
    if sudo test -x /opt/rustfs/bin/rustfs \
       && sudo systemctl list-unit-files --no-legend rustfs.service >/dev/null 2>&1; then
      DISCOVERY_OBJECT_STORAGE_MODE="rustfs"
      log "Storage: RustFS detectado nesta maquina; adotando o modo rustfs (sem instalar/atualizar)."
    fi
  fi
}

# Endpoint default do storage: o MESMO host do portal (o path do bucket entra
# pelo location do nginx). Usa o dominio do certificado quando existe (HTTPS
# confiavel); caso contrario cai para o host interno em HTTP.
resolve_object_storage_default_endpoint() {
  # Le do shell E do discovery.env: no modo UPDATE o shell nao tem as variaveis
  # de dominio (elas vivem no env) e a derivacao falhava com "Nao foi possivel
  # derivar o endpoint do RustFS". Loopback e sempre descartado (agente/
  # navegador nao alcancam o localhost do servidor).
  local env_file="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"
  _os_env() { sudo awk -F= -v k="$1" '$1==k{sub("^[^=]*=","");print;exit}' "$env_file" 2>/dev/null || true; }

  local candidate https_host=""
  for candidate in \
    "$(_os_env ZEROSSL_CERT_DOMAIN)" "$(_os_env LETSENCRYPT_CERT_DOMAIN)" \
    "${ZEROSSL_CERT_DOMAIN:-}" "${LETSENCRYPT_CERT_DOMAIN:-}" \
    "$(_os_env Authentication__Fido2__ServerDomain)" \
    "$(_os_env EXTERNAL_API_HOST)" "${EXTERNAL_API_HOST:-}"; do
    candidate="$(normalize_host_without_scheme "$candidate")"
    case "$candidate" in
      ""|localhost|127.*|0.0.0.0|[::1]|::1) continue ;;
      *) https_host="$candidate"; break ;;
    esac
  done
  if [[ -n "$https_host" ]]; then
    printf 'https://%s' "$https_host"
    return
  fi

  local internal_host=""
  for candidate in "$(_os_env INTERNAL_API_HOST)" "${INTERNAL_API_HOST:-}"; do
    candidate="$(normalize_host_without_scheme "$candidate")"
    case "$candidate" in
      ""|localhost|127.*|0.0.0.0|[::1]|::1) continue ;;
      *) internal_host="$candidate"; break ;;
    esac
  done
  if [[ -n "$internal_host" ]]; then
    printf 'http://%s' "$internal_host"
    return
  fi
  printf ''
}

normalize_object_storage_settings() {
  # Endpoint PUBLICO persistido como loopback (ex.: "https://localhost" gerado
  # por um update antigo) e inutil: agentes/navegadores nao alcancam. Limpa para
  # a derivacao abaixo recalcular o host correto.
  case "$(printf '%s' "${DISCOVERY_OBJECT_STORAGE_ENDPOINT:-}" | tr '[:upper:]' '[:lower:]')" in
    *localhost*|*127.0.0.1*|*0.0.0.0*|*\[::1\]*) DISCOVERY_OBJECT_STORAGE_ENDPOINT="" ;;
  esac

  local raw_mode="${DISCOVERY_OBJECT_STORAGE_MODE:-skip}"
  raw_mode="$(printf '%s' "$raw_mode" | tr '[:upper:]' '[:lower:]')"
  case "$raw_mode" in
    1|local|rustfs)    DISCOVERY_OBJECT_STORAGE_MODE="rustfs" ;;
    2|external|s3)     DISCOVERY_OBJECT_STORAGE_MODE="external" ;;
    3|skip|none|"")    DISCOVERY_OBJECT_STORAGE_MODE="skip" ;;
    *) fail "DISCOVERY_OBJECT_STORAGE_MODE invalido: $raw_mode (use rustfs, external ou skip)" ;;
  esac

  RUSTFS_VERSION="${RUSTFS_VERSION:-1.0.1}"
  RUSTFS_ADDRESS="${RUSTFS_ADDRESS:-127.0.0.1:9000}"
  RUSTFS_DATA_DIR="${RUSTFS_DATA_DIR:-/opt/rustfs/data}"
  DISCOVERY_OBJECT_STORAGE_BUCKET="${DISCOVERY_OBJECT_STORAGE_BUCKET:-discovery-files}"
  DISCOVERY_OBJECT_STORAGE_REGION="${DISCOVERY_OBJECT_STORAGE_REGION:-us-east-1}"
  DISCOVERY_OBJECT_STORAGE_PORT="${DISCOVERY_OBJECT_STORAGE_PORT:-9000}"
  DISCOVERY_OBJECT_STORAGE_URL_TTL_HOURS="${DISCOVERY_OBJECT_STORAGE_URL_TTL_HOURS:-24}"
  DISCOVERY_OBJECT_STORAGE_MAX_UPLOAD="${DISCOVERY_OBJECT_STORAGE_MAX_UPLOAD:-64m}"
  DISCOVERY_OBJECT_STORAGE_NGINX_ENABLED="${DISCOVERY_OBJECT_STORAGE_NGINX_ENABLED:-y}"
  DISCOVERY_OBJECT_STORAGE_SSL_VERIFY="${DISCOVERY_OBJECT_STORAGE_SSL_VERIFY:-true}"
  # S3-compativeis (RustFS/MinIO/R2) usam path-style; AWS S3 puro usa
  # virtual-hosted e o proprio SDK resolve pelo host. O valor aqui documenta a
  # intencao e e gravado no banco.
  DISCOVERY_OBJECT_STORAGE_USE_PATH_STYLE="${DISCOVERY_OBJECT_STORAGE_USE_PATH_STYLE:-true}"

  case "$DISCOVERY_OBJECT_STORAGE_NGINX_ENABLED" in
    1|y|yes|true|on|Y|YES) DISCOVERY_OBJECT_STORAGE_NGINX_ENABLED="y" ;;
    *) DISCOVERY_OBJECT_STORAGE_NGINX_ENABLED="n" ;;
  esac

  if [[ "$DISCOVERY_OBJECT_STORAGE_MODE" == "skip" ]]; then
    return 0
  fi

  [[ "$DISCOVERY_OBJECT_STORAGE_BUCKET" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] \
    || fail "DISCOVERY_OBJECT_STORAGE_BUCKET invalido: $DISCOVERY_OBJECT_STORAGE_BUCKET (minusculas, digitos, ponto e hifen; 3-63)"
  [[ "$DISCOVERY_OBJECT_STORAGE_PORT" =~ ^[0-9]{2,5}$ ]] \
    || fail "DISCOVERY_OBJECT_STORAGE_PORT invalido: $DISCOVERY_OBJECT_STORAGE_PORT"
  [[ "$DISCOVERY_OBJECT_STORAGE_URL_TTL_HOURS" =~ ^[0-9]+$ ]] \
    || fail "DISCOVERY_OBJECT_STORAGE_URL_TTL_HOURS invalido: $DISCOVERY_OBJECT_STORAGE_URL_TTL_HOURS"

  if [[ "$DISCOVERY_OBJECT_STORAGE_MODE" == "rustfs" && -z "${DISCOVERY_OBJECT_STORAGE_ENDPOINT:-}" ]]; then
    DISCOVERY_OBJECT_STORAGE_ENDPOINT="$(resolve_object_storage_default_endpoint)"
  fi
  if [[ "$DISCOVERY_OBJECT_STORAGE_MODE" == "rustfs" && -z "${DISCOVERY_OBJECT_STORAGE_ENDPOINT:-}" ]]; then
    fail "Nao foi possivel derivar o endpoint do RustFS; informe DISCOVERY_OBJECT_STORAGE_ENDPOINT (host ou http(s)://host, sem path)."
  fi

  if [[ -n "${DISCOVERY_OBJECT_STORAGE_ENDPOINT:-}" ]]; then
    # O banco guarda host[:porta] (com ou sem scheme): um path quebraria a
    # assinatura das URLs pre-assinadas e e rejeitado pelo proprio backend.
    [[ "$DISCOVERY_OBJECT_STORAGE_ENDPOINT" =~ ^(https?://)?[A-Za-z0-9.-]+(:[0-9]{2,5})?$ ]] \
      || fail "DISCOVERY_OBJECT_STORAGE_ENDPOINT deve ser host[:porta] ou http(s)://host[:porta], sem path: $DISCOVERY_OBJECT_STORAGE_ENDPOINT"
  fi

  if [[ "$DISCOVERY_OBJECT_STORAGE_MODE" == "external" ]]; then
    [[ -n "${DISCOVERY_OBJECT_STORAGE_ENDPOINT:-}" ]] || fail "Informe DISCOVERY_OBJECT_STORAGE_ENDPOINT para o provedor externo."
    [[ -n "${DISCOVERY_OBJECT_STORAGE_ACCESS_KEY:-}" ]] || fail "Informe DISCOVERY_OBJECT_STORAGE_ACCESS_KEY para o provedor externo."
    [[ -n "${DISCOVERY_OBJECT_STORAGE_SECRET_KEY:-}" ]] || fail "Informe DISCOVERY_OBJECT_STORAGE_SECRET_KEY para o provedor externo."
  fi
}
normalize_install_branch_input() {
  local raw_branch="$1"
  local normalized_branch
  normalized_branch="$(printf '%s' "$raw_branch" | tr '[:upper:]' '[:lower:]')"
  case "$normalized_branch" in
    lts|release|beta|dev) printf '%s' "$normalized_branch"; return ;;
  esac
  # Sem '-' inicial (injecao de flag em `git clone -b`) e sem '..' (traversal).
  [[ "$raw_branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]*$ ]] || fail "Branch invalida: $raw_branch"
  [[ "$raw_branch" != *..* ]] || fail "Branch invalida (sequencia '..' nao permitida): $raw_branch"
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

  local installed=0
  is_discovery_installed && installed=1

  local default_option="1"
  [[ "$installed" -eq 1 ]] && default_option="3"

  while true; do
    wizard_header "Modo de Operacao" "$(wizard_step_label "2/8" "1/7")"
    echo "Escolha o que sera executado neste momento:"
    if [[ "$installed" -eq 1 ]]; then
      echo "1) Instalacao completa (API + portal web + Postgres + NATS + servicos) [ja instalado]"
    else
      echo "1) Instalacao completa (API + portal web + Postgres + NATS + servicos)"
    fi
    echo "2) Atualizar somente configuracao do NATS (inclui issuer/auth_callout)"
    echo "3) Atualizar instalacao existente (repositorios + rebuild API/portal + restart servicos)"
    echo "4) Ver dados do servidor (instalacao atual, usuario, senha, chaves e afins)"
    echo "5) Manutencao avancada (reset senha/MFA e recovery de usuario admin)"
    echo "----------------------------------------"

    local selected_option
    read -r -p "Opcao [${default_option}]: " selected_option
    selected_option="${selected_option:-$default_option}"
    selected_option="$(printf '%s' "$selected_option" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    case "$selected_option" in
      1|full|install|complete)
        if [[ "$installed" -eq 1 ]]; then
          echo "Sistema ja instalado. Use a opcao 3 para atualizar ou a opcao 1 apenas se quiser reinstalar do zero."
          echo "Para reinstalar do zero, digite 'full' novamente para confirmar."
          local confirm
          read -r -p "Confirmar reinstalacao completa? (s/N): " confirm
          confirm="$(printf '%s' "${confirm:-n}" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
          case "$confirm" in
            s|sim|y|yes|1) ;;
            *) echo "Reinstalacao cancelada. Voltando ao menu..."; continue ;;
          esac
        fi
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

  echo; echo "[discovery.env] (valores sensiveis mascarados)"
  if sudo test -f /etc/discovery-api/discovery.env; then
    sudo awk -F= '
      {
        v = toupper($1)
        if (NF > 1 && v ~ /PASSWORD|PASSWD|SEED|TOKEN|SECRET|EAB|HMAC|SIGNINGKEY|CONNECTIONSTRINGS|PRIVATEKEY/) {
          print $1 "=********"
        } else {
          print
        }
      }' /etc/discovery-api/discovery.env | sed 's/^/  /'
  else
    echo "  Arquivo /etc/discovery-api/discovery.env nao encontrado."
  fi

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
