#!/usr/bin/env bash
set -euo pipefail

ACTION="${1:-issue}"
DISCOVERY_ENV_FILE="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"

load_env_file() {
  local file_path="$1"
  [[ -f "$file_path" ]] || return 0

  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    [[ -z "$line" ]] && continue
    [[ "$line" =~ ^[[:space:]]*# ]] && continue
    [[ "$line" == *"="* ]] || continue

    local key="${line%%=*}"
    local value="${line#*=}"

    key="$(printf '%s' "$key" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue

    if [[ "$value" =~ ^\".*\"$ ]]; then
      value="${value:1:${#value}-2}"
    elif [[ "$value" =~ ^\'.*\'$ ]]; then
      value="${value:1:${#value}-2}"
    fi

    export "$key=$value"
  done < "$file_path"
}

load_env_file "$DISCOVERY_ENV_FILE"

log() {
  printf '[letsencrypt] %s\n' "$*"
}

warn() {
  printf '[letsencrypt][aviso] %s\n' "$*" >&2
}

fail() {
  printf '[letsencrypt][erro] %s\n' "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || fail "Comando obrigatorio ausente: $1"
}

normalize_host_without_scheme() {
  local value="${1:-}"
  value="${value#http://}"
  value="${value#https://}"
  value="${value%%/*}"
  value="${value%.}"
  printf '%s' "$value"
}

is_truthy() {
  case "$(printf '%s' "${1:-}" | tr '[:upper:]' '[:lower:]')" in
    1|true|yes|y|sim|s) return 0 ;;
    *) return 1 ;;
  esac
}

provider="$(printf '%s' "${TLS_CERT_PROVIDER:-self-signed}" | tr '[:upper:]' '[:lower:]')"
case "$provider" in
  letsencrypt|lets-encrypt|le|letsencrypt-acme)
    ;;
  *)
    log "TLS_CERT_PROVIDER=$provider; nada a fazer."
    exit 0
    ;;
esac

LETSENCRYPT_ACME_SERVER="${LETSENCRYPT_ACME_SERVER:-https://acme-v02.api.letsencrypt.org/directory}"
LETSENCRYPT_ACME_HOME="${LETSENCRYPT_ACME_HOME:-/etc/discovery-api/acme}"
LETSENCRYPT_ACME_SH_DIR="${LETSENCRYPT_ACME_SH_DIR:-/opt/discovery-ops/acme.sh}"
LETSENCRYPT_ACME_SH="${LETSENCRYPT_ACME_SH:-$LETSENCRYPT_ACME_SH_DIR/acme.sh}"
LETSENCRYPT_CERT_KEY_PATH="${LETSENCRYPT_CERT_KEY_PATH:-/etc/discovery-api/certs/api-internal.key}"
LETSENCRYPT_CERT_FULLCHAIN_PATH="${LETSENCRYPT_CERT_FULLCHAIN_PATH:-/etc/discovery-api/certs/api-internal.crt}"
LETSENCRYPT_DNS_RESOLVERS="${LETSENCRYPT_DNS_RESOLVERS:-1.1.1.1,8.8.8.8}"
LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS="${LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS:-600}"
LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS="${LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS:-15}"
LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY="${LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY:-30}"
LETSENCRYPT_FORCE_RENEW="${LETSENCRYPT_FORCE_RENEW:-0}"

LETSENCRYPT_CERT_DOMAIN="$(normalize_host_without_scheme "${LETSENCRYPT_CERT_DOMAIN:-${Authentication__Fido2__ServerDomain:-${INTERNAL_API_HOST:-${EXTERNAL_API_HOST:-}}}}")"
[[ -n "$LETSENCRYPT_CERT_DOMAIN" ]] || fail "LETSENCRYPT_CERT_DOMAIN nao definido."
[[ -n "${LETSENCRYPT_ACME_EMAIL:-}" ]] || fail "LETSENCRYPT_ACME_EMAIL nao definido."

require_cmd git
require_cmd openssl
require_cmd dig

install_acme_sh() {
  if [[ -x "$LETSENCRYPT_ACME_SH" ]]; then
    return
  fi

  log "Instalando acme.sh em $LETSENCRYPT_ACME_SH_DIR"
  install -d -m 750 -o root -g discovery-api "$(dirname "$LETSENCRYPT_ACME_SH_DIR")"
  rm -rf "$LETSENCRYPT_ACME_SH_DIR"
  git clone --depth 1 https://github.com/acmesh-official/acme.sh.git "$LETSENCRYPT_ACME_SH_DIR"
  chown -R root:discovery-api "$LETSENCRYPT_ACME_SH_DIR"
  chmod 750 "$LETSENCRYPT_ACME_SH_DIR/acme.sh"
}

build_domain_args() {
  DOMAIN_ARGS=(-d "$LETSENCRYPT_CERT_DOMAIN")

  local raw_alt_domains="${LETSENCRYPT_CERT_ALT_DOMAINS:-}"
  raw_alt_domains="${raw_alt_domains//;/,}"
  IFS=',' read -r -a ALT_DOMAIN_ITEMS <<< "$raw_alt_domains"
  for item in "${ALT_DOMAIN_ITEMS[@]}"; do
    local domain
    domain="$(normalize_host_without_scheme "$(printf '%s' "$item" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')")"
    [[ -n "$domain" ]] || continue
    [[ "$domain" == "$LETSENCRYPT_CERT_DOMAIN" ]] && continue
    DOMAIN_ARGS+=(-d "$domain")
  done
}

register_account() {
  install -d -m 750 -o root -g discovery-api "$LETSENCRYPT_ACME_HOME"
  # Let's Encrypt nao usa EAB; apenas registra a conta com o email.
  "$LETSENCRYPT_ACME_SH" --home "$LETSENCRYPT_ACME_HOME" \
    --register-account \
    --server "$LETSENCRYPT_ACME_SERVER" \
    -m "$LETSENCRYPT_ACME_EMAIL" >/dev/null
}

extract_quoted_or_last_field() {
  local line="$1"
  local quoted
  quoted="$(printf '%s\n' "$line" | sed -n "s/.*'\([^']*\)'.*/\1/p")"
  if [[ -n "$quoted" ]]; then
    printf '%s' "$quoted"
    return
  fi
  printf '%s\n' "$line" | awk -F': ' '{print $NF}' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'
}

parse_dns_challenges() {
  local output_file="$1"
  local challenge_file="$2"
  local current_domain=""
  : > "$challenge_file"

  while IFS= read -r line; do
    if [[ "$line" == *"Domain:"* ]]; then
      current_domain="$(extract_quoted_or_last_field "$line")"
      continue
    fi

    if [[ "$line" == *"TXT value:"* && -n "$current_domain" ]]; then
      local txt_value
      txt_value="$(extract_quoted_or_last_field "$line")"
      if [[ -n "$txt_value" ]]; then
        printf 'TXT\t%s\t%s\n' "$current_domain" "$txt_value" >> "$challenge_file"
      fi
      current_domain=""
    fi
  done < "$output_file"
}

show_dns_challenges() {
  local challenge_file="$1"
  echo
  echo "Crie/atualize os registros DNS abaixo antes de continuar:"
  echo "----------------------------------------"
  while IFS=$'\t' read -r record_type record_name record_value; do
    [[ -n "$record_type" && -n "$record_name" && -n "$record_value" ]] || continue
    echo "Tipo: $record_type"
    echo "Nome: $record_name"
    echo "Valor: $record_value"
    echo "----------------------------------------"
  done < "$challenge_file"
}

call_dns_hook() {
  local action="$1"
  local challenge_file="$2"
  [[ -n "${LETSENCRYPT_DNS_AUTOMATION_HOOK:-}" ]] || return 1
  [[ -x "$LETSENCRYPT_DNS_AUTOMATION_HOOK" ]] || fail "LETSENCRYPT_DNS_AUTOMATION_HOOK nao executavel: $LETSENCRYPT_DNS_AUTOMATION_HOOK"

  while IFS=$'\t' read -r record_type record_name record_value; do
    [[ -n "$record_type" && -n "$record_name" && -n "$record_value" ]] || continue
    "$LETSENCRYPT_DNS_AUTOMATION_HOOK" "$action" "$record_type" "$record_name" "$record_value" "$LETSENCRYPT_CERT_DOMAIN"
  done < "$challenge_file"
  return 0
}

normalize_dns_value() {
  local record_type="$1"
  local value="$2"

  case "$record_type" in
    CNAME)
      printf '%s' "$value" | tr '[:upper:]' '[:lower:]' | sed 's/[.]$//'
      ;;
    TXT)
      printf '%s' "$value" | sed 's/" "//g;s/"//g;s/^[[:space:]]*//;s/[[:space:]]*$//'
      ;;
    *)
      printf '%s' "$value"
      ;;
  esac
}

