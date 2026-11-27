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

setup_agent_mtls_ca() {
  local cert_dir="/etc/discovery-api/certs"
  local ca_cert_path="$cert_dir/agent-ca.crt"
  local ca_key_path="$cert_dir/agent-ca.key"

  sudo install -d -m 750 -o root -g discovery-api "$cert_dir"

  if sudo test -s "$ca_cert_path" && sudo test -s "$ca_key_path"; then
    log "CA mTLS de agents ja existe; mantendo arquivos atuais"
    sudo chmod 644 "$ca_cert_path"
    sudo chmod 640 "$ca_key_path"
    sudo chown root:discovery-api "$ca_cert_path" "$ca_key_path" 2>/dev/null || true
    return 0
  fi

  log "Gerando CA mTLS interna de agents (ECDSA P-256, 10 anos)"
  local ca_conf; ca_conf="$(mktemp)"
  local key_tmp; key_tmp="$(mktemp)"
  local cert_tmp; cert_tmp="$(mktemp)"

  cat > "$ca_conf" <<'EOF'
[req]
prompt = no
default_md = sha256
distinguished_name = dn
x509_extensions = v3_ca

[dn]
CN = DiscoveryRMM Internal Agent CA
O = DiscoveryRMM

[v3_ca]
basicConstraints = critical,CA:TRUE
keyUsage = critical,keyCertSign,cRLSign
subjectKeyIdentifier = hash
EOF

  if ! openssl ecparam -name prime256v1 -genkey -noout -out "$key_tmp" \
    || ! openssl req -x509 -new -nodes -key "$key_tmp" -sha256 -days 3650 \
         -config "$ca_conf" -extensions v3_ca -out "$cert_tmp"; then
    rm -f "$ca_conf" "$key_tmp" "$cert_tmp"
    warn "Falha ao gerar CA mTLS de agents via openssl."
    return 1
  fi

  sudo install -m 640 -o root -g discovery-api "$key_tmp" "$ca_key_path"
  sudo install -m 644 -o root -g discovery-api "$cert_tmp" "$ca_cert_path"
  rm -f "$ca_conf" "$key_tmp" "$cert_tmp"
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

  # Guardas ${VAR:-}: este caminho tambem e alcancado fora do wizard (modes.sh
  # switch_tls_provider), onde ACCESS_MODE/hosts nunca foram normalizados. Sem
  # elas, set -u aborta com "ACCESS_MODE: unbound variable". Vazio equivale a
  # "nem internal nem external": nenhum SAN extra e adicionado.
  if [[ "${ACCESS_MODE:-}" == "internal" || "${ACCESS_MODE:-}" == "hybrid" ]]; then
    san_entry="$(build_certificate_san_entry "${INTERNAL_API_HOST:-}")"
    [[ -n "$san_entry" ]] && san_entries+=("$san_entry")
  fi

  if [[ "${ACCESS_MODE:-}" == "external" || "${ACCESS_MODE:-}" == "hybrid" ]]; then
    san_entry="$(build_certificate_san_entry "${EXTERNAL_API_HOST:-}")"
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

  load_env_keys_if_unset "$env_file" \
    TLS_CERT_PROVIDER \
    ZEROSSL_CERT_DOMAIN ZEROSSL_CERT_ALT_DOMAINS ZEROSSL_ACME_EMAIL \
    ZEROSSL_ACME_EAB_KID ZEROSSL_ACME_EAB_HMAC_KEY \
    ZEROSSL_DNS_RESOLVERS ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS \
    ZEROSSL_DNS_POLL_INTERVAL_SECONDS ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY \
    ZEROSSL_AUTO_RENEW_ENABLED ZEROSSL_DNS_AUTOMATION_HOOK \
    LETSENCRYPT_CERT_DOMAIN LETSENCRYPT_CERT_ALT_DOMAINS LETSENCRYPT_ACME_EMAIL \
    LETSENCRYPT_DNS_RESOLVERS LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS \
    LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY \
    LETSENCRYPT_AUTO_RENEW_ENABLED LETSENCRYPT_DNS_AUTOMATION_HOOK
}

