#!/usr/bin/env bash
set -euo pipefail

ACTION="${1:-issue}"
DISCOVERY_ENV_FILE="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"

if [[ -f "$DISCOVERY_ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  source "$DISCOVERY_ENV_FILE"
fi

log() {
  printf '[zerossl] %s\n' "$*"
}

warn() {
  printf '[zerossl][aviso] %s\n' "$*" >&2
}

fail() {
  printf '[zerossl][erro] %s\n' "$*" >&2
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
  zerossl|zerossl-acme|acme)
    ;;
  *)
    log "TLS_CERT_PROVIDER=$provider; nada a fazer."
    exit 0
    ;;
esac

ZEROSSL_ACME_SERVER="${ZEROSSL_ACME_SERVER:-https://acme.zerossl.com/v2/DV90}"
ZEROSSL_ACME_HOME="${ZEROSSL_ACME_HOME:-/etc/discovery-api/acme}"
ZEROSSL_ACME_SH_DIR="${ZEROSSL_ACME_SH_DIR:-/opt/discovery-ops/acme.sh}"
ZEROSSL_ACME_SH="${ZEROSSL_ACME_SH:-$ZEROSSL_ACME_SH_DIR/acme.sh}"
ZEROSSL_CERT_KEY_PATH="${ZEROSSL_CERT_KEY_PATH:-/etc/discovery-api/certs/api-internal.key}"
ZEROSSL_CERT_FULLCHAIN_PATH="${ZEROSSL_CERT_FULLCHAIN_PATH:-/etc/discovery-api/certs/api-internal.crt}"
ZEROSSL_DNS_RESOLVERS="${ZEROSSL_DNS_RESOLVERS:-1.1.1.1,8.8.8.8}"
ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS="${ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS:-600}"
ZEROSSL_DNS_POLL_INTERVAL_SECONDS="${ZEROSSL_DNS_POLL_INTERVAL_SECONDS:-15}"
ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY="${ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY:-30}"
ZEROSSL_FORCE_RENEW="${ZEROSSL_FORCE_RENEW:-0}"

ZEROSSL_CERT_DOMAIN="$(normalize_host_without_scheme "${ZEROSSL_CERT_DOMAIN:-${Authentication__Fido2__ServerDomain:-${INTERNAL_API_HOST:-${EXTERNAL_API_HOST:-}}}}")"
[[ -n "$ZEROSSL_CERT_DOMAIN" ]] || fail "ZEROSSL_CERT_DOMAIN nao definido."
[[ -n "${ZEROSSL_ACME_EMAIL:-}" ]] || fail "ZEROSSL_ACME_EMAIL nao definido."
[[ -n "${ZEROSSL_ACME_EAB_KID:-}" ]] || fail "ZEROSSL_ACME_EAB_KID nao definido."
[[ -n "${ZEROSSL_ACME_EAB_HMAC_KEY:-}" ]] || fail "ZEROSSL_ACME_EAB_HMAC_KEY nao definido."

require_cmd git
require_cmd openssl
require_cmd dig

install_acme_sh() {
  if [[ -x "$ZEROSSL_ACME_SH" ]]; then
    return
  fi

  log "Instalando acme.sh em $ZEROSSL_ACME_SH_DIR"
  install -d -m 750 -o root -g discovery-api "$(dirname "$ZEROSSL_ACME_SH_DIR")"
  rm -rf "$ZEROSSL_ACME_SH_DIR"
  git clone --depth 1 https://github.com/acmesh-official/acme.sh.git "$ZEROSSL_ACME_SH_DIR"
  chown -R root:discovery-api "$ZEROSSL_ACME_SH_DIR"
  chmod 750 "$ZEROSSL_ACME_SH_DIR/acme.sh"
}

build_domain_args() {
  DOMAIN_ARGS=(-d "$ZEROSSL_CERT_DOMAIN")

  local raw_alt_domains="${ZEROSSL_CERT_ALT_DOMAINS:-}"
  raw_alt_domains="${raw_alt_domains//;/,}"
  IFS=',' read -r -a ALT_DOMAIN_ITEMS <<< "$raw_alt_domains"
  for item in "${ALT_DOMAIN_ITEMS[@]}"; do
    local domain
    domain="$(normalize_host_without_scheme "$(printf '%s' "$item" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')")"
    [[ -n "$domain" ]] || continue
    [[ "$domain" == "$ZEROSSL_CERT_DOMAIN" ]] && continue
    DOMAIN_ARGS+=(-d "$domain")
  done
}

register_account() {
  install -d -m 750 -o root -g discovery-api "$ZEROSSL_ACME_HOME"
  "$ZEROSSL_ACME_SH" --home "$ZEROSSL_ACME_HOME" \
    --register-account \
    --server "$ZEROSSL_ACME_SERVER" \
    -m "$ZEROSSL_ACME_EMAIL" \
    --eab-kid "$ZEROSSL_ACME_EAB_KID" \
    --eab-hmac-key "$ZEROSSL_ACME_EAB_HMAC_KEY" >/dev/null
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
  [[ -n "${ZEROSSL_DNS_AUTOMATION_HOOK:-}" ]] || return 1
  [[ -x "$ZEROSSL_DNS_AUTOMATION_HOOK" ]] || fail "ZEROSSL_DNS_AUTOMATION_HOOK nao executavel: $ZEROSSL_DNS_AUTOMATION_HOOK"

  while IFS=$'\t' read -r record_type record_name record_value; do
    [[ -n "$record_type" && -n "$record_name" && -n "$record_value" ]] || continue
    "$ZEROSSL_DNS_AUTOMATION_HOOK" "$action" "$record_type" "$record_name" "$record_value" "$ZEROSSL_CERT_DOMAIN"
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
  local timeout_seconds="$ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS"
  local poll_interval="$ZEROSSL_DNS_POLL_INTERVAL_SECONDS"
  local elapsed=0
  local resolver_list
  resolver_list="${ZEROSSL_DNS_RESOLVERS//;/,}"
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
    fail "Validacao DNS manual exige terminal interativo ou ZEROSSL_DNS_AUTOMATION_HOOK."
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
  local -a command_args=(--home "$ZEROSSL_ACME_HOME" "$command_action" --server "$ZEROSSL_ACME_SERVER" --dns)
  command_args+=("${DOMAIN_ARGS[@]}")
  command_args+=(--yes-I-know-dns-manual-mode-enough-go-ahead-please)

  if [[ "$command_action" == "--renew" ]] && is_truthy "$ZEROSSL_FORCE_RENEW"; then
    command_args+=(--force)
  fi

  set +e
  "$ZEROSSL_ACME_SH" "${command_args[@]}" 2>&1 | tee "$output_file"
  local exit_code=${PIPESTATUS[0]}
  set -e

  parse_dns_challenges "$output_file" "$challenge_file"
  return "$exit_code"
}

complete_dns_challenge() {
  local -a command_args=(--home "$ZEROSSL_ACME_HOME" --renew --server "$ZEROSSL_ACME_SERVER" --dns)
  command_args+=("${DOMAIN_ARGS[@]}")
  command_args+=(--yes-I-know-dns-manual-mode-enough-go-ahead-please --force)
  "$ZEROSSL_ACME_SH" "${command_args[@]}"
}

install_certificate() {
  install -d -m 750 -o root -g discovery-api "$(dirname "$ZEROSSL_CERT_KEY_PATH")"
  "$ZEROSSL_ACME_SH" --home "$ZEROSSL_ACME_HOME" \
    --install-cert \
    -d "$ZEROSSL_CERT_DOMAIN" \
    --key-file "$ZEROSSL_CERT_KEY_PATH" \
    --fullchain-file "$ZEROSSL_CERT_FULLCHAIN_PATH" \
    --reloadcmd "systemctl reload nginx >/dev/null 2>&1 || systemctl restart nginx >/dev/null 2>&1 || true"

  chmod 640 "$ZEROSSL_CERT_KEY_PATH"
  chmod 644 "$ZEROSSL_CERT_FULLCHAIN_PATH"
  chown root:discovery-api "$ZEROSSL_CERT_KEY_PATH"
  chown root:discovery-api "$ZEROSSL_CERT_FULLCHAIN_PATH"
}

certificate_needs_renewal() {
  if [[ ! -f "$ZEROSSL_CERT_FULLCHAIN_PATH" ]]; then
    return 0
  fi

  if is_truthy "$ZEROSSL_FORCE_RENEW"; then
    return 0
  fi

  local seconds_left=$((ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY * 86400))
  if openssl x509 -checkend "$seconds_left" -noout -in "$ZEROSSL_CERT_FULLCHAIN_PATH" >/dev/null 2>&1; then
    return 1
  fi

  return 0
}

issue_or_renew() {
  local command_action="$1"

  install_acme_sh
  build_domain_args
  register_account

  if [[ "$command_action" == "--renew" ]] && ! certificate_needs_renewal; then
    log "Certificado ainda valido por mais de $ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY dias; renovacao ignorada."
    exit 0
  fi

  if [[ "$command_action" == "--renew" && -z "${ZEROSSL_DNS_AUTOMATION_HOOK:-}" && ! -t 0 ]]; then
    warn "Renovacao ZeroSSL usa DNS manual e nao ha terminal interativo. Execute este script manualmente ou configure ZEROSSL_DNS_AUTOMATION_HOOK."
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
    else
      cat "$output_file" >&2
      rm -f "$output_file" "$challenge_file"
      fail "acme.sh falhou antes de retornar desafio DNS."
    fi
  fi

  install_certificate
  call_dns_hook down "$challenge_file" || true
  rm -f "$output_file" "$challenge_file"
  log "Certificado ZeroSSL instalado em $ZEROSSL_CERT_FULLCHAIN_PATH"
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