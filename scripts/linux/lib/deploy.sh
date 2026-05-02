# Discovery RMM installer – deployment (publish, environment file, systemd, Nginx)
# Requires: common.sh (log, warn, fail, build_https_origin_from_host, build_portal_access_url, normalize_host_without_scheme, resolve_fido2_server_domain, build_nip_io_host_from_ipv4, is_ipv4_address)

publish_api() {
  log "Publicando Discovery.Api"
  local release_id; release_id="$(date +%Y%m%d%H%M%S)-initial"
  local release_dir="$DISCOVERY_API_RELEASES/$release_id"

  sudo -u discovery-api mkdir -p "$release_dir"
  if [[ "${DISCOVERY_CLEAN_BUILD:-1}" == "1" ]]; then
    log "Limpando cache de build da API (obj/ bin/)"
    sudo -u discovery-api find "$DISCOVERY_API_SOURCE" -maxdepth 4 \( -name obj -o -name bin \) -type d -exec rm -rf {} + 2>/dev/null || true
  fi
  sudo -u discovery-api dotnet publish "$DISCOVERY_API_SOURCE/src/Discovery.Api/Discovery.Api.csproj" \
    -c Release -r "$DISCOVERY_DOTNET_RUNTIME" --self-contained false -o "$release_dir" /p:UseAppHost=true

  sudo -u discovery-api rm -f "$release_dir"/appsettings*.json || true
  sudo -u discovery-api ln -sfn "$release_dir" "$DISCOVERY_API_CURRENT"

  if ! sudo -u discovery-api test -x "$release_dir/Discovery.Api"; then
    fail "Binario Discovery.Api nao gerado"
  fi
}

apply_site_release_permissions() {
  local release_dir="$1"
  sudo find "$release_dir" -type d -exec chmod 755 {} +
  sudo find "$release_dir" -type f -exec chmod 644 {} +
}

publish_site() {
  log "Publicando portal web Discovery"
  local release_id; release_id="$(date +%Y%m%d%H%M%S)-initial"
  local release_dir="$DISCOVERY_SITE_RELEASES/$release_id"

  sudo -u discovery-api mkdir -p "$release_dir"
  if [[ "${DISCOVERY_CLEAN_BUILD:-1}" == "1" ]]; then
    log "Limpando cache de build do portal web (node_modules/.cache e dist/)"
    sudo -u discovery-api rm -rf "$DISCOVERY_SITE_SOURCE/node_modules/.cache" "$DISCOVERY_SITE_SOURCE/dist" 2>/dev/null || true
  fi
  sudo -u discovery-api -H npm --prefix "$DISCOVERY_SITE_SOURCE" ci
  sudo -u discovery-api -H env \
    VITE_API_URL="$DISCOVERY_SITE_API_URL" \
    VITE_REALTIME_PROVIDER="$DISCOVERY_SITE_REALTIME_PROVIDER" \
    VITE_NATS_ENABLED="$DISCOVERY_SITE_NATS_ENABLED" \
    VITE_NATS_URL="$DISCOVERY_SITE_NATS_URL" \
    VITE_AGENT_OFFLINE_FALLBACK_MS="${DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS:-60000}" \
    npm --prefix "$DISCOVERY_SITE_SOURCE" run build

  if ! sudo -u discovery-api test -f "$DISCOVERY_SITE_SOURCE/dist/index.html"; then
    fail "Build do portal web nao gerou dist/index.html"
  fi

  sudo -u discovery-api cp -a "$DISCOVERY_SITE_SOURCE/dist/." "$release_dir/"
  apply_site_release_permissions "$release_dir"
  sudo -u discovery-api ln -sfn "$release_dir" "$DISCOVERY_SITE_CURRENT"
}

# ── Environment file ───────────────────────────────────────────────────────

