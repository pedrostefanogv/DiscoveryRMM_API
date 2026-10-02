#!/usr/bin/env bash
# Smoke test do processamento em segundo plano — rodar apos o deploy da API.
#
# Uso:
#   SMOKE_TOKEN=<jwt> bash smoke_background_processing.sh
#   SMOKE_USER_ID=<guid-de-admin> bash smoke_background_processing.sh   # gera o JWT no servidor
# Opcionais: API_BASE (http://127.0.0.1:8080), JWT_KEY, SMOKE_TIMEOUT (120s)
#
# Cobre as regressoes corrigidas: contrato de nome do job (M2), forca do ciclo (M3),
# snapshots de metricas atualizando (SQL), backfill concluindo e formato camelCase
# do last_result_json (B1). Sai com codigo != 0 em qualquer falha.
set -uo pipefail

API_BASE="${API_BASE:-http://127.0.0.1:8080}"
JWT_KEY="${JWT_KEY:-/etc/discovery-api/certs/jwt-private.pem}"
SMOKE_TIMEOUT="${SMOKE_TIMEOUT:-120}"
GROUP="tickets"
METRICS_JOB="technician-metrics-refresh"
ZERO="00000000-0000-0000-0000-000000000000"
BODY=/tmp/smoke_body.json
FAIL=0

ok()  { echo "  [ok]    $1"; }
bad() { echo "  [FALHA] $1"; FAIL=1; }
jq_() { python3 -c "import json,sys;d=json.load(open('$BODY'));print($1)" 2>/dev/null; }

mint_token() {
  local sub="$1" now exp hdr pay si
  b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
  now="$(date +%s)"; exp=$((now + 900))
  hdr="$(printf '%s' '{"alg":"RS256","typ":"JWT"}' | b64url)"
  pay="$(printf '{"iss":"discovery","aud":"discovery","sub":"%s","jti":"11111111-2222-3333-4444-555555555555","mfa_verified":"true","iat":%s,"nbf":%s,"exp":%s}' "$sub" "$now" "$now" "$exp" | b64url)"
  si="$hdr.$pay"
  printf '%s' "$si" > /tmp/smoke_si.txt
  openssl dgst -sha256 -sign "$JWT_KEY" -out /tmp/smoke_sig.bin /tmp/smoke_si.txt >/dev/null 2>&1 || return 1
  printf '%s.%s' "$si" "$(openssl base64 -A -in /tmp/smoke_sig.bin | tr '+/' '-_' | tr -d '=')"
}

TOKEN="${SMOKE_TOKEN:-}"
if [[ -z "$TOKEN" ]]; then
  [[ -n "${SMOKE_USER_ID:-}" ]] || { echo "Defina SMOKE_TOKEN ou SMOKE_USER_ID."; exit 2; }
  [[ -r "$JWT_KEY" ]] || { echo "Chave JWT nao legivel: $JWT_KEY"; exit 2; }
  TOKEN="$(mint_token "$SMOKE_USER_ID")" || { echo "Falha ao gerar o token."; exit 2; }
fi

api() { curl -s -o "$BODY" -w '%{http_code}' -H "Authorization: Bearer $TOKEN" "$@"; }

echo "== 1. Scheduler e contrato de nomes (M2) =="
code="$(api "$API_BASE/api/v1/admin/jobs")"
if [[ "$code" == "200" ]]; then ok "GET /admin/jobs = 200"; else bad "GET /admin/jobs = $code"; fi
if [[ "$code" == "200" ]]; then
  [[ "$(jq_ "any(j.get('jobName')=='$METRICS_JOB' and j.get('jobGroup')=='$GROUP' for j in d.get('jobs',[]))")" == "True" ]] \
    && ok "job $GROUP/$METRICS_JOB registrado com o nome base" \
    || bad "job $GROUP/$METRICS_JOB nao encontrado no scheduler"
  [[ "$(jq_ "any(str(j.get('jobName','')).endswith('-trigger') for j in d.get('jobs',[]))")" == "False" ]] \
    && ok "nenhum JobKey com sufixo -trigger" \
    || bad "existe JobKey com sufixo -trigger (contrato M2 quebrado)"
fi