# ── Cloudflare Tunnel (cloudflared) ────────────────────────────────────────
#
# Duas armadilhas conhecidas deste passo (BUG 2026-11-20, host ARM64/Ubuntu
# 26.04 "resolute"):
#
#  1) O repositorio apt da Cloudflare nao publica toda suite que o Ubuntu
#     publica. Em suites novas (resolute, questing, ...) o Release da suite
#     nao existe e o apt responde 404:
#       Err:7 https://pkg.cloudflare.com/cloudflared resolute Release
#         404  Not Found
#       E: The repository '... resolute Release' does not have a Release file.
#     Gravar o .list com "$(lsb_release -cs)" cru nesse cenario envenena o apt
#     do host inteiro (TODO apt-get update passa a falhar), inclusive o
#     apt-get upgrade de apply_system_updates.
#
#  2) O repo quebrado derrubava a instalacao INTEIRA em ACCESS_MODE=external:
#     o "if ! apt-get install cloudflared" capturava tambem o erro do
#     "apt-get update" e chamava fail(), abortando a instalacao no meio (com
#     postgres/redis/nats/chaves ja criados). O cloudflared e UM servico
#     opcional: nenhuma falha dele deve abortar o instalador; o tunnel fica
#     indisponivel e o operador e instruido a instalar/rodar a mao.
#
# Estrategia: so gravamos o .list depois de CONFIRMAR (curl no Release) que a
# suite existe; se nao existir, caimos para a suite publicada mais proxima
# (o binario do cloudflared e estatico, entao roda em qualquer suite); se
# nenhuma servir, baixamos o binario oficial direto do GitHub (mesmo padrao
# ja usado em install_rustfs_binary/ensure_mc_cli).

# Suites publicadas hoje em https://pkg.cloudflare.com/cloudflared/dists/
# (ordem: da mais nova para a mais antiga). Usado como fallback quando a suite
# local nao tem Release. Manter ordenado do mais novo para o mais antigo.
CLOUDFLARED_APT_SUITES_FALLBACK=(noble jammy focal bookworm bullseye)

# Mapeia VERSION_CODENAME -> suite do Cloudflare. Debian usa apelidos de
# brinquedo (trixie/forky) que o Cloudflare nao publica; mapeamos para a suite
# Debian LTS suportada.
cloudflared_apt_suite_candidates() {
  local codename="${1:-}"
  local -a candidates=()

  if [[ -n "$codename" ]]; then candidates+=("$codename"); fi

  case "$codename" in
    # Ubuntu ainda nao publicado pelo Cloudflare -> suite Ubuntu LTS conhecida.
    resolute|questing|plucky|oracular) candidates+=(noble jammy) ;;
    # Debian testing/forky: Cloudflare publica apenas as LTS.
    trixie|forky|sid|testing) candidates+=(bookworm bullseye) ;;
  esac

  local suite
  for suite in "${CLOUDFLARED_APT_SUITES_FALLBACK[@]}"; do candidates+=("$suite"); done

  # Remove vazios/duplicados preservando a ordem (a primeira suite valida vence).
  local -A seen=()
  for suite in "${candidates[@]}"; do
    [[ -n "$suite" ]] || continue
    if [[ -z "${seen[$suite]:-}" ]]; then seen[$suite]=1; printf '%s\n' "$suite"; fi
  done
}

# Retorna a primeira suite com Release publicado, ou 1 se nenhuma servir.
# A checagem e FAIL-SAFE: sem rede/curl, retorna 1 e o caller decide (nao
# gravamos um .list que nao conseguimos validar).
resolve_cloudflared_apt_suite() {
  local suite
  while IFS= read -r suite; do
    [[ -n "$suite" ]] || continue
    if curl -fsI --connect-timeout 10 --retry 2 --retry-delay 2 \
        "https://pkg.cloudflare.com/cloudflared/dists/${suite}/Release" >/dev/null 2>&1; then
      printf '%s' "$suite"
      return 0
    fi
  done < <(cloudflared_apt_suite_candidates "${1:-}")
  return 1
}