dns_record_matches() {
  local resolver="$1"
  local record_type="$2"
  local record_name="$3"
  local expected_value="$4"
  local expected
  expected="$(normalize_dns_value "$record_type" "$expected_value")"

  local actual_values
  actual_values="$(dig +time=3 +tries=1 +short "$record_type" "$record_name" "@$resolver" 2>/dev/null || true)"
  [[ -n "$actual_values" ]] || return 1

  while IFS= read -r actual_value; do
    actual_value="$(normalize_dns_value "$record_type" "$actual_value")"
    if [[ "$actual_value" == "$expected" ]]; then
      return 0
    fi
  done <<< "$actual_values"

  return 1
}

wait_for_dns_records() {
  local challenge_file="$1"
  local timeout_seconds="$LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS"
  local poll_interval="$LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS"
  local elapsed=0
  local resolver_list
  resolver_list="${LETSENCRYPT_DNS_RESOLVERS//;/,}"
  resolver_list="${resolver_list// /,}"

  log "Validando DNS antes de prosseguir (resolvers: $resolver_list)"
  while (( elapsed <= timeout_seconds )); do
    local all_ok=1

    while IFS=$'\t' read -r record_type record_name record_value; do
      [[ -n "$record_type" && -n "$record_name" && -n "$record_value" ]] || continue
      local record_ok=0
      IFS=',' read -r -a resolvers <<< "$resolver_list"
      for resolver in "${resolvers[@]}"; do
        resolver="$(printf '%s' "$resolver" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
        [[ -n "$resolver" ]] || continue
        if dns_record_matches "$resolver" "$record_type" "$record_name" "$record_value"; then
          record_ok=1
          break
        fi
      done

      if [[ "$record_ok" -ne 1 ]]; then
        all_ok=0
        break
      fi
    done < "$challenge_file"

    if [[ "$all_ok" -eq 1 ]]; then
      log "DNS validado com sucesso."
      return 0
    fi

    sleep "$poll_interval"
    elapsed=$((elapsed + poll_interval))
  done

  show_dns_challenges "$challenge_file"
  fail "DNS nao contem os registros esperados apos ${timeout_seconds}s. Corrija os registros e execute novamente."
}

