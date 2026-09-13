# Discovery RMM installer – TLS certificates and JWT keys
# Requires: common.sh (log, warn, fail, build_certificate_san_entry, resolve_fido2_server_domain, normalize_host_without_scheme)

setup_jwt_signing_keys() {
  local private_key_path="/etc/discovery-api/certs/jwt-private.pem"
  local public_key_path="/etc/discovery-api/certs/jwt-public.pem"

  local has_private=0 has_public=0
  sudo test -f "$private_key_path" && has_private=1
  sudo test -f "$public_key_path" && has_public=1

  if [[ "$has_private" -eq 1 && "$has_public" -eq 1 ]]; then
    log "Par de chaves JWT ja existe; mantendo arquivos atuais"
    return
  fi

  # So a publica sumiu: deriva da privada (regenerar o par invalidaria TODAS
  # as sessoes/tokens emitidos com a chave atual).
  if [[ "$has_private" -eq 1 && "$has_public" -eq 0 ]]; then
    log "Chave publica JWT ausente; derivando da chave privada existente (sessoes preservadas)."
    local public_tmp; public_tmp="$(mktemp)"
    sudo openssl rsa -in "$private_key_path" -pubout -out "$public_tmp"
    sudo install -m 644 -o root -g discovery-api "$public_tmp" "$public_key_path"
    rm -f "$public_tmp"
    return
  fi

  log "Gerando par de chaves JWT persistentes (RS256, 3072-bit)"
  local private_tmp; private_tmp="$(mktemp)"
  local public_tmp;  public_tmp="$(mktemp)"

  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$private_tmp"
  openssl rsa -in "$private_tmp" -pubout -out "$public_tmp"

  sudo install -m 640 -o root -g discovery-api "$private_tmp" "$private_key_path"
  sudo install -m 644 -o root -g discovery-api "$public_tmp" "$public_key_path"
  rm -f "$private_tmp" "$public_tmp"
}

setup_self_signed_proxy_certificate() {
  local cert_path="/etc/discovery-api/certs/api-internal.crt"
  local key_path="/etc/discovery-api/certs/api-internal.key"

  # Verifica se o certificado ja existe e e valido (nao expirado) e cobre os SANs atuais
  if sudo test -f "$cert_path" && sudo test -f "$key_path"; then
    local cert_ok=1
    sudo openssl x509 -in "$cert_path" -noout -checkend 0 >/dev/null 2>&1 || cert_ok=0
    # O comentario original prometia validar os SANs: verifica o host primario
    # atual — se o host mudou, o cert errado ficaria ativo ate expirar.
    if [[ "$cert_ok" -eq 1 ]]; then
      local fido2_domain_now
      fido2_domain_now="$(resolve_fido2_server_domain)"
      if [[ -n "$fido2_domain_now" ]] \
         && ! sudo openssl x509 -in "$cert_path" -noout -checkhost "$fido2_domain_now" >/dev/null 2>&1; then
        cert_ok=0
      fi
    fi

    if [[ "$cert_ok" -eq 1 ]]; then
      log "Certificado self-signed existente valido e cobrindo o host atual; mantendo atual."
      sudo chmod 640 "$key_path"
      sudo chmod 644 "$cert_path"
      sudo chown root:discovery-api "$key_path" 2>/dev/null || true
      return
    fi
    log "Certificado self-signed existente expirado ou nao cobre o host atual; regenerando."
  fi

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
default_bits = 2048
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

  # Geracao em arquivos temporarios + install atomico: uma falha do openssl no
  # meio nao pode deixar o nginx sem chave.
  local key_tmp; key_tmp="$(mktemp)"
  local cert_tmp; cert_tmp="$(mktemp)"
  openssl req -x509 -nodes -days 825 \
    -newkey rsa:2048 \
    -keyout "$key_tmp" \
    -out "$cert_tmp" \
    -config "$cert_conf"
  rm -f "$cert_conf"

  sudo install -m 640 -o root -g discovery-api "$key_tmp" "$key_path"
  sudo install -m 644 -o root -g discovery-api "$cert_tmp" "$cert_path"
  rm -f "$key_tmp" "$cert_tmp"
}

# ── ZeroSSL ACME ───────────────────────────────────────────────────────────