write_environment_file() {
  log "Escrevendo arquivo de ambiente da API"

  local public_host
  if [[ "$ACCESS_MODE" == "external" ]]; then public_host="$EXTERNAL_API_HOST"
  elif [[ "$ACCESS_MODE" == "hybrid" ]]; then public_host="$EXTERNAL_API_HOST"
  else public_host="$INTERNAL_API_HOST"; fi

  local fido2_server_domain; fido2_server_domain="$(resolve_fido2_server_domain)"
  local fido2_server_name="${DISCOVERY_FIDO2_SERVER_NAME:-Discovery RMM}"
  local nats_server_external_host
  nats_server_external_host="$(normalize_host_without_scheme "${NATS_SERVER_HOST_EXTERNAL:-$public_host}")"
  local nats_use_wss_external="${NATS_USE_WSS_EXTERNAL:-false}"

  local -a web_hosts=("localhost" "127.0.0.1")
  web_hosts+=("$fido2_server_domain")
  if [[ "$ACCESS_MODE" == "internal" || "$ACCESS_MODE" == "hybrid" ]]; then
    web_hosts+=("$(normalize_host_without_scheme "$INTERNAL_API_HOST")")
  fi
  if [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]]; then
    web_hosts+=("$(normalize_host_without_scheme "$EXTERNAL_API_HOST")")
  fi

  local -a allowed_origins=()
  local host_entry=""
  for host_entry in "${web_hosts[@]}"; do
    [[ -n "$host_entry" ]] || continue
    local base_origin; base_origin="$(build_https_origin_from_host "$host_entry")"
    [[ -n "$base_origin" ]] || continue
    if [[ ! " ${allowed_origins[*]} " =~ " ${base_origin} " ]]; then
      allowed_origins+=("$base_origin")
    fi
  done

  if [[ -n "${DISCOVERY_ADDITIONAL_ALLOWED_ORIGINS:-}" ]]; then
    local extra_origin=""
    IFS=',' read -r -a _extra_origins <<< "$DISCOVERY_ADDITIONAL_ALLOWED_ORIGINS"
    for extra_origin in "${_extra_origins[@]}"; do
      extra_origin="$(printf '%s' "$extra_origin" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
      [[ -n "$extra_origin" ]] || continue
      if [[ ! " ${allowed_origins[*]} " =~ " ${extra_origin} " ]]; then
        allowed_origins+=("$extra_origin")
      fi
    done
  fi

  local cors_lines="" fido2_origin_lines=""
  local idx=0
  for origin in "${allowed_origins[@]}"; do
    cors_lines+="Security__Cors__AllowedOrigins__${idx}=${origin}"$'\n'
    fido2_origin_lines+="Authentication__Fido2__Origins__${idx}=${origin}"$'\n'
    idx=$((idx + 1))
  done

  sudo tee /etc/discovery-api/discovery.env >/dev/null <<EOF
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:8080
OPENAPI__ENABLED=$( [[ "${OPENAPI_ENABLED:-0}" == "1" ]] && echo true || echo false )
OpenApi__Scalar__Enabled=$( [[ "${OPENAPI_SCALAR_ENABLED:-$OPENAPI_ENABLED}" == "1" ]] && echo true || echo false )
ConnectionStrings__DefaultConnection=Host=127.0.0.1;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
Nats__Url=nats://${NATS_AUTH_USER}:${NATS_AUTH_PASSWORD}@127.0.0.1:4222
Nats__AuthUser=${NATS_AUTH_USER}
Nats__AuthPassword=${NATS_AUTH_PASSWORD}
Nats__AccountSeed=${NATS_ACCOUNT_SEED:-}
Nats__XKeySeed=${NATS_XKEY_SEED:-}
Nats__AuthCallout__Enabled=$( [[ "$NATS_AUTH_CALLOUT_ENABLED" == "1" ]] && echo true || echo false )
Nats__AuthCallout__Subject=${NATS_AUTH_CALLOUT_SUBJECT}
Nats__ServerHostExternal=${nats_server_external_host}
Nats__ServerHostInternal=127.0.0.1
Nats__UseWssExternal=${nats_use_wss_external}
AgentPackage__PublicApiScheme=https
AgentPackage__PublicApiServer=${public_host}
AgentPackage__Profiles__linux__DiscoveryProjectPath=${DISCOVERY_AGENT_SRC}
AgentPackage__Profiles__linux__BinaryPath=${DISCOVERY_AGENT_SRC}/src/build/bin/discovery-agent.exe
AgentPackage__Profiles__linux__OutputName=discovery-agent.exe
Authentication__Jwt__Issuer=discovery
Authentication__Jwt__Audience=discovery
Authentication__Jwt__PrivateKeyPath=/etc/discovery-api/certs/jwt-private.pem
Authentication__Jwt__PublicKeyPath=/etc/discovery-api/certs/jwt-public.pem
Authentication__Fido2__ServerDomain=${fido2_server_domain}
Authentication__Fido2__ServerName=${fido2_server_name}
${cors_lines}${fido2_origin_lines}DISCOVERY_ADDITIONAL_ALLOWED_ORIGINS=${DISCOVERY_ADDITIONAL_ALLOWED_ORIGINS:-}
DISCOVERY_API_BASE=${DISCOVERY_API_BASE}
DISCOVERY_API_SOURCE=${DISCOVERY_API_SOURCE}
DISCOVERY_API_RELEASES=${DISCOVERY_API_RELEASES}
DISCOVERY_API_CURRENT=${DISCOVERY_API_CURRENT}
DISCOVERY_GIT_REPO=${DISCOVERY_GIT_REPO}
DISCOVERY_GIT_BRANCH=${DISCOVERY_GIT_BRANCH}
DISCOVERY_BOOTSTRAP_ADMIN_LOGIN=${DISCOVERY_BOOTSTRAP_ADMIN_LOGIN:-}
DISCOVERY_DOTNET_RUNTIME=${DISCOVERY_DOTNET_RUNTIME}
SELFUPDATE_ENABLED=${SELFUPDATE_ENABLED:-1}
SELFUPDATE_INTERVAL=${SELFUPDATE_INTERVAL:-24h}
DISCOVERY_CLEAN_BUILD=${DISCOVERY_CLEAN_BUILD:-1}
DISCOVERY_SITE_GIT_REPO=${DISCOVERY_SITE_GIT_REPO}
DISCOVERY_SITE_BASE=${DISCOVERY_SITE_BASE}
DISCOVERY_SITE_SOURCE=${DISCOVERY_SITE_SOURCE}
DISCOVERY_SITE_RELEASES=${DISCOVERY_SITE_RELEASES}
DISCOVERY_SITE_CURRENT=${DISCOVERY_SITE_CURRENT}
DISCOVERY_SITE_API_URL=${DISCOVERY_SITE_API_URL}
DISCOVERY_SITE_REALTIME_PROVIDER=${DISCOVERY_SITE_REALTIME_PROVIDER}
DISCOVERY_SITE_NATS_ENABLED=${DISCOVERY_SITE_NATS_ENABLED}
DISCOVERY_SITE_NATS_URL=${DISCOVERY_SITE_NATS_URL}
DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS=${DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS:-60000}
NATS_WS_PORT=${NATS_WS_PORT:-8081}
NATS_WS_HOST=${NATS_WS_HOST:-127.0.0.1}
NATS_WS_TLS_ENABLED=${NATS_WS_TLS_ENABLED:-false}
TLS_CERT_PROVIDER=${TLS_CERT_PROVIDER:-self-signed}
ZEROSSL_CERT_DOMAIN=${ZEROSSL_CERT_DOMAIN:-}
ZEROSSL_CERT_ALT_DOMAINS=${ZEROSSL_CERT_ALT_DOMAINS:-}
ZEROSSL_ACME_EMAIL=${ZEROSSL_ACME_EMAIL:-}
ZEROSSL_ACME_EAB_KID=${ZEROSSL_ACME_EAB_KID:-}
ZEROSSL_ACME_EAB_HMAC_KEY=${ZEROSSL_ACME_EAB_HMAC_KEY:-}
ZEROSSL_DNS_RESOLVERS=${ZEROSSL_DNS_RESOLVERS:-1.1.1.1,8.8.8.8}
ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS=${ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS:-600}
ZEROSSL_DNS_POLL_INTERVAL_SECONDS=${ZEROSSL_DNS_POLL_INTERVAL_SECONDS:-15}
ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY=${ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY:-30}
ZEROSSL_AUTO_RENEW_ENABLED=${ZEROSSL_AUTO_RENEW_ENABLED:-1}
ZEROSSL_DNS_AUTOMATION_HOOK=${ZEROSSL_DNS_AUTOMATION_HOOK:-}
EOF

  sudo chmod 640 /etc/discovery-api/discovery.env
  sudo chown root:discovery-api /etc/discovery-api/discovery.env
}