confirm_manual_dns_ready() {
  local challenge_file="$1"

  if call_dns_hook up "$challenge_file"; then
    log "Hook DNS executado; aguardando propagacao."
    return
  fi

  if [[ ! -t 0 ]]; then
    show_dns_challenges "$challenge_file"
    fail "Validacao DNS manual exige terminal interativo ou LETSENCRYPT_DNS_AUTOMATION_HOOK."
  fi

  show_dns_challenges "$challenge_file"
  local confirm=""
  read -r -p "Confirma que o DNS acima ja foi criado/atualizado? (s/N): " confirm
  confirm="$(printf '%s' "$confirm" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$confirm" in
    s|sim|y|yes) ;;
    *) fail "Operacao cancelada antes da validacao DNS." ;;
  esac
}

request_dns_challenges() {
  local output_file="$1"
  local challenge_file="$2"
  local command_action="$3"
  local -a command_args=(--home "$LETSENCRYPT_ACME_HOME" "$command_action" --server "$LETSENCRYPT_ACME_SERVER" --dns)
  command_args+=("${DOMAIN_ARGS[@]}")
  command_args+=(--yes-I-know-dns-manual-mode-enough-go-ahead-please)

  if [[ "$command_action" == "--renew" ]] && is_truthy "$LETSENCRYPT_FORCE_RENEW"; then
    command_args+=(--force)
  fi

  set +e
  "$LETSENCRYPT_ACME_SH" "${command_args[@]}" 2>&1 | tee "$output_file"
  local exit_code=${PIPESTATUS[0]}
  set -e

  parse_dns_challenges "$output_file" "$challenge_file"
  return "$exit_code"
}

complete_dns_challenge() {
  local -a command_args=(--home "$LETSENCRYPT_ACME_HOME" --renew --server "$LETSENCRYPT_ACME_SERVER" --dns)
  command_args+=("${DOMAIN_ARGS[@]}")
  command_args+=(--yes-I-know-dns-manual-mode-enough-go-ahead-please --force)

  local output_file
  output_file="$(mktemp)"
  set +e
  "$LETSENCRYPT_ACME_SH" "${command_args[@]}" 2>&1 | tee "$output_file"
  local exit_code=${PIPESTATUS[0]}
  set -e

  if [[ "$exit_code" -eq 0 ]]; then
    rm -f "$output_file"
    return 0
  fi

  # Alguns cenarios com sudo retornam codigo nao zero mesmo apos emissao bem-sucedida.
  local cert_dir_ecc="$LETSENCRYPT_ACME_HOME/${LETSENCRYPT_CERT_DOMAIN}_ecc"
  local cert_dir_rsa="$LETSENCRYPT_ACME_HOME/${LETSENCRYPT_CERT_DOMAIN}"
  if [[ -f "$cert_dir_ecc/fullchain.cer" || -f "$cert_dir_rsa/fullchain.cer" ]]; then
    warn "acme.sh retornou codigo $exit_code, mas os artefatos do certificado foram gerados; seguindo instalacao."
    rm -f "$output_file"
    return 0
  fi

  cat "$output_file" >&2
  rm -f "$output_file"
  fail "acme.sh falhou ao completar o desafio DNS (codigo $exit_code)."
}