install_zerossl_acme_certificate_script() {
  [[ -f "$ZEROSSL_ACME_TEMPLATE_PATH" ]] || fail "Template ZeroSSL ACME nao encontrado: $ZEROSSL_ACME_TEMPLATE_PATH"
  sudo install -m 750 -o root -g discovery-api "$ZEROSSL_ACME_TEMPLATE_PATH" "$DISCOVERY_OPS_DIR/zerossl-acme-certificate.sh"
}

setup_zerossl_acme_certificate() {
  log "Emitindo certificado ZeroSSL via ACME"
  install_zerossl_acme_certificate_script

  # Segredos (EAB HMAC etc.) NAO vao em argv (`sudo env KEY=...` fica visivel
  # em /proc/*/cmdline). O template carrega os valores de
  # /etc/discovery-api/discovery.env via load_env_file — o instalador garante
  # que o env esteja atualizado ANTES de chamar esta funcao.
  sudo "$DISCOVERY_OPS_DIR/zerossl-acme-certificate.sh" issue
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
# Prefixo '-': a ausencia do arquivo nao impede o timer de iniciar (os valores
# tambem sao carregados pelo proprio template via load_env_file).
EnvironmentFile=-/etc/discovery-api/discovery.env
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

# ── Let's Encrypt ACME ─────────────────────────────────────────────────────

install_letsencrypt_acme_certificate_script() {
  [[ -f "$LETSENCRYPT_ACME_TEMPLATE_PATH" ]] || fail "Template Let's Encrypt ACME nao encontrado: $LETSENCRYPT_ACME_TEMPLATE_PATH"
  sudo install -m 750 -o root -g discovery-api "$LETSENCRYPT_ACME_TEMPLATE_PATH" "$DISCOVERY_OPS_DIR/letsencrypt-acme-certificate.sh"
}

setup_letsencrypt_acme_certificate() {
  log "Emitindo certificado Let's Encrypt via ACME"
  install_letsencrypt_acme_certificate_script

  # Sem segredos em argv: o template le os valores do discovery.env (ver
  # setup_zerossl_acme_certificate).
  sudo "$DISCOVERY_OPS_DIR/letsencrypt-acme-certificate.sh" issue
}

setup_proxy_certificate() {
  normalize_tls_certificate_provider
  if [[ "$TLS_CERT_PROVIDER" == "zerossl-acme" ]]; then
    setup_zerossl_acme_certificate
    return
  fi
  if [[ "$TLS_CERT_PROVIDER" == "letsencrypt-acme" ]]; then
    setup_letsencrypt_acme_certificate
    return
  fi
  setup_self_signed_proxy_certificate
}

setup_letsencrypt_renewal_timer() {
  [[ "${TLS_CERT_PROVIDER:-self-signed}" == "letsencrypt-acme" ]] || return 0
  normalize_letsencrypt_auto_renew_enabled
  if [[ "${LETSENCRYPT_AUTO_RENEW_ENABLED:-1}" != "1" ]]; then
    log "Timer de renovacao Let's Encrypt desativado por configuracao."
    sudo systemctl disable --now discovery-letsencrypt-renew.timer >/dev/null 2>&1 || true
    return 0
  fi

  install_letsencrypt_acme_certificate_script

  sudo tee /etc/systemd/system/discovery-letsencrypt-renew.service >/dev/null <<EOF
[Unit]
Description=Discovery RMM Let's Encrypt certificate renewal
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
# Prefixo '-': a ausencia do arquivo nao impede o timer de iniciar (os valores
# tambem sao carregados pelo proprio template via load_env_file).
EnvironmentFile=-/etc/discovery-api/discovery.env
ExecStart=${DISCOVERY_OPS_DIR}/letsencrypt-acme-certificate.sh renew
EOF

  sudo tee /etc/systemd/system/discovery-letsencrypt-renew.timer >/dev/null <<EOF
[Unit]
Description=Discovery RMM Let's Encrypt certificate renewal timer

[Timer]
OnCalendar=*-*-* 03:20:00
RandomizedDelaySec=1h
Persistent=true

[Install]
WantedBy=timers.target
EOF

  sudo systemctl daemon-reload
  sudo systemctl enable --now discovery-letsencrypt-renew.timer
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
  LETSENCRYPT_CERT_DOMAIN="${LETSENCRYPT_CERT_DOMAIN:-$(sudo awk -F= '/^LETSENCRYPT_CERT_DOMAIN=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  LETSENCRYPT_CERT_ALT_DOMAINS="${LETSENCRYPT_CERT_ALT_DOMAINS:-$(sudo awk -F= '/^LETSENCRYPT_CERT_ALT_DOMAINS=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  LETSENCRYPT_ACME_EMAIL="${LETSENCRYPT_ACME_EMAIL:-$(sudo awk -F= '/^LETSENCRYPT_ACME_EMAIL=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  LETSENCRYPT_DNS_RESOLVERS="${LETSENCRYPT_DNS_RESOLVERS:-$(sudo awk -F= '/^LETSENCRYPT_DNS_RESOLVERS=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS="${LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS:-$(sudo awk -F= '/^LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS="${LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS:-$(sudo awk -F= '/^LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY="${LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY:-$(sudo awk -F= '/^LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  LETSENCRYPT_AUTO_RENEW_ENABLED="${LETSENCRYPT_AUTO_RENEW_ENABLED:-$(sudo awk -F= '/^LETSENCRYPT_AUTO_RENEW_ENABLED=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
  LETSENCRYPT_DNS_AUTOMATION_HOOK="${LETSENCRYPT_DNS_AUTOMATION_HOOK:-$(sudo awk -F= '/^LETSENCRYPT_DNS_AUTOMATION_HOOK=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)}"
}

setup_cloudflare_tunnel() {
  [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]] || return 0

  # Em modo external, falha de tunnel e critico; em modo hybrid, e aviso apenas.
  local tunnel_critical=0
  if [[ "$ACCESS_MODE" == "external" ]]; then tunnel_critical=1; fi

  log "Instalando e configurando cloudflared"

  if ! command -v cloudflared >/dev/null 2>&1; then
    if ! curl -fsSL --connect-timeout 10 --retry 2 --retry-delay 2 https://pkg.cloudflare.com/cloudflare-main.gpg | sudo gpg --yes --dearmor -o /usr/share/keyrings/cloudflare-main.gpg; then
      if [[ "$tunnel_critical" -eq 1 ]]; then
        fail "Nao foi possivel configurar repositorio do cloudflared (critico em ACCESS_MODE=external)."
      fi
      warn "Nao foi possivel configurar repositorio do cloudflared; seguindo sem tunnel automatico."; return
    fi
    echo "deb [signed-by=/usr/share/keyrings/cloudflare-main.gpg] https://pkg.cloudflare.com/cloudflared $(lsb_release -cs) main" | sudo tee /etc/apt/sources.list.d/cloudflared.list >/dev/null
    sudo apt-get update -y
    if ! sudo apt-get install -y cloudflared; then
      if [[ "$tunnel_critical" -eq 1 ]]; then
        fail "cloudflared indisponivel (critico em ACCESS_MODE=external)."
      fi
      warn "cloudflared indisponivel para esta arquitetura/distribuicao; seguindo sem tunnel automatico."; return
    fi
  fi

  if ! command -v cloudflared >/dev/null 2>&1; then
    if [[ "$tunnel_critical" -eq 1 ]]; then
      fail "cloudflared nao encontrado apos tentativa de instalacao (critico em ACCESS_MODE=external)."
    fi
    warn "cloudflared nao encontrado apos tentativa de instalacao; seguindo sem tunnel automatico."; return
  fi

  if ! sudo cloudflared service install "$CLOUDFLARE_TUNNEL_TOKEN"; then
    if [[ "$tunnel_critical" -eq 1 ]]; then
      fail "Falha ao configurar cloudflared service install (critico em ACCESS_MODE=external)."
    fi
    warn "Falha ao configurar cloudflared service install; siga com configuracao manual do tunnel."; return
  fi

  # O unit gerado pelo cloudflared embute o token do tunnel; restringir a leitura.
  if sudo test -f /etc/systemd/system/cloudflared.service; then
    sudo chmod 640 /etc/systemd/system/cloudflared.service 2>/dev/null || true
  fi

  sudo systemctl enable cloudflared || warn "Nao foi possivel habilitar servico cloudflared automaticamente."
  sudo systemctl restart cloudflared || warn "Nao foi possivel reiniciar cloudflared automaticamente."
}