# ── Self-update script and timer ───────────────────────────────────────────

install_selfupdate_script() {
  local target_script="$DISCOVERY_OPS_DIR/selfupdate-discovery-api.sh"
  log "Escrevendo script de self-update em $target_script"
  [[ -f "$SELFUPDATE_TEMPLATE_PATH" ]] || fail "Template de self-update nao encontrado: $SELFUPDATE_TEMPLATE_PATH"
  sudo install -m 750 -o discovery-api -g discovery-api "$SELFUPDATE_TEMPLATE_PATH" "$target_script"
  sudo chmod 750 "$target_script"
  sudo chown discovery-api:discovery-api "$target_script"

  if [[ "${SELFUPDATE_ENABLED:-1}" != "1" ]]; then
    log "Self-update automatico desativado; removendo timer se existir"
    sudo systemctl disable --now discovery-selfupdate.timer >/dev/null 2>&1 || true
    return
  fi

  local interval="${SELFUPDATE_INTERVAL:-24h}"
  log "Configurando timer de self-update (intervalo: ${interval})"

  sudo tee /etc/systemd/system/discovery-selfupdate.service >/dev/null <<EOF
[Unit]
Description=Discovery RMM Self-Update
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
User=discovery-api
Group=discovery-api
EnvironmentFile=/etc/discovery-api/discovery.env
ExecStart=${target_script}
StandardOutput=journal
StandardError=journal
EOF

  sudo tee /etc/systemd/system/discovery-selfupdate.timer >/dev/null <<EOF
[Unit]
Description=Discovery RMM Self-Update Timer

[Timer]
OnBootSec=5min
OnUnitInactiveSec=${interval}
Unit=discovery-selfupdate.service

[Install]
WantedBy=timers.target
EOF

  sudo systemctl daemon-reload
  sudo systemctl enable --now discovery-selfupdate.timer
  log "Timer discovery-selfupdate ativo (intervalo: ${interval})"
}

