# Discovery RMM installer – service provisioning (Postgres, Redis, NATS)
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
  fi

  if sudo test -f "$nats_conf"; then
    NATS_BIND_HOST="${NATS_BIND_HOST:-$(sudo sed -n 's/^listen:[[:space:]]*\([^:]*\):[0-9][0-9]*.*/\1/p' "$nats_conf" | head -n 1)}"
    NATS_MONITOR_HOST="${NATS_MONITOR_HOST:-$(sudo sed -n 's/^http:[[:space:]]*\([^:]*\):[0-9][0-9]*.*/\1/p' "$nats_conf" | head -n 1)}"
    NATS_AUTH_CALLOUT_ISSUER="${NATS_AUTH_CALLOUT_ISSUER:-$(sudo sed -n 's/^[[:space:]]*issuer:[[:space:]]*"\([^"]*\)".*/\1/p' "$nats_conf" | head -n 1)}"
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

# ── NATS server configuration ──────────────────────────────────────────────

setup_nats() {
  log "Configurando NATS"
  local nats_conf; nats_conf="$(resolve_nats_conf_path)"
  sudo install -d -m 755 -o root -g root "$(dirname "$nats_conf")"

  local nats_ws_port="${NATS_WS_PORT:-8081}"
  local nats_ws_host="${NATS_WS_HOST:-127.0.0.1}"
  local nats_ws_tls="${NATS_WS_TLS_ENABLED:-false}"

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
${auth_block}${ws_block}
EOF

  sudo chmod 640 "$nats_conf"
  sudo chown root:nats "$nats_conf" || true

  sudo systemctl enable nats-server
  sudo systemctl restart nats-server
}
