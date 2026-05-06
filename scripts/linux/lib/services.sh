# Discovery RMM installer – service provisioning (Postgres, Redis, NATS, JetStream)
# Requires: common.sh (log, warn, fail, resolve_nats_conf_path)

setup_redis() {
  log "Configurando Redis"
  sudo systemctl enable redis-server
  sudo systemctl restart redis-server
}

ensure_pgvector_package() {
  local pg_major
  pg_major="$(psql --version | sed -n 's/.* \([0-9][0-9]*\)\..*/\1/p' | head -n 1)"
  [[ -n "$pg_major" ]] || fail "Nao foi possivel detectar versao major do PostgreSQL."

  local vector_pkg="postgresql-${pg_major}-pgvector"
  if dpkg -s "$vector_pkg" >/dev/null 2>&1; then
    log "Pacote ${vector_pkg} ja instalado"; return
  fi

  log "Instalando suporte a embeddings no PostgreSQL (${vector_pkg})"
  if ! sudo apt-get install -y "$vector_pkg"; then
    warn "Pacote ${vector_pkg} indisponivel neste host. O servidor continuara sem pgvector (embeddings)."
    return
  fi
}

setup_postgres() {
  log "Configurando PostgreSQL"
  local escaped_db_user="${POSTGRES_USER//\'/\'\'}"
  local escaped_db_name="${POSTGRES_DB//\'/\'\'}"
  local escaped_db_password="${POSTGRES_PASSWORD//\'/\'\'}"

  sudo systemctl enable postgresql
  sudo systemctl restart postgresql
  ensure_pgvector_package

  if ! sudo -u postgres psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='${escaped_db_user}'" | grep -q 1; then
    sudo -u postgres psql -c "CREATE USER \"${POSTGRES_USER}\" WITH PASSWORD '${escaped_db_password}';"
  else
    sudo -u postgres psql -c "ALTER USER \"${POSTGRES_USER}\" WITH PASSWORD '${escaped_db_password}';"
  fi

  if ! sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='${escaped_db_name}'" | grep -q 1; then
    sudo -u postgres psql -c "CREATE DATABASE \"${POSTGRES_DB}\" OWNER \"${POSTGRES_USER}\";"
  fi

  sudo -u postgres psql -c "GRANT ALL PRIVILEGES ON DATABASE \"${POSTGRES_DB}\" TO \"${POSTGRES_USER}\";"
  sudo -u postgres psql -d "${POSTGRES_DB}" -c "CREATE EXTENSION IF NOT EXISTS vector;"
}

# ── nk tool ────────────────────────────────────────────────────────────────

install_nk_tool() {
  if command -v nk &>/dev/null; then return 0; fi

  local arch; arch="$(uname -m)"
  local nk_arch
  case "$arch" in
    x86_64)  nk_arch="amd64" ;;
    aarch64) nk_arch="arm64" ;;
    armv7l)  nk_arch="armv7" ;;
    *)       fail "Arquitetura nao suportada para download do nk: $arch" ;;
  esac

  local nk_bin="/usr/local/bin/nk"
  local tmp_dir; tmp_dir="$(mktemp -d)"
  local nk_zip="$tmp_dir/nkeys.zip"; local nk_path=""
  local release_tag
  release_tag="$(curl -fsSL "https://api.github.com/repos/nats-io/nkeys/releases/latest" | jq -r '.tag_name')"
  [[ -n "$release_tag" && "$release_tag" != "null" ]] || fail "Nao foi possivel descobrir a versao mais recente do nkeys."

  local nk_url="https://github.com/nats-io/nkeys/releases/download/${release_tag}/nkeys-${release_tag}-linux-${nk_arch}.zip"
  log "Baixando ferramenta nk (geracao de chaves NATS)..."
  sudo apt-get install -y unzip >/dev/null
  curl -fsSL "$nk_url" -o "$nk_zip"
  unzip -q "$nk_zip" -d "$tmp_dir"

  nk_path="$(cd "$tmp_dir" && find . -type f -name nk | head -n 1)"
  nk_path="${nk_path#./}"; nk_path="$tmp_dir/$nk_path"
  [[ -n "$nk_path" ]] || fail "Nao foi possivel localizar o binario nk no pacote baixado."

  sudo install -m 0755 "$nk_path" "$nk_bin"
  rm -rf "$tmp_dir"
  log "nk instalado em $nk_bin"
}