# ── Systemd service ────────────────────────────────────────────────────────

write_systemd_service() {
  log "Criando servico systemd discovery-api"
  sudo tee /etc/systemd/system/discovery-api.service >/dev/null <<EOF
[Unit]
Description=Discovery API
After=network-online.target postgresql.service nats-server.service
Wants=network-online.target

[Service]
Type=simple
User=discovery-api
Group=discovery-api
WorkingDirectory=${DISCOVERY_API_CURRENT}
EnvironmentFile=/etc/discovery-api/discovery.env
ExecStart=${DISCOVERY_API_CURRENT}/Discovery.Api
Restart=always
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF

  sudo systemctl daemon-reload
  sudo systemctl enable discovery-api
  sudo systemctl restart discovery-api
}

# ── Nginx site proxy ──────────────────────────────────────────────────────

write_site_proxy_config() {
  log "Configurando Nginx para portal web + proxy da API"
  local access_mode="${ACCESS_MODE:-internal}"
  local fido2_server_domain; fido2_server_domain="$(resolve_fido2_server_domain)"

  local -a server_names=("localhost" "127.0.0.1")
  [[ -n "$fido2_server_domain" ]] && server_names+=("$fido2_server_domain")
  if [[ "$access_mode" == "internal" || "$access_mode" == "hybrid" ]] && [[ -n "${INTERNAL_API_HOST:-}" ]]; then
    server_names+=("$INTERNAL_API_HOST")
  fi
  if [[ "$access_mode" == "external" || "$access_mode" == "hybrid" ]] && [[ -n "${EXTERNAL_API_HOST:-}" ]]; then
    server_names+=("$EXTERNAL_API_HOST")
  fi

  local server_name_list; server_name_list="$(printf '%s ' "${server_names[@]}")"
  server_name_list="${server_name_list% }"

  local redirect_rules=""
  if [[ "$access_mode" == "internal" || "$access_mode" == "hybrid" ]]; then
    local normalized_internal_host
    normalized_internal_host="$(normalize_host_without_scheme "${INTERNAL_API_HOST:-}")"
    if [[ -n "$normalized_internal_host" && "$normalized_internal_host" != "$fido2_server_domain" ]]; then
      redirect_rules+="  if (\$host = ${normalized_internal_host}) {"$'\n'
      redirect_rules+="    return 308 https://${fido2_server_domain}\$request_uri;"$'\n'
      redirect_rules+="  }"$'\n'
    fi
  fi

  [[ -f "$NGINX_TEMPLATE_PATH" ]] || fail "Template Nginx nao encontrado: $NGINX_TEMPLATE_PATH"

  local rendered_conf; rendered_conf="$(mktemp)"
  awk \
    -v server_name_list="$server_name_list" \
    -v discovery_site_current="$DISCOVERY_SITE_CURRENT" \
    -v nats_ws_port="${NATS_WS_PORT:-8081}" \
    -v redirect_rules="$redirect_rules" \
    '{
      gsub("__SERVER_NAME_LIST__", server_name_list)
      gsub("__DISCOVERY_SITE_CURRENT__", discovery_site_current)
      gsub("__NATS_WS_PORT__", nats_ws_port)
      if ($0 ~ /__REDIRECT_RULES__/) {
        if (length(redirect_rules) > 0) { printf "%s", redirect_rules }
        next
      }
      print
    }' "$NGINX_TEMPLATE_PATH" > "$rendered_conf"

  sudo install -m 644 -o root -g root "$rendered_conf" /etc/nginx/sites-available/discovery-rmm
  rm -f "$rendered_conf"

  sudo rm -f /etc/nginx/sites-enabled/default
  sudo ln -sfn /etc/nginx/sites-available/discovery-rmm /etc/nginx/sites-enabled/discovery-rmm
  sudo nginx -t
  sudo systemctl enable nginx
  sudo systemctl restart nginx
}