# Instala o binario oficial direto do GitHub (arm64/amd64). Usado quando o repo
# apt nao serve para a suite do host. Instalacao manual/idempotente: nunca
# sobrescreve um binario ja presente.
install_cloudflared_from_github() {
  local arch asset base url tmp
  case "$(uname -m)" in
    x86_64|amd64)   asset="cloudflared-linux-amd64" ;;
    aarch64|arm64)  asset="cloudflared-linux-arm64" ;;
    armv7l|armhf)   asset="cloudflared-linux-arm"   ;;
    *) return 1 ;;
  esac

  base="${CLOUDFLARED_DOWNLOAD_BASE:-https://github.com/cloudflare/cloudflared/releases/latest/download}"
  url="${base}/${asset}"
  tmp="$(mktemp)"

  if ! curl -fsSL --connect-timeout 20 --retry 3 --retry-delay 2 -o "$tmp" "$url"; then
    rm -f "$tmp"; return 1
  fi
  if ! sudo install -m 0755 -o root -g root "$tmp" /usr/local/bin/cloudflared; then
    rm -f "$tmp"; return 1
  fi
  rm -f "$tmp"
  return 0
}

# Codename do host (Ubuntu/Debian). Prefere VERSION_CODENAME de /etc/os-release
# (nao depende do lsb_release estar instalado/consistente) e cai para
# lsb_release -cs. Nao falha: codename vazio so reduz a lista de candidatas.
cloudflared_host_codename() {
  local codename=""
  if [[ -r /etc/os-release ]]; then
    codename="$(. /etc/os-release && printf '%s' "${VERSION_CODENAME:-}")"
  fi
  if [[ -z "$codename" ]] && command -v lsb_release >/dev/null 2>&1; then
    codename="$(lsb_release -cs 2>/dev/null || true)"
  fi
  printf '%s' "$codename"
}

# Instala o cloudflared via apt (repo oficial) e valida. Retorna 0 em sucesso.
# Uso: install_cloudflared_via_apt [codename-do-host]
install_cloudflared_via_apt() {
  local keyring="/usr/share/keyrings/cloudflare-main.gpg"
  local list_file="/etc/apt/sources.list.d/cloudflared.list"
  local key_tmp

  key_tmp="$(mktemp)"
  if ! curl -fsSL --connect-timeout 10 --retry 2 --retry-delay 2 \
      https://pkg.cloudflare.com/cloudflare-main.gpg -o "$key_tmp"; then
    rm -f "$key_tmp"
    warn "Nao foi possivel baixar a chave do repositorio do cloudflared."
    return 1
  fi
  if ! sudo gpg --yes --dearmor -o "$keyring" < "$key_tmp"; then
    rm -f "$key_tmp"
    warn "Nao foi possivel instalar a chave do repositorio do cloudflared."
    return 1
  fi
  rm -f "$key_tmp"

  local host_suite="${1:-}"
  local suite
  # resolve_cloudflared_apt_suite faz a checagem REAL (curl no Release): a suite
  # local pode nao existir no repo do Cloudflare (Ubuntu 26.04/resolute), e
  # nesse caso a primeira candidata valida vence. So gravamos o .list com uma
  # suite confirmada — nunca com "$(lsb_release -cs)" as cegas, que envenena o
  # apt do host inteiro com um 404.
  if ! suite="$(resolve_cloudflared_apt_suite "$host_suite")"; then
    warn "Repositorio apt do cloudflared nao publica suite compativel com este host (suite local: ${host_suite:-desconhecida}); usando download direto do binario."
    return 1
  fi

  if [[ "$suite" != "$host_suite" ]]; then
    log "Suite '${host_suite:-desconhecida}' sem Release no repo do cloudflared; usando a suite '${suite}' (binario estatico, compativel)."
  fi

  echo "deb [signed-by=${keyring}] https://pkg.cloudflare.com/cloudflared ${suite} main" | sudo tee "$list_file" >/dev/null

  # apt-get update e NEEDED aqui para carregar o novo repo. Nao tratamos o
  # exit code como fatal: outros repos do host podem falhar (inclusive um
  # cloudflared.list antigo e quebrado, que removemos antes) sem que isso
  # signifique que o pacote do cloudflared esta indisponivel.
  if ! sudo apt-get update -y \
      -o Dir::Etc::sourcelist="$list_file" \
      -o Dir::Etc::sourceparts="-" \
      -o APT::Get::List-Cleanup="0"; then
    warn "apt-get update do repositorio do cloudflared falhou; tentando o pacote mesmo assim."
  fi

  if ! sudo apt-get install -y \
      -o Dir::Etc::sourcelist="$list_file" \
      -o Dir::Etc::sourceparts="-" \
      -o APT::Get::List-Cleanup="0" \
      cloudflared; then
    warn "Pacote cloudflared indisponivel no repositorio apt para a suite '${suite}'."
    return 1
  fi

  command -v cloudflared >/dev/null 2>&1
}

