#!/usr/bin/env bash
# Discovery RMM - Hook de automacao DNS para ZeroSSL (DNS-01) via Cloudflare API
# Chamado por zerossl-acme-certificate.sh:
#   hook <up|down> <record_type> <record_name> <record_value> <domain>
set -euo pipefail

CF_TOKEN_FILE='/etc/discovery-api/cloudflare.token'
CF_ZONE_ID='e41ec27c329aa8bcdcb8b8fb986cb11e'

action="${1:-}"
record_type="${2:-}"
record_name="${3:-}"
record_value="${4:-}"
domain="${5:-}"

[[ -n "$action" && -n "$record_type" && -n "$record_name" && -n "$record_value" ]] || { echo '[cfhook] argumentos insuficientes' >&2; exit 1; }
[[ -f "$CF_TOKEN_FILE" ]] || { echo "[cfhook] token nao encontrado em $CF_TOKEN_FILE" >&2; exit 1; }
CF_TOKEN="$(cat "$CF_TOKEN_FILE")"

# Normaliza nome do registro: remove trailing dot
record_name="${record_name%.}"

# Busca IDs de registros TXT existentes com o mesmo nome
find_record_ids() {
  curl -s -X GET "https://api.cloudflare.com/client/v4/zones/$CF_ZONE_ID/dns_records?type=TXT&name=$record_name" \
    -H "Authorization: Bearer $CF_TOKEN" -H 'Content-Type: application/json' \
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

create_record() {
  curl -s -X POST "https://api.cloudflare.com/client/v4/zones/$CF_ZONE_ID/dns_records" \
    -H "Authorization: Bearer $CF_TOKEN" -H 'Content-Type: application/json' \
    --data "{\"type\":\"TXT\",\"name\":\"$record_name\",\"content\":\"$record_value\",\"ttl\":120}" \
  | python3 -c 'import sys,json; d=json.load(sys.stdin); print("success" if d.get("success") else "fail: "+str(d.get("errors")))'
}

delete_record() {
  local rid="$1"
  curl -s -X DELETE "https://api.cloudflare.com/client/v4/zones/$CF_ZONE_ID/dns_records/$rid" \
    -H "Authorization: Bearer $CF_TOKEN" -H 'Content-Type: application/json' \
  | python3 -c 'import sys,json; d=json.load(sys.stdin); print("success" if d.get("success") else "fail: "+str(d.get("errors")))'
}

case "$action" in
  up)
    echo "[cfhook] criando TXT $record_name = $record_value"
    create_record
    ;;
  down)
    echo "[cfhook] removendo TXT $record_name"
    for rid in $(find_record_ids); do
      [[ -n "$rid" ]] && delete_record "$rid"
    done
    ;;
  *)
    echo "[cfhook] acao invalida: $action" >&2
    exit 1
    ;;
esac