# ── Admin recovery ─────────────────────────────────────────────────────────

wait_for_discovery_api_ready() {
  local max_attempts="${1:-60}"
  local sleep_seconds="${2:-2}"
  local attempt
  local health_url="http://127.0.0.1:8080/health"

  log "Aguardando discovery-api concluir o startup em ${health_url}"

  for ((attempt = 1; attempt <= max_attempts; attempt++)); do
    if sudo systemctl is-active --quiet discovery-api && curl -fsS "$health_url" >/dev/null 2>&1; then
      log "discovery-api respondeu com sucesso em /health"
      return 0
    fi
    if sudo systemctl is-failed --quiet discovery-api; then break; fi
    sleep "$sleep_seconds"
  done

  sudo systemctl status discovery-api --no-pager || true
  sudo journalctl -u discovery-api -n 50 --no-pager || true
  fail "discovery-api nao ficou pronta a tempo; bootstrap administrativo abortado."
}

persist_bootstrap_admin_login_in_env() {
  local bootstrap_login="$1"
  local env_file="${2:-/etc/discovery-api/discovery.env}"

  [[ -n "$bootstrap_login" ]] || return 0
  bootstrap_login="${bootstrap_login//$'\r'/}"
  bootstrap_login="${bootstrap_login//$'\n'/}"

  if ! sudo test -f "$env_file"; then
    warn "Arquivo de ambiente da API nao encontrado para persistir login bootstrap: $env_file"
    return 0
  fi

  local tmp_file
  tmp_file="$(mktemp)"
  sudo awk '!/^DISCOVERY_BOOTSTRAP_ADMIN_LOGIN=/' "$env_file" > "$tmp_file"
  printf 'DISCOVERY_BOOTSTRAP_ADMIN_LOGIN=%s\n' "$bootstrap_login" >> "$tmp_file"
  sudo install -m 640 -o root -g discovery-api "$tmp_file" "$env_file"
  rm -f "$tmp_file"
}