echo "== 2. Acionamento manual com force (M2 + M3) =="
code="$(api -X POST "$API_BASE/api/v1/admin/jobs/$GROUP/$METRICS_JOB/trigger?force=true")"
[[ "$code" == "200" ]] && ok "POST trigger?force=true = 200" || bad "POST trigger?force=true = $code"

echo "== 3. Snapshot de metricas atualiza (SQL + camelCase) =="
sleep 5
code="$(api "$API_BASE/api/v1/configurations/background-processing/status")"
if [[ "$code" == "200" ]]; then
  python3 - <<'PYEOF' > /tmp/smoke_metrics.txt 2>&1 || true
import json, datetime
d = json.load(open('/tmp/smoke_body.json'))
row = next((r for r in d if r.get('scopeType') == 'technician_metrics' and str(r.get('scopeId','')).lower() == '00000000-0000-0000-0000-000000000000'), None)
if row is None:
    print('missing'); raise SystemExit
raw = row.get('lastRunAt') or ''
try:
    ts = datetime.datetime.fromisoformat(raw.replace('Z','+00:00'))
    age = (datetime.datetime.now(datetime.timezone.utc) - ts).total_seconds()
except Exception:
    print('bad-date'); raise SystemExit
payload = row.get('lastResultJson') or ''
try:
    parsed = json.loads(payload)
except Exception:
    print('bad-json'); raise SystemExit
keys = set(parsed.keys())
print(('fresh' if age < 600 else 'stale') + '|' + ('camel' if 'updated' in keys or 'pending' in keys else ('pascal' if 'Updated' in keys or 'Pending' in keys else 'unknown')))
PYEOF
  res="$(cat /tmp/smoke_metrics.txt)"
  case "$res" in
    fresh*camel) ok "escopo global de metricas atualizado e last_result_json em camelCase ($res)";;
    *) bad "estado das metricas: $res (esperado fresh*camel)";;
  esac
else bad "GET /background-processing/status = $code"; fi

echo "== 4. Backfill conclui (sem status=failed) =="
code="$(api -X POST "$API_BASE/api/v1/configurations/background-processing/backfill")"
[[ "$code" == "202" || "$code" == "200" ]] && ok "POST backfill = $code" || bad "POST backfill = $code"
deadline=$(( $(date +%s) + SMOKE_TIMEOUT ))
state="pending"
while [[ $(date +%s) -lt $deadline ]]; do
  code="$(api "$API_BASE/api/v1/configurations/background-processing/status")"
  # status em camelCase (esperado) ou PascalCase (linha legada — B1 quebrado)
  state="$(python3 -c "import json;d=json.load(open('$BODY'));r=[x for x in d if x.get('scopeType')=='technician_metrics_backfill' and str(x.get('scopeId','')).lower()=='$ZERO'];p=(json.loads(r[0]['lastResultJson']) if r and r[0].get('lastResultJson') else {});print(p.get('status') or p.get('Status') or 'none')" 2>/dev/null)"
  [[ "$state" == "completed" || "$state" == "failed" ]] && break
  sleep 5
done
[[ "$state" == "completed" ]] && ok "backfill concluido" || bad "backfill terminou como '$state'"

echo "== 5. Journal (apenas quando executado no servidor) =="
if command -v journalctl >/dev/null 2>&1 && systemctl is-active --quiet discovery-api 2>/dev/null; then
  start="$(systemctl show -p ActiveEnterTimestamp --value discovery-api)"
  sql="$(journalctl -u discovery-api --since "$start" -o cat --no-pager 2>/dev/null | grep -ac 'Executed DbCommand' || true)"
  [[ "$sql" == "0" ]] && ok "sem EF SQL em Information no journal" || bad "$sql linhas de EF SQL em Information (M1)"
  err="$(journalctl -u discovery-api --since "$start" -o cat --no-pager 2>/dev/null | grep -ac '42601' || true)"
  [[ "$err" == "0" ]] && ok "sem erro 42601 (FILTER)" || bad "erro 42601 ainda presente (deploy do SQL pendente)"
fi

echo
if [[ "$FAIL" == "0" ]]; then echo "SMOKE: OK"; else echo "SMOKE: FALHOU"; fi
exit "$FAIL"
