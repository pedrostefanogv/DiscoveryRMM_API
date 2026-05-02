# Discovery RMM installer – TLS certificates and JWT keys
# Requires: common.sh (log, warn, fail, build_certificate_san_entry, resolve_fido2_server_domain, normalize_host_without_scheme)

setup_jwt_signing_keys() {
  local private_key_path="/etc/discovery-api/certs/jwt-private.pem"
  local public_key_path="/etc/discovery-api/certs/jwt-public.pem"

  if sudo test -f "$private_key_path" && sudo test -f "$public_key_path"; then
    log "Par de chaves JWT ja existe; mantendo arquivos atuais"
    return
  fi

  log "Gerando par de chaves JWT persistentes (RS256)"
  local private_tmp; private_tmp="$(mktemp)"
  local public_tmp;  public_tmp="$(mktemp)"

  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$private_tmp"
  openssl rsa -in "$private_tmp" -pubout -out "$public_tmp"

  sudo install -m 640 -o root -g discovery-api "$private_tmp" "$private_key_path"
  sudo install -m 644 -o root -g discovery-api "$public_tmp" "$public_key_path"
  rm -f "$private_tmp" "$public_tmp"
}

setup_self_signed_proxy_certificate() {
  log "Gerando certificado self-signed para o proxy web local"

  local fido2_server_domain; fido2_server_domain="$(resolve_fido2_server_domain)"
  local primary_host="$fido2_server_domain"
  local -a san_entries=("DNS:localhost" "IP:127.0.0.1")
  local san_entry=""

  san_entry="$(build_certificate_san_entry "$fido2_server_domain")"
  [[ -n "$san_entry" ]] && san_entries+=("$san_entry")

  if [[ "$ACCESS_MODE" == "internal" || "$ACCESS_MODE" == "hybrid" ]]; then
    san_entry="$(build_certificate_san_entry "$INTERNAL_API_HOST")"
    [[ -n "$san_entry" ]] && san_entries+=("$san_entry")
  fi

  if [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]]; then
    san_entry="$(build_certificate_san_entry "$EXTERNAL_API_HOST")"
    [[ -n "$san_entry" ]] && san_entries+=("$san_entry")
  fi

  local san_list; san_list="$(IFS=, ; printf '%s' "${san_entries[*]}")"

  local cert_conf; cert_conf="$(mktemp)"
  cat > "$cert_conf" <<EOF
[req]
default_bits = 4096
prompt = no
default_md = sha256
distinguished_name = dn
x509_extensions = v3_req

[dn]
CN = $primary_host
O = Discovery

[v3_req]
subjectAltName = $san_list
extendedKeyUsage = serverAuth
keyUsage = digitalSignature, keyEncipherment
EOF

  sudo rm -f /etc/discovery-api/certs/api-internal.key /etc/discovery-api/certs/api-internal.crt
  sudo openssl req -x509 -nodes -days 825 \
    -newkey rsa:4096 \
    -keyout /etc/discovery-api/certs/api-internal.key \
    -out /etc/discovery-api/certs/api-internal.crt \
    -config "$cert_conf"
  rm -f "$cert_conf"

  sudo chmod 640 /etc/discovery-api/certs/api-internal.key
  sudo chmod 644 /etc/discovery-api/certs/api-internal.crt
  sudo chown root:discovery-api /etc/discovery-api/certs/api-internal.key
}

# ── ZeroSSL ACME ───────────────────────────────────────────────────────────

install_zerossl_acme_certificate_script() {
  [[ -f "$ZEROSSL_ACME_TEMPLATE_PATH" ]] || fail "Template ZeroSSL ACME nao encontrado: $ZEROSSL_ACME_TEMPLATE_PATH"
  sudo install -m 750 -o root -g discovery-api "$ZEROSSL_ACME_TEMPLATE_PATH" "$DISCOVERY_OPS_DIR/zerossl-acme-certificate.sh"
}

setup_zerossl_acme_certificate() {
  log "Emitindo certificado ZeroSSL via ACME"
  install_zerossl_acme_certificate_script

  sudo env \
    TLS_CERT_PROVIDER="$TLS_CERT_PROVIDER" \
    ZEROSSL_CERT_DOMAIN="$ZEROSSL_CERT_DOMAIN" \
    ZEROSSL_CERT_ALT_DOMAINS="${ZEROSSL_CERT_ALT_DOMAINS:-}" \
    ZEROSSL_ACME_EMAIL="$ZEROSSL_ACME_EMAIL" \
    ZEROSSL_ACME_EAB_KID="$ZEROSSL_ACME_EAB_KID" \
    ZEROSSL_ACME_EAB_HMAC_KEY="$ZEROSSL_ACME_EAB_HMAC_KEY" \
    ZEROSSL_DNS_RESOLVERS="${ZEROSSL_DNS_RESOLVERS:-1.1.1.1,8.8.8.8}" \
    ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS="${ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS:-600}" \
    ZEROSSL_DNS_POLL_INTERVAL_SECONDS="${ZEROSSL_DNS_POLL_INTERVAL_SECONDS:-15}" \
    ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY="${ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY:-30}" \
    ZEROSSL_DNS_AUTOMATION_HOOK="${ZEROSSL_DNS_AUTOMATION_HOOK:-}" \
    "$DISCOVERY_OPS_DIR/zerossl-acme-certificate.sh" issue
}