run_db_migrations() {
  log "Aguardando startup da API para executar o bootstrap administrativo"

  local api_env_file="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"
  local api_current="${DISCOVERY_API_CURRENT:-/opt/discovery-api/current}"

  if ! sudo -u discovery-api test -x "$api_current/Discovery.Api"; then
    fail "Binario da API nao encontrado em $api_current/Discovery.Api. Assegure-se de que publish_api foi executado com sucesso antes."
  fi

  wait_for_discovery_api_ready
  prepare_bootstrap_admin_login "final"

  log "API pronta; executando bootstrap admin via --recover-admin..."
  if ! sudo -u discovery-api test -r "$api_env_file"; then
    fail "Arquivo de ambiente da API nao encontrado ou sem leitura para discovery-api: $api_env_file"
  fi

  local output
  local bootstrap_login="${DISCOVERY_BOOTSTRAP_ADMIN_LOGIN:-${ADMIN_RECOVERY_LOGIN:-}}"
  [[ -n "$bootstrap_login" ]] || fail "Login do primeiro acesso nao foi preparado antes do bootstrap admin."
  persist_bootstrap_admin_login_in_env "$bootstrap_login" "$api_env_file"

  output="$(sudo -u discovery-api env \
    DISCOVERY_API_MAINTENANCE_ENV_FILE="$api_env_file" \
    DISCOVERY_API_MAINTENANCE_CURRENT="$api_current" \
    DISCOVERY_API_BOOTSTRAP_LOGIN="$bootstrap_login" \
    DISCOVERY_API_BOOTSTRAP_PASSWORD="${DISCOVERY_BOOTSTRAP_ADMIN_PASSWORD:-}" \
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
      if [[ -n "${DISCOVERY_API_BOOTSTRAP_PASSWORD:-}" ]]; then
        printf "%s\n" "$DISCOVERY_API_BOOTSTRAP_PASSWORD" | "${recover_args[@]}" --password-stdin
      else
        "${recover_args[@]}"
      fi
    ' 2>&1)" || {
    local exit_code=$?
    fail "recover-admin falhou com codigo ${exit_code}:\n${output}"
  }

  log "Admin bootstrap concluido. Credenciais geradas pelo recover-admin."

  local filtered_output
  filtered_output="$(printf '%s\n' "$output" | sed -n '/^Admin recovery completed\./,$p')"
  if [[ -z "$filtered_output" ]]; then filtered_output="$output"; fi
  ADMIN_RECOVERY_OUTPUT="$filtered_output"

  ADMIN_RECOVERY_LOGIN="$(printf '%s\n' "$output" | sed -nE 's/^[[:space:]]*(Login|Usuario|User(name)?)[[:space:]]*:[[:space:]]*(.+)$/\3/p' | head -n 1)"
  if [[ -z "${ADMIN_RECOVERY_LOGIN:-}" ]]; then ADMIN_RECOVERY_LOGIN="$bootstrap_login"; fi
}

# ── NATS env update helpers ────────────────────────────────────────────────