install_certificate() {
  install -d -m 750 -o root -g discovery-api "$(dirname "$LETSENCRYPT_CERT_KEY_PATH")"
  local output_file
  output_file="$(mktemp)"
  set +e
  "$LETSENCRYPT_ACME_SH" --home "$LETSENCRYPT_ACME_HOME" \
    --install-cert \
    -d "$LETSENCRYPT_CERT_DOMAIN" \
    --key-file "$LETSENCRYPT_CERT_KEY_PATH" \
    --fullchain-file "$LETSENCRYPT_CERT_FULLCHAIN_PATH" \
    --reloadcmd "systemctl reload nginx >/dev/null 2>&1 || systemctl restart nginx >/dev/null 2>&1 || true" \
    2>&1 | tee "$output_file"
  local exit_code=${PIPESTATUS[0]}
  set -e

  if [[ "$exit_code" -ne 0 && ( ! -s "$LETSENCRYPT_CERT_KEY_PATH" || ! -s "$LETSENCRYPT_CERT_FULLCHAIN_PATH" ) ]]; then
    cat "$output_file" >&2
    rm -f "$output_file"
    fail "acme.sh falhou ao instalar o certificado (codigo $exit_code)."
  fi
  rm -f "$output_file"

  chmod 640 "$LETSENCRYPT_CERT_KEY_PATH"
  chmod 644 "$LETSENCRYPT_CERT_FULLCHAIN_PATH"
  chown root:discovery-api "$LETSENCRYPT_CERT_KEY_PATH"
  chown root:discovery-api "$LETSENCRYPT_CERT_FULLCHAIN_PATH"
}

certificate_needs_renewal() {
  if [[ ! -f "$LETSENCRYPT_CERT_FULLCHAIN_PATH" ]]; then
    return 0
  fi

  if is_truthy "$LETSENCRYPT_FORCE_RENEW"; then
    return 0
  fi

  local seconds_left=$((LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY * 86400))
  if openssl x509 -checkend "$seconds_left" -noout -in "$LETSENCRYPT_CERT_FULLCHAIN_PATH" >/dev/null 2>&1; then
    return 1
  fi

  return 0
}