setup_proxy_certificate() {
  normalize_tls_certificate_provider
  if [[ "$TLS_CERT_PROVIDER" == "zerossl-acme" ]]; then
    setup_zerossl_acme_certificate
    return
  fi
  setup_self_signed_proxy_certificate
}

setup_zerossl_renewal_timer() {
  [[ "${TLS_CERT_PROVIDER:-self-signed}" == "zerossl-acme" ]] || return 0
  normalize_zerossl_auto_renew_enabled
  if [[ "${ZEROSSL_AUTO_RENEW_ENABLED:-1}" != "1" ]]; then
    log "Timer de renovacao ZeroSSL desativado por configuracao."
    sudo systemctl disable --now discovery-zerossl-renew.timer >/dev/null 2>&1 || true
    return 0
  fi

  install_zerossl_acme_certificate_script

  sudo tee /etc/systemd/system/discovery-zerossl-renew.service >/dev/null <<EOF
[Unit]
Description=Discovery RMM ZeroSSL certificate renewal
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
EnvironmentFile=/etc/discovery-api/discovery.env
ExecStart=${DISCOVERY_OPS_DIR}/zerossl-acme-certificate.sh renew
EOF

  sudo tee /etc/systemd/system/discovery-zerossl-renew.timer >/dev/null <<EOF
[Unit]
Description=Discovery RMM ZeroSSL certificate renewal timer

[Timer]
OnCalendar=*-*-* 03:20:00
RandomizedDelaySec=1h
Persistent=true

[Install]
WantedBy=timers.target
EOF

  sudo systemctl daemon-reload
  sudo systemctl enable --now discovery-zerossl-renew.timer
}

# ── TLS defaults from existing install ─────────────────────────────────────

load_existing_tls_defaults() {
  local env_file="/etc/discovery-api/discovery.env"
  sudo test -f "$env_file" || return 0

  TLS_CERT_PROVIDER="${TLS_CERT_PROVIDER:-$(sudo awk -F= '/^TLS_CERT_PROVIDER=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  ZEROSSL_CERT_DOMAIN="${ZEROSSL_CERT_DOMAIN:-$(sudo awk -F= '/^ZEROSSL_CERT_DOMAIN=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  ZEROSSL_CERT_ALT_DOMAINS="${ZEROSSL_CERT_ALT_DOMAINS:-$(sudo awk -F= '/^ZEROSSL_CERT_ALT_DOMAINS=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  ZEROSSL_ACME_EMAIL="${ZEROSSL_ACME_EMAIL:-$(sudo awk -F= '/^ZEROSSL_ACME_EMAIL=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  ZEROSSL_ACME_EAB_KID="${ZEROSSL_ACME_EAB_KID:-$(sudo awk -F= '/^ZEROSSL_ACME_EAB_KID=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  ZEROSSL_ACME_EAB_HMAC_KEY="${ZEROSSL_ACME_EAB_HMAC_KEY:-$(sudo awk -F= '/^ZEROSSL_ACME_EAB_HMAC_KEY=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  ZEROSSL_DNS_RESOLVERS="${ZEROSSL_DNS_RESOLVERS:-$(sudo awk -F= '/^ZEROSSL_DNS_RESOLVERS=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS="${ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS:-$(sudo awk -F= '/^ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  ZEROSSL_DNS_POLL_INTERVAL_SECONDS="${ZEROSSL_DNS_POLL_INTERVAL_SECONDS:-$(sudo awk -F= '/^ZEROSSL_DNS_POLL_INTERVAL_SECONDS=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY="${ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY:-$(sudo awk -F= '/^ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  ZEROSSL_AUTO_RENEW_ENABLED="${ZEROSSL_AUTO_RENEW_ENABLED:-$(sudo awk -F= '/^ZEROSSL_AUTO_RENEW_ENABLED=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  ZEROSSL_DNS_AUTOMATION_HOOK="${ZEROSSL_DNS_AUTOMATION_HOOK:-$(sudo awk -F= '/^ZEROSSL_DNS_AUTOMATION_HOOK=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
}

setup_cloudflare_tunnel() {
  [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]] || return 0

  log "Instalando e configurando cloudflared"

  if ! command -v cloudflared >/dev/null 2>&1; then
    if ! curl -fsSL https://pkg.cloudflare.com/cloudflare-main.gpg | sudo gpg --yes --dearmor -o /usr/share/keyrings/cloudflare-main.gpg; then
      warn "Nao foi possivel configurar repositorio do cloudflared; seguindo sem tunnel automatico."; return
    fi
    echo "deb [signed-by=/usr/share/keyrings/cloudflare-main.gpg] https://pkg.cloudflare.com/cloudflared $(lsb_release -cs) main" | sudo tee /etc/apt/sources.list.d/cloudflared.list >/dev/null
    sudo apt-get update -y
    if ! sudo apt-get install -y cloudflared; then
      warn "cloudflared indisponivel para esta arquitetura/distribuicao; seguindo sem tunnel automatico."; return
    fi
  fi

  if ! command -v cloudflared >/dev/null 2>&1; then
    warn "cloudflared nao encontrado apos tentativa de instalacao; seguindo sem tunnel automatico."; return
  fi

  if ! sudo cloudflared service install "$CLOUDFLARE_TUNNEL_TOKEN"; then
    warn "Falha ao configurar cloudflared service install; siga com configuracao manual do tunnel."; return
  fi

  sudo systemctl enable cloudflared || warn "Nao foi possivel habilitar servico cloudflared automaticamente."
  sudo systemctl restart cloudflared || warn "Nao foi possivel reiniciar cloudflared automaticamente."
}
