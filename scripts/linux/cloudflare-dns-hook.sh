#!/usr/bin/env bash
# Discovery RMM - Hook de automacao DNS para ZeroSSL (DNS-01) via Cloudflare API
# Chamado por zerossl-acme-certificate.sh:
#   hook <up|down> <record_type> <record_name> <record_value> <domain>
#
# Contrato de saida: exit 0 = operacao confirmada; exit != 0 = falha (o acme.sh
# registra e o desafio falha de forma visivel — nunca "sucesso falso").
# O token NUNCA vai para argv (usa -H @arquivo 0600) e o zone id fica em
# /etc/discovery-api/cloudflare.zone (nao hardcodado neste script).
set -euo pipefail

CF_TOKEN_FILE='/etc/discovery-api/cloudflare.token'
CF_ZONE_FILE='/etc/discovery-api/cloudflare.zone'
CF_API_BASE='https://api.cloudflare.com/client/v4'
CURL_OPTS=(--max-time 30 --connect-timeout 10 --retry 2 --retry-delay 2)

action="${1:-}"
record_type="${2:-}"
record_name="${3:-}"
record_value="${4:-}"
domain="${5:-}"

[[ -n "$action" && -n "$record_type" && -n "$record_name" && -n "$record_value" ]] || { echo '[cfhook] argumentos insuficientes' >&2; exit 1; }
[[ -f "$CF_TOKEN_FILE" ]] || { echo "[cfhook] token nao encontrado em $CF_TOKEN_FILE" >&2; exit 1; }

# Zone id: variavel de ambiente > arquivo de config. Nao existe valor padrao.
CF_ZONE_ID="${CF_ZONE_ID:-}"
if [[ -z "$CF_ZONE_ID" && -f "$CF_ZONE_FILE" ]]; then
  CF_ZONE_ID="$(head -n 1 "$CF_ZONE_FILE" | tr -d '[:space:]')"
fi
[[ -n "$CF_ZONE_ID" ]] || { echo "[cfhook] zone id ausente: defina CF_ZONE_ID no ambiente ou crie $CF_ZONE_FILE" >&2; exit 1; }

CF_TOKEN="$(tr -d '[:space:]' < "$CF_TOKEN_FILE")"
[[ -n "$CF_TOKEN" ]] || { echo "[cfhook] token vazio em $CF_TOKEN_FILE" >&2; exit 1; }

# Header de autenticacao em arquivo 0600 (token nao aparece no argv do curl,
# que e world-readable via /proc/*/cmdline).
HEADER_FILE="$(mktemp)"
chmod 600 "$HEADER_FILE"
printf 'Authorization: Bearer %s\n' "$CF_TOKEN" > "$HEADER_FILE"
trap 'rm -f "$HEADER_FILE"' EXIT

# Executa a chamada e valida o envelope de resposta da Cloudflare.
# Falha HTTP (curl -f) ou success=false => exit != 0.
cf_api() {
  local method="$1" path="$2" payload="${3:-}" out
  if [[ -n "$payload" ]]; then
    out="$(curl -fsS "${CURL_OPTS[@]}" -X "$method" -H "@${HEADER_FILE}" \
      -H 'Content-Type: application/json' --data "$payload" "${CF_API_BASE}${path}")"
  else
    out="$(curl -fsS "${CURL_OPTS[@]}" -X "$method" -H "@${HEADER_FILE}" "${CF_API_BASE}${path}")"
  fi
  printf '%s' "$out" | python3 -c '
import sys, json
try:
    d = json.load(sys.stdin)
except Exception as e:
    print(f"[cfhook] resposta invalida da API Cloudflare: {e}", file=sys.stderr)
    sys.exit(2)
if not d.get("success"):
    print(f"[cfhook] API Cloudflare retornou erro: {d.get(\"errors\")}", file=sys.stderr)
    sys.exit(3)
'
}

# Monta o payload TXT com quoting JSON correto (sem injecao de aspas).
txt_payload() {
  python3 -c 'import json, sys; print(json.dumps({"type": "TXT", "name": sys.argv[1], "content": sys.argv[2], "ttl": 120}))' "$1" "$2"
}

# Normaliza nome do registro: remove trailing dot
record_name="${record_name%.}"

find_record_ids() {
  curl -fsS "${CURL_OPTS[@]}" -H "@${HEADER_FILE}" \
    "${CF_API_BASE}/zones/${CF_ZONE_ID}/dns_records?type=TXT&name=${record_name}" \
  | python3 -c '
import sys, json
try:
    d = json.load(sys.stdin)
except Exception:
    sys.exit(0)
for r in d.get("result", []):
    print(r.get("id"))
'
}

delete_record() {
  local rid="$1"
  cf_api DELETE "/zones/${CF_ZONE_ID}/dns_records/${rid}"
}

case "$action" in
  up)
    echo "[cfhook] criando TXT $record_name"
    cf_api POST "/zones/${CF_ZONE_ID}/dns_records" "$(txt_payload "$record_name" "$record_value")"
    echo "[cfhook] TXT criado com sucesso."
    ;;
  down)
    echo "[cfhook] removendo TXT $record_name"
    mapfile -t ids < <(find_record_ids)
    if (( ${#ids[@]} == 0 )); then
      echo "[cfhook] nenhum registro TXT encontrado para remover."
      exit 0
    fi
    for rid in "${ids[@]}"; do
      [[ -n "$rid" ]] || continue
      delete_record "$rid"
      echo "[cfhook] registro $rid removido."
    done
    ;;
  *)
    echo "[cfhook] acao invalida: $action" >&2
    exit 1
    ;;
esac