# Recupera um certificado que ja foi emitido pela CA (order finalizado) mas
# ainda nao foi baixado/instalado localmente. Evita repetir o processo de
# renovacao (e o rate-limit) quando o acme.sh ja possui um cert valido.
# Retorna 0 se conseguiu instalar um certificado valido, 1 caso contrario.
recover_already_issued_certificate() {
  local domain_dir="$LETSENCRYPT_ACME_HOME/${LETSENCRYPT_CERT_DOMAIN}_ecc"
  local domain_conf="$domain_dir/${LETSENCRYPT_CERT_DOMAIN}.conf"
  local acme_fullchain="$domain_dir/fullchain.cer"
  local acme_key="$domain_dir/${LETSENCRYPT_CERT_DOMAIN}.key"

  # Sem conf do dominio no acme.sh, nao ha order para recuperar.
  [[ -f "$domain_conf" ]] || return 1

  local link_cert=""
  link_cert="$(awk -F= '/^Le_LinkCert=/{sub("^[^=]*=",""); gsub(/[\x27\x22]/,""); print; exit}' "$domain_conf" 2>/dev/null || true)"

  # Se o acme.sh ja tem um fullchain valido, instala direto (sem re-emitir).
  if [[ -s "$acme_fullchain" ]] && openssl x509 -checkend 0 -noout -in "$acme_fullchain" >/dev/null 2>&1; then
    log "Recuperando certificado ja emitido (fullchain valido no acme.sh)."
    install -d -m 750 -o root -g discovery-api "$(dirname "$LETSENCRYPT_CERT_KEY_PATH")"
    install -m 640 -o root -g discovery-api "$acme_key" "$LETSENCRYPT_CERT_KEY_PATH"
    install -m 644 -o root -g discovery-api "$acme_fullchain" "$LETSENCRYPT_CERT_FULLCHAIN_PATH"
    chown root:discovery-api "$LETSENCRYPT_CERT_KEY_PATH" "$LETSENCRYPT_CERT_FULLCHAIN_PATH"
    systemctl reload nginx >/dev/null 2>&1 || systemctl restart nginx >/dev/null 2>&1 || true
    log "Certificado recuperado e instalado em $LETSENCRYPT_CERT_FULLCHAIN_PATH"
    return 0
  fi

  # Se ha um Le_LinkCert (order finalizado) mas o fullchain nao foi baixado,
  # tenta baixar via acme.sh --renew --force (que baixa do order existente).
  if [[ -n "$link_cert" ]]; then
    log "Order ja finalizado (Le_LinkCert presente); tentando baixar o certificado emitido sem re-emitir."
    local out
    out="$(mktemp)"
    set +e
    "$LETSENCRYPT_ACME_SH" --home "$LETSENCRYPT_ACME_HOME" --renew -d "$LETSENCRYPT_CERT_DOMAIN" --force \
      --yes-I-know-dns-manual-mode-enough-go-ahead-please 2>&1 | tee "$out"
    local rc=${PIPESTATUS[0]}
    set -e
    rm -f "$out"

    if [[ -s "$acme_fullchain" ]] && openssl x509 -checkend 0 -noout -in "$acme_fullchain" >/dev/null 2>&1; then
      log "Certificado baixado com sucesso do order ja finalizado."
      install -d -m 750 -o root -g discovery-api "$(dirname "$LETSENCRYPT_CERT_KEY_PATH")"
      install -m 640 -o root -g discovery-api "$acme_key" "$LETSENCRYPT_CERT_KEY_PATH"
      install -m 644 -o root -g discovery-api "$acme_fullchain" "$LETSENCRYPT_CERT_FULLCHAIN_PATH"
      chown root:discovery-api "$LETSENCRYPT_CERT_KEY_PATH" "$LETSENCRYPT_CERT_FULLCHAIN_PATH"
      systemctl reload nginx >/dev/null 2>&1 || systemctl restart nginx >/dev/null 2>&1 || true
      log "Certificado recuperado e instalado em $LETSENCRYPT_CERT_FULLCHAIN_PATH"
      return 0
    fi

    warn "Nao foi possivel baixar o certificado do order ja finalizado (rc=$rc)."
  fi

  return 1
}

issue_or_renew() {
  local command_action="$1"

  install_acme_sh
  build_domain_args
  register_account

  if [[ "$command_action" == "--renew" ]] && ! certificate_needs_renewal; then
    log "Certificado ainda valido por mais de $LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY dias; renovacao ignorada."
    exit 0
  fi

  if [[ "$command_action" == "--renew" && -z "${LETSENCRYPT_DNS_AUTOMATION_HOOK:-}" && ! -t 0 ]]; then
    warn "Renovacao Let's Encrypt usa DNS manual e nao ha terminal interativo. Execute este script manualmente ou configure LETSENCRYPT_DNS_AUTOMATION_HOOK."
    exit 0
  fi

  # Antes de re-emitir, tenta recuperar um certificado ja emitido pela CA.
  if recover_already_issued_certificate; then
    exit 0
  fi

  local output_file
  local challenge_file
  output_file="$(mktemp)"
  challenge_file="$(mktemp)"

  if request_dns_challenges "$output_file" "$challenge_file" "$command_action"; then
    if [[ -s "$challenge_file" ]]; then
      confirm_manual_dns_ready "$challenge_file"
      wait_for_dns_records "$challenge_file"
      complete_dns_challenge
    fi
  else
    if [[ -s "$challenge_file" ]]; then
      confirm_manual_dns_ready "$challenge_file"
      wait_for_dns_records "$challenge_file"
      complete_dns_challenge
    elif grep -qiE 'Domains not changed\.|Skipping\. Next renewal time is:' "$output_file"; then
      log "Certificado ja valido e sem mudanca de dominio; seguindo com instalacao do certificado atual."
    else
      cat "$output_file" >&2
      rm -f "$output_file" "$challenge_file"
      fail "acme.sh falhou antes de retornar desafio DNS."
    fi
  fi

  install_certificate
  call_dns_hook down "$challenge_file" || true
  rm -f "$output_file" "$challenge_file"
  log "Certificado Let's Encrypt instalado em $LETSENCRYPT_CERT_FULLCHAIN_PATH"
}

case "$ACTION" in
  issue|install)
    issue_or_renew --issue
    ;;
  renew)
    issue_or_renew --renew
    ;;
  *)
    fail "Acao invalida: $ACTION (use issue ou renew)"
    ;;
esac