update_nats_environment_file() {
  local env_file="/etc/discovery-api/discovery.env"
  if ! sudo test -f "$env_file"; then
    log "Arquivo $env_file nao encontrado. Pulando atualizacao de variaveis NATS da API."; return
  fi

  log "Atualizando variaveis NATS no /etc/discovery-api/discovery.env"
  local tmp_file; tmp_file="$(mktemp)"

  sudo awk '
    !/^Nats__Url=/ &&
    !/^Nats__AuthUser=/ &&
    !/^Nats__AuthPassword=/ &&
    !/^Nats__ServerHostExternal=/ &&
    !/^Nats__ServerHostInternal=/ &&
    !/^Nats__UseWssExternal=/ &&
    !/^Nats__AuthCallout__Enabled=/ &&
    !/^Nats__AuthCallout__Subject=/ &&
    !/^Nats__AccountSeed=/ &&
    !/^Nats__XKeySeed=/ &&
    !/^NATS_WS_PORT=/ &&
    !/^NATS_WS_HOST=/ &&
    !/^NATS_WS_TLS_ENABLED=/
  ' "$env_file" > "$tmp_file"

  local nats_server_external_host
  nats_server_external_host="$(normalize_host_without_scheme "${NATS_SERVER_HOST_EXTERNAL:-${EXTERNAL_API_HOST:-${INTERNAL_API_HOST:-}}}")"

  cat >> "$tmp_file" <<EOF
Nats__Url=nats://${NATS_AUTH_USER}:${NATS_AUTH_PASSWORD}@127.0.0.1:4222
Nats__AuthUser=${NATS_AUTH_USER}
Nats__AuthPassword=${NATS_AUTH_PASSWORD}
Nats__ServerHostExternal=${nats_server_external_host}
Nats__ServerHostInternal=${NATS_SERVER_HOST_INTERNAL:-127.0.0.1}
Nats__UseWssExternal=${NATS_USE_WSS_EXTERNAL:-false}
Nats__AuthCallout__Enabled=$( [[ "$NATS_AUTH_CALLOUT_ENABLED" == "1" ]] && echo true || echo false )
Nats__AuthCallout__Subject=${NATS_AUTH_CALLOUT_SUBJECT}
Nats__AccountSeed=${NATS_ACCOUNT_SEED:-}
Nats__XKeySeed=${NATS_XKEY_SEED:-}
NATS_WS_PORT=${NATS_WS_PORT:-8081}
NATS_WS_HOST=${NATS_WS_HOST:-127.0.0.1}
NATS_WS_TLS_ENABLED=${NATS_WS_TLS_ENABLED:-false}
EOF

  sudo install -m 640 -o root -g discovery-api "$tmp_file" "$env_file"
  rm -f "$tmp_file"
}

update_site_realtime_environment_file() {
  local env_file="/etc/discovery-api/discovery.env"
  sudo test -f "$env_file" || return 0

  local tmp_file; tmp_file="$(mktemp)"
  sudo awk '
    !/^DISCOVERY_SITE_REALTIME_PROVIDER=/ &&
    !/^DISCOVERY_SITE_NATS_ENABLED=/ &&
    !/^DISCOVERY_SITE_NATS_URL=/ &&
    !/^DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS=/ &&
    !/^NATS_WS_PORT=/ &&
    !/^NATS_WS_HOST=/ &&
    !/^NATS_WS_TLS_ENABLED=/
  ' "$env_file" > "$tmp_file"

  cat >> "$tmp_file" <<EOF
DISCOVERY_SITE_REALTIME_PROVIDER=${DISCOVERY_SITE_REALTIME_PROVIDER}
DISCOVERY_SITE_NATS_ENABLED=${DISCOVERY_SITE_NATS_ENABLED}
DISCOVERY_SITE_NATS_URL=${DISCOVERY_SITE_NATS_URL}
DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS=${DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS:-60000}
NATS_WS_PORT=${NATS_WS_PORT:-8081}
NATS_WS_HOST=${NATS_WS_HOST:-127.0.0.1}
NATS_WS_TLS_ENABLED=${NATS_WS_TLS_ENABLED:-false}
EOF

  sudo install -m 640 -o root -g discovery-api "$tmp_file" "$env_file"
  rm -f "$tmp_file"
}