install_nats_cli() {
  if command -v nats &>/dev/null; then return 0; fi

  local arch; arch="$(uname -m)"
  local nats_arch
  case "$arch" in
    x86_64)  nats_arch="amd64" ;;
    aarch64) nats_arch="arm64" ;;
    armv7l)  nats_arch="arm7" ;;
    *)       fail "Arquitetura nao suportada para download do nats CLI: $arch" ;;
  esac

  local tmp_dir; tmp_dir="$(mktemp -d)"
  local nats_zip="$tmp_dir/natscli.zip"
  local nats_bin="/usr/local/bin/nats"
  local release_json asset_url nats_path

  release_json="$(curl -fsSL "https://api.github.com/repos/nats-io/natscli/releases/latest")"
  asset_url="$(printf '%s' "$release_json" | jq -r --arg arch "$nats_arch" '.assets[]?.browser_download_url | select(test("linux-" + $arch + "\\.zip$"))' | head -n 1)"

  [[ -n "$asset_url" && "$asset_url" != "null" ]] || fail "Nao foi possivel localizar asset Linux do nats CLI para arquitetura $nats_arch."

  log "Baixando nats CLI para bootstrap de streams JetStream..."
  sudo apt-get install -y unzip >/dev/null
  curl -fsSL "$asset_url" -o "$nats_zip"
  unzip -q "$nats_zip" -d "$tmp_dir"

  nats_path="$(cd "$tmp_dir" && find . -type f -name nats | head -n 1)"
  nats_path="${nats_path#./}"
  nats_path="$tmp_dir/$nats_path"

  [[ -f "$nats_path" ]] || fail "Nao foi possivel localizar o binario nats no pacote baixado."

  sudo install -m 0755 "$nats_path" "$nats_bin"
  rm -rf "$tmp_dir"
  log "nats CLI instalado em $nats_bin"
}

generate_nats_account_keys() {
  if [[ -n "${NATS_ACCOUNT_SEED:-}" && -n "${NATS_ACCOUNT_PUBLIC_KEY:-}" ]]; then
    log "Seeds NATS ja existentes – reutilizando (sem regenerar)"
    if [[ -n "${NATS_XKEY_SEED:-}" && -z "${NATS_XKEY_PUBLIC_KEY:-}" ]]; then
      install_nk_tool
      NATS_XKEY_PUBLIC_KEY="$(nk -inkey <(printf '%s' "$NATS_XKEY_SEED") -pubout 2>/dev/null || true)"
      [[ -n "$NATS_XKEY_PUBLIC_KEY" ]] || fail "Falha ao derivar chave publica XKey do seed existente."
    fi
    return 0
  fi

  if [[ -n "${NATS_ACCOUNT_SEED:-}" ]]; then
    install_nk_tool
    NATS_ACCOUNT_PUBLIC_KEY="$(nk -inkey <(printf '%s' "$NATS_ACCOUNT_SEED") -pubout 2>/dev/null || true)"
    if [[ -n "$NATS_ACCOUNT_PUBLIC_KEY" ]]; then
      log "Chave publica NATS derivada do seed existente"
      if [[ -n "${NATS_XKEY_SEED:-}" && -z "${NATS_XKEY_PUBLIC_KEY:-}" ]]; then
        NATS_XKEY_PUBLIC_KEY="$(nk -inkey <(printf '%s' "$NATS_XKEY_SEED") -pubout 2>/dev/null || true)"
        [[ -n "$NATS_XKEY_PUBLIC_KEY" ]] || fail "Falha ao derivar chave publica XKey do seed existente."
      fi
      return 0
    fi
  fi

  install_nk_tool

  log "Gerando account key NATS..."
  local account_seed; account_seed="$(nk -gen account | head -n 1 | tr -d '\r')"
  NATS_ACCOUNT_SEED="$account_seed"
  NATS_ACCOUNT_PUBLIC_KEY="$(nk -inkey <(printf '%s' "$NATS_ACCOUNT_SEED") -pubout 2>/dev/null || true)"
  [[ -n "$NATS_ACCOUNT_PUBLIC_KEY" ]] || fail "Falha ao derivar chave publica da conta NATS a partir do seed."

  log "Gerando xkey NATS (criptografia do callout)..."
  local xkey_seed; xkey_seed="$(nk -gen curve | head -n 1 | tr -d '\r')"
  NATS_XKEY_SEED="$xkey_seed"
  NATS_XKEY_PUBLIC_KEY="$(nk -inkey <(printf '%s' "$NATS_XKEY_SEED") -pubout 2>/dev/null || true)"
  [[ -n "$NATS_XKEY_PUBLIC_KEY" ]] || fail "Falha ao derivar chave publica XKey do NATS a partir do seed."

  log "Chaves NATS geradas com sucesso. Issuer (account public key): $NATS_ACCOUNT_PUBLIC_KEY"
}