setup_cloudflare_tunnel() {
  [[ "${ACCESS_MODE:-}" == "external" || "${ACCESS_MODE:-}" == "hybrid" ]] || return 0

  log "Instalando e configurando cloudflared"

  if ! command -v cloudflared >/dev/null 2>&1; then
    # Caminho 1: repositorio apt oficial (respeita o gerenciador de pacotes).
    install_cloudflared_via_apt "$(cloudflared_host_codename)" || true
  fi

  if ! command -v cloudflared >/dev/null 2>&1; then
    # Caminho 2: binario oficial do GitHub (independente de suite/arquitetura).
    log "cloudflared ausente apos o apt; baixando o binario oficial (independente de distribuicao)."
    install_cloudflared_from_github || true
  fi

  if ! command -v cloudflared >/dev/null 2>&1; then
    if [[ -f /etc/apt/sources.list.d/cloudflared.list ]]; then
      # O .list so existe aqui se foi gravado por uma execucao ANTIGA (a versao
      # nova valida o Release antes de gravar). Deixar um repo 404 na maquina
      # quebra todo 'apt-get update' seguinte, inclusive de terceiros.
      sudo mv /etc/apt/sources.list.d/cloudflared.list /tmp/cloudflared.list.quebrado 2>/dev/null \
        || warn "Remova manualmente /etc/apt/sources.list.d/cloudflared.list (repositorio invalido)."
      warn "Repositorio apt invalido do cloudflared foi desativado (backup: /tmp/cloudflared.list.quebrado); rode 'sudo apt-get update'."
    fi
    warn "cloudflared indisponivel nesta arquitetura/distribuicao; seguindo SEM tunnel automatico."
    warn "Instale manualmente e configure o tunnel (troque o asset pela sua arquitetura):"
    warn "  asset=\$(uname -m | sed 's/x86_64/amd64/;s/aarch64/arm64/')"
    warn "  sudo curl -fsSL -o /usr/local/bin/cloudflared https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-\$asset"
    warn "  sudo chmod 0755 /usr/local/bin/cloudflared"
    warn "  sudo cloudflared service install <TOKEN_DO_TUNNEL>"
    return 0
  fi

  # Token pode nao existir fora do wizard (prompts.sh/normalize.sh): sem a guarda
  # ${VAR:-} o set -u aborta aqui em vez de emitir o warn abaixo. Token vazio
  # nao configura um tunnel valido, entao segue pelo mesmo ramo de aviso.
  if [[ -z "${CLOUDFLARE_TUNNEL_TOKEN:-}" ]]; then
    warn "CLOUDFLARE_TUNNEL_TOKEN ausente; cloudflared instalado mas o tunnel NAO foi configurado."
    warn "Configure depois: sudo cloudflared service install <TOKEN_DO_TUNNEL>"
    return 0
  fi

  if ! sudo cloudflared service install "${CLOUDFLARE_TUNNEL_TOKEN:-}"; then
    warn "Falha ao configurar cloudflared service install; siga com configuracao manual do tunnel."
    return 0
  fi

  # O unit gerado pelo cloudflared embute o token do tunnel; restringir a leitura.
  if sudo test -f /etc/systemd/system/cloudflared.service; then
    sudo chmod 640 /etc/systemd/system/cloudflared.service 2>/dev/null || true
  fi

  sudo systemctl enable cloudflared || warn "Nao foi possivel habilitar servico cloudflared automaticamente."
  sudo systemctl restart cloudflared || warn "Nao foi possivel reiniciar cloudflared automaticamente."
}