# ── Summary ────────────────────────────────────────────────────────────────

show_summary() {
  log "Instalacao concluida"
  local internal_portal_host="" internal_portal_url="" primary_portal_host="" primary_portal_url=""

  internal_portal_host="$(resolve_internal_portal_domain)"
  internal_portal_url="$(build_portal_access_url "$internal_portal_host")"
  primary_portal_host="$(resolve_fido2_server_domain)"
  primary_portal_url="$(build_portal_access_url "$primary_portal_host")"

  echo
  echo "Resumo:"
  echo "- API base: $DISCOVERY_API_BASE"
  echo "- API current: $DISCOVERY_API_CURRENT"
  echo "- Site base: $DISCOVERY_SITE_BASE"
  echo "- Site current: $DISCOVERY_SITE_CURRENT"
  echo "- Agent source: $DISCOVERY_AGENT_SRC"
  echo "- Agent artifacts: $DISCOVERY_AGENT_ARTIFACTS"
  if [[ -n "${ADMIN_RECOVERY_LOGIN:-}" ]]; then echo "- Usuario administrador: $ADMIN_RECOVERY_LOGIN"
  elif [[ -n "${ADMIN_RECOVERY_OUTPUT:-}" ]]; then echo "- Usuario administrador: admin"
  else echo "- Usuario administrador: (nao gerado nesta execucao)"; fi
  echo "- Access mode: $ACCESS_MODE"
  if [[ "$ACCESS_MODE" == "internal" || "$ACCESS_MODE" == "hybrid" ]]; then
    echo "- Host interno: $INTERNAL_API_HOST"
    if [[ -n "$internal_portal_url" ]]; then echo "- Portal interno: $internal_portal_url"
    else echo "- Portal interno: https://$INTERNAL_API_HOST/"; fi
  fi
  if [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]]; then
    echo "- Host externo: $EXTERNAL_API_HOST"
    echo "- Portal externo: https://$EXTERNAL_API_HOST/"
  fi
  if [[ -n "$primary_portal_host" ]]; then echo "- Hostname MFA/portal: $primary_portal_host"; fi
  echo "- TLS: ${TLS_CERT_PROVIDER:-self-signed}"
  echo

  print_selected_configuration_summary
  if [[ -n "${ADMIN_RECOVERY_LOGIN:-}" ]]; then echo "- Usuario administrador: $ADMIN_RECOVERY_LOGIN"
  elif [[ -n "${ADMIN_RECOVERY_OUTPUT:-}" ]]; then echo "- Usuario administrador: admin"
  else echo "- Usuario administrador: (nao gerado nesta execucao)"; fi
  echo

  if [[ -n "${ADMIN_RECOVERY_OUTPUT:-}" ]]; then
    echo "========================================"
    echo " CREDENCIAIS DE PRIMEIRO ACESSO"
    echo "----------------------------------------"
    echo "$ADMIN_RECOVERY_OUTPUT"
    echo "----------------------------------------"
    echo "Guarde a senha temporaria com seguranca."
    echo "Obrigatorio alterar login + senha no primeiro acesso."
    echo "========================================"
    echo
  fi

  echo "Verificacoes recomendadas:"
  echo "1) sudo systemctl status discovery-api --no-pager"
  echo "2) sudo systemctl status nginx --no-pager"
  echo "3) sudo systemctl status nats-server --no-pager"
  echo "4) sudo systemctl status postgresql --no-pager"
  echo "5) curl -k https://127.0.0.1/"
  if [[ "${OPENAPI_ENABLED:-0}" == "1" ]]; then echo "6) curl -k https://127.0.0.1/openapi/v1.json"
  else echo "6) sudo ss -ltnp '( sport = :8080 )'"; fi
}