# ── Load existing defaults ─────────────────────────────────────────────────

load_existing_nats_defaults() {
  local env_file="/etc/discovery-api/discovery.env"
  local nats_conf; nats_conf="$(resolve_nats_conf_path)"

  if sudo test -f "$env_file"; then
    NATS_AUTH_USER="${NATS_AUTH_USER:-$(sudo awk -F= '/^Nats__AuthUser=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_AUTH_PASSWORD="${NATS_AUTH_PASSWORD:-$(sudo awk -F= '/^Nats__AuthPassword=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    local existing_callout_enabled
    existing_callout_enabled="$(sudo awk -F= '/^Nats__AuthCallout__Enabled=/{print tolower(substr($0, index($0,$2))); exit}' "$env_file" 2>/dev/null || true)"
    if [[ -z "${NATS_AUTH_CALLOUT_ENABLED:-}" && -n "$existing_callout_enabled" ]]; then
      if [[ "$existing_callout_enabled" == "true" ]]; then NATS_AUTH_CALLOUT_ENABLED="1"
      elif [[ "$existing_callout_enabled" == "false" ]]; then NATS_AUTH_CALLOUT_ENABLED="0"; fi
    fi
    NATS_AUTH_CALLOUT_SUBJECT="${NATS_AUTH_CALLOUT_SUBJECT:-$(sudo awk -F= '/^Nats__AuthCallout__Subject=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_SERVER_HOST_EXTERNAL="${NATS_SERVER_HOST_EXTERNAL:-$(sudo awk -F= '/^Nats__ServerHostExternal=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_SERVER_HOST_INTERNAL="${NATS_SERVER_HOST_INTERNAL:-$(sudo awk -F= '/^Nats__ServerHostInternal=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    local existing_use_wss_external
    existing_use_wss_external="$(sudo awk -F= '/^Nats__UseWssExternal=/{print tolower(substr($0, index($0,$2))); exit}' "$env_file" 2>/dev/null || true)"
    if [[ -z "${NATS_USE_WSS_EXTERNAL:-}" && -n "$existing_use_wss_external" ]]; then
      if [[ "$existing_use_wss_external" == "true" ]]; then NATS_USE_WSS_EXTERNAL="true"
      elif [[ "$existing_use_wss_external" == "false" ]]; then NATS_USE_WSS_EXTERNAL="false"; fi
    fi
    NATS_WS_PORT="${NATS_WS_PORT:-$(sudo awk -F= '/^NATS_WS_PORT=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_WS_HOST="${NATS_WS_HOST:-$(sudo awk -F= '/^NATS_WS_HOST=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_WS_TLS_ENABLED="${NATS_WS_TLS_ENABLED:-$(sudo awk -F= '/^NATS_WS_TLS_ENABLED=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_ACCOUNT_SEED="${NATS_ACCOUNT_SEED:-$(sudo awk -F= '/^Nats__AccountSeed=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_XKEY_SEED="${NATS_XKEY_SEED:-$(sudo awk -F= '/^Nats__XKeySeed=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_JS_ENABLED="${NATS_JS_ENABLED:-$(sudo awk -F= '/^NATS_JS_ENABLED=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_JS_STORE_DIR="${NATS_JS_STORE_DIR:-$(sudo awk -F= '/^NATS_JS_STORE_DIR=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_JS_MAX_MEMORY_STORE="${NATS_JS_MAX_MEMORY_STORE:-$(sudo awk -F= '/^NATS_JS_MAX_MEMORY_STORE=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_JS_MAX_FILE_STORE="${NATS_JS_MAX_FILE_STORE:-$(sudo awk -F= '/^NATS_JS_MAX_FILE_STORE=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_JS_FANOUT_STREAM_ENABLED="${NATS_JS_FANOUT_STREAM_ENABLED:-$(sudo awk -F= '/^NATS_JS_FANOUT_STREAM_ENABLED=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_JS_FANOUT_STREAM_NAME="${NATS_JS_FANOUT_STREAM_NAME:-$(sudo awk -F= '/^NATS_JS_FANOUT_STREAM_NAME=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_JS_FANOUT_STREAM_SUBJECTS="${NATS_JS_FANOUT_STREAM_SUBJECTS:-$(sudo awk -F= '/^NATS_JS_FANOUT_STREAM_SUBJECTS=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_JS_FANOUT_STREAM_MAX_AGE="${NATS_JS_FANOUT_STREAM_MAX_AGE:-$(sudo awk -F= '/^NATS_JS_FANOUT_STREAM_MAX_AGE=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_JS_FANOUT_STREAM_MAX_BYTES="${NATS_JS_FANOUT_STREAM_MAX_BYTES:-$(sudo awk -F= '/^NATS_JS_FANOUT_STREAM_MAX_BYTES=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
    NATS_JS_FANOUT_STREAM_DUPE_WINDOW="${NATS_JS_FANOUT_STREAM_DUPE_WINDOW:-$(sudo awk -F= '/^NATS_JS_FANOUT_STREAM_DUPE_WINDOW=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  fi

  if sudo test -f "$nats_conf"; then
    NATS_BIND_HOST="${NATS_BIND_HOST:-$(sudo sed -n 's/^listen:[[:space:]]*\([^:]*\):[0-9][0-9]*.*/\1/p' "$nats_conf" | head -n 1)}"
    NATS_MONITOR_HOST="${NATS_MONITOR_HOST:-$(sudo sed -n 's/^http:[[:space:]]*\([^:]*\):[0-9][0-9]*.*/\1/p' "$nats_conf" | head -n 1)}"
    NATS_AUTH_CALLOUT_ISSUER="${NATS_AUTH_CALLOUT_ISSUER:-$(sudo sed -n 's/^[[:space:]]*issuer:[[:space:]]*"\([^"]*\)".*/\1/p' "$nats_conf" | head -n 1)}"
    if [[ -z "${NATS_JS_ENABLED:-}" ]]; then
      if sudo grep -Eq '^[[:space:]]*jetstream[[:space:]]*\{' "$nats_conf"; then NATS_JS_ENABLED="1"
      else NATS_JS_ENABLED="0"; fi
    fi
    NATS_JS_STORE_DIR="${NATS_JS_STORE_DIR:-$(sudo sed -n 's/^[[:space:]]*store_dir:[[:space:]]*"\([^"]*\)".*/\1/p' "$nats_conf" | head -n 1)}"
    NATS_JS_MAX_MEMORY_STORE="${NATS_JS_MAX_MEMORY_STORE:-$(sudo sed -n 's/^[[:space:]]*max_memory_store:[[:space:]]*\([^[:space:]]*\).*/\1/p' "$nats_conf" | head -n 1)}"
    NATS_JS_MAX_FILE_STORE="${NATS_JS_MAX_FILE_STORE:-$(sudo sed -n 's/^[[:space:]]*max_file_store:[[:space:]]*\([^[:space:]]*\).*/\1/p' "$nats_conf" | head -n 1)}"
  fi
  NATS_USER="${NATS_USER:-${NATS_AUTH_USER:-discovery_nats}}"
  NATS_PASSWORD="${NATS_PASSWORD:-${NATS_AUTH_PASSWORD:-}}"
}

load_existing_site_realtime_defaults() {
  local env_file="/etc/discovery-api/discovery.env"
  sudo test -f "$env_file" || return 0

  DISCOVERY_SITE_NATS_URL="${DISCOVERY_SITE_NATS_URL:-$(sudo awk -F= '/^DISCOVERY_SITE_NATS_URL=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS="${DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS:-$(sudo awk -F= '/^DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  NATS_WS_PORT="${NATS_WS_PORT:-$(sudo awk -F= '/^NATS_WS_PORT=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  NATS_WS_HOST="${NATS_WS_HOST:-$(sudo awk -F= '/^NATS_WS_HOST=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  NATS_WS_TLS_ENABLED="${NATS_WS_TLS_ENABLED:-$(sudo awk -F= '/^NATS_WS_TLS_ENABLED=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  Authentication__Fido2__ServerDomain="${Authentication__Fido2__ServerDomain:-$(sudo awk -F= '/^Authentication__Fido2__ServerDomain=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
}

detect_nats_service_user() {
  local nats_user
  nats_user="$(systemctl show -p User --value nats-server 2>/dev/null | tr -d '[:space:]')"
  if [[ -z "$nats_user" ]]; then
    if id -u nats >/dev/null 2>&1; then nats_user="nats"
    else nats_user="root"; fi
  fi
  printf '%s' "$nats_user"
}

detect_nats_service_group() {
  local nats_user="$1"
  local nats_group
  nats_group="$(systemctl show -p Group --value nats-server 2>/dev/null | tr -d '[:space:]')"
  if [[ -z "$nats_group" ]]; then
    nats_group="$(id -gn "$nats_user" 2>/dev/null || true)"
  fi
  if [[ -z "$nats_group" ]]; then
    if getent group nats >/dev/null 2>&1; then nats_group="nats"
    else nats_group="root"; fi
  fi
  printf '%s' "$nats_group"
}

ensure_nats_fanout_stream() {
  if [[ "${NATS_JS_ENABLED:-1}" != "1" ]]; then
    log "JetStream desativado; pulando bootstrap do stream de fan-out."
    return
  fi

  if [[ "${NATS_JS_FANOUT_STREAM_ENABLED:-1}" != "1" ]]; then
    log "Bootstrap automatico do stream de fan-out desativado por configuracao."
    return
  fi

  install_nats_cli

  local stream_name="${NATS_JS_FANOUT_STREAM_NAME:-DISCOVERY_FANOUT_COMMANDS}"
  local stream_subjects="${NATS_JS_FANOUT_STREAM_SUBJECTS:-tenant.*.site.*.agents.command,tenant.*.agents.command,tenant.global.agents.command}"
  local stream_max_age="${NATS_JS_FANOUT_STREAM_MAX_AGE:-24h}"
  local stream_max_bytes="${NATS_JS_FANOUT_STREAM_MAX_BYTES:-134217728}"
  local stream_dupe_window="${NATS_JS_FANOUT_STREAM_DUPE_WINDOW:-2m}"
  local -a nats_cmd=(
    nats
    --server "nats://127.0.0.1:4222"
    --user "$NATS_AUTH_USER"
    --password "$NATS_AUTH_PASSWORD"
  )

  local attempt
  for attempt in {1..10}; do
    if "${nats_cmd[@]}" stream info "$stream_name" >/dev/null 2>&1; then
      if "${nats_cmd[@]}" stream update "$stream_name" \
        --subjects "$stream_subjects" \
        --storage file \
        --retention limits \
        --discard old \
        --max-age "$stream_max_age" \
        --max-bytes "$stream_max_bytes" \
        --dupe-window "$stream_dupe_window" \
        --defaults >/dev/null 2>&1; then
        log "Stream JetStream '$stream_name' atualizado para fan-out de comandos."
        return
      fi
    else
      if "${nats_cmd[@]}" stream add "$stream_name" \
        --subjects "$stream_subjects" \
        --storage file \
        --retention limits \
        --discard old \
        --max-age "$stream_max_age" \
        --max-bytes "$stream_max_bytes" \
        --dupe-window "$stream_dupe_window" \
        --defaults >/dev/null 2>&1; then
        log "Stream JetStream '$stream_name' criado para fan-out de comandos."
        return
      fi
    fi
    sleep 2
  done

  fail "Nao foi possivel criar/atualizar o stream JetStream de fan-out ('${stream_name}')."
}

# ── NATS server configuration ──────────────────────────────────────────────

setup_nats() {
  log "Configurando NATS"
  local nats_conf; nats_conf="$(resolve_nats_conf_path)"
  sudo install -d -m 755 -o root -g root "$(dirname "$nats_conf")"

  local nats_ws_port="${NATS_WS_PORT:-8081}"
  local nats_ws_host="${NATS_WS_HOST:-127.0.0.1}"
  local nats_ws_tls="${NATS_WS_TLS_ENABLED:-false}"
  local nats_js_enabled="${NATS_JS_ENABLED:-1}"
  local nats_js_store_dir="${NATS_JS_STORE_DIR:-${DISCOVERY_OPS_DIR:-/opt/discovery-ops}/nats/jetstream}"
  local nats_js_max_memory_store="${NATS_JS_MAX_MEMORY_STORE:-64M}"
  local nats_js_max_file_store="${NATS_JS_MAX_FILE_STORE:-512M}"

  local jetstream_block=""
  if [[ "$nats_js_enabled" == "1" ]]; then
    local nats_service_user nats_service_group
    nats_service_user="$(detect_nats_service_user)"
    nats_service_group="$(detect_nats_service_group "$nats_service_user")"

    if [[ -n "${DISCOVERY_OPS_DIR:-}" && "$nats_js_store_dir" == "${DISCOVERY_OPS_DIR}"* ]]; then
      if sudo test -d "$DISCOVERY_OPS_DIR"; then
        sudo chmod 751 "$DISCOVERY_OPS_DIR"
      else
        if id -u discovery-api >/dev/null 2>&1; then
          sudo install -d -m 751 -o discovery-api -g discovery-api "$DISCOVERY_OPS_DIR"
        else
          sudo install -d -m 751 -o root -g root "$DISCOVERY_OPS_DIR"
        fi
      fi
    fi

    sudo install -d -m 750 -o "$nats_service_user" -g "$nats_service_group" "$(dirname "$nats_js_store_dir")"
    sudo install -d -m 750 -o "$nats_service_user" -g "$nats_service_group" "$nats_js_store_dir"

    jetstream_block=$(cat <<EOF

jetstream {
  store_dir: "$nats_js_store_dir"
  max_memory_store: $nats_js_max_memory_store
  max_file_store: $nats_js_max_file_store
}
EOF
)
  fi

  local ws_block=""
  if [[ "$nats_ws_tls" == "true" ]]; then
    ws_block=$(cat <<"EOF"

websocket {
  port: __WS_PORT__
  host: __WS_HOST__
  tls {
    cert_file: "/etc/discovery-api/certs/api-internal.crt"
    key_file: "/etc/discovery-api/certs/api-internal.key"
  }
}
EOF
)
  else
    ws_block=$(cat <<"EOF"

websocket {
  port: __WS_PORT__
  host: __WS_HOST__
  no_tls: true
}
EOF
)
  fi
  ws_block="${ws_block//__WS_PORT__/$nats_ws_port}"
  ws_block="${ws_block//__WS_HOST__/$nats_ws_host}"

  local auth_block
  if [[ "$NATS_AUTH_CALLOUT_ENABLED" == "1" ]]; then
    local xkey_line=""
    if [[ -n "${NATS_XKEY_PUBLIC_KEY:-}" ]]; then
      xkey_line=$'\n    xkey: "'"$NATS_XKEY_PUBLIC_KEY"$'"'
    fi
    auth_block=$(cat <<EOF
authorization {
  timeout: 1
  users = [
    { user: "$NATS_AUTH_USER", password: "$NATS_AUTH_PASSWORD" }
  ]
  auth_callout {
    issuer: "$NATS_ACCOUNT_PUBLIC_KEY"
    auth_users: [ "$NATS_AUTH_USER" ]${xkey_line}
  }
  default_permissions {
    publish = ["\$SYS.>"]
    subscribe = ["\$SYS.>"]
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
max_payload: 4194304
max_connections: 5000
write_deadline: 5s
${jetstream_block}${auth_block}${ws_block}
EOF

  sudo chmod 640 "$nats_conf"
  sudo chown root:nats "$nats_conf" || true

  sudo systemctl enable nats-server
  sudo systemctl restart nats-server
}
