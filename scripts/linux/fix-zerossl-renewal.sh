#!/usr/bin/env bash
# Discovery RMM - Correcao da renovacao ZeroSSL (2026-08-01)
# 1) Atualiza /etc/discovery-api/discovery.env para provider zerossl-acme + hook DNS
# 2) Torna EAB opcional no zerossl-acme-certificate.sh quando a conta acme.sh ja existe
#
# USO: sudo ./fix-zerossl-renewal.sh [dominio] [email]
#   - Sem argumentos, le ZEROSSL_CERT_DOMAIN e ZEROSSL_ACME_EMAIL do proprio
#     /etc/discovery-api/discovery.env (o script NAO traz mais valores fixos de
#     nenhuma instalacao — rodar em outro servidor nao sobrescreve a config TLS).
set -euo pipefail

ENV_FILE="/etc/discovery-api/discovery.env"
SCRIPT="/opt/discovery-ops/zerossl-acme-certificate.sh"
ACME_HOME="/etc/discovery-api/acme"
ACCOUNT_DIR="$ACME_HOME/ca/acme.zerossl.com/v2/DV90"

log() { printf '[fix] %s\n' "$*"; }
fail() { printf '[fix][erro] %s\n' "$*" >&2; exit 1; }

require_cmd() { command -v "$1" >/dev/null 2>&1 || fail "Comando obrigatorio ausente: $1"; }

[[ "$(id -u)" -eq 0 ]] || fail "Execute como root (o script altera $ENV_FILE e $SCRIPT)."
require_cmd python3
[[ -f "$ENV_FILE" ]] || fail "Arquivo $ENV_FILE nao encontrado (execute o instalador primeiro)."

arg_domain="${1:-}"
arg_email="${2:-}"

get_env_key() {
  awk -F= -v k="$1" '$1==k {sub("^[^=]*=",""); print; exit}' "$ENV_FILE" 2>/dev/null || true
}

ZEROSSL_DOMAIN="${arg_domain:-$(get_env_key ZEROSSL_CERT_DOMAIN)}"
ZEROSSL_EMAIL="${arg_email:-$(get_env_key ZEROSSL_ACME_EMAIL)}"

if [[ -z "$ZEROSSL_DOMAIN" ]]; then
  fail "Dominio ZeroSSL nao informado. Use: $0 <dominio> [email] (ou defina ZEROSSL_CERT_DOMAIN no $ENV_FILE)"
fi
if [[ -z "$ZEROSSL_EMAIL" ]]; then
  fail "Email ACME nao informado. Use: $0 <dominio> <email> (ou defina ZEROSSL_ACME_EMAIL no $ENV_FILE)"
fi

STAMP="$(date +%Y%m%d%H%M%S)"

# ── 1) Atualiza discovery.env ─────────────────────────────────────────────
log "Atualizando $ENV_FILE"

# Backup com timestamp (reruns nao sobrescrevem o backup original)
cp "$ENV_FILE" "$ENV_FILE.bak-fix-$STAMP"

# Funcao: define ou atualiza chave
set_env_key() {
  local key="$1" value="$2"
  if grep -q "^${key}=" "$ENV_FILE" 2>/dev/null; then
    sed -i "s|^${key}=.*|${key}=${value}|" "$ENV_FILE"
  else
    printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
  fi
}

set_env_key "TLS_CERT_PROVIDER" "zerossl-acme"
set_env_key "ZEROSSL_CERT_DOMAIN" "$ZEROSSL_DOMAIN"
set_env_key "ZEROSSL_CERT_ALT_DOMAINS" "$(get_env_key ZEROSSL_CERT_ALT_DOMAINS)"
set_env_key "ZEROSSL_ACME_EMAIL" "$ZEROSSL_EMAIL"
set_env_key "ZEROSSL_ACME_EAB_KID" "$(get_env_key ZEROSSL_ACME_EAB_KID)"
set_env_key "ZEROSSL_ACME_EAB_HMAC_KEY" "$(get_env_key ZEROSSL_ACME_EAB_HMAC_KEY)"
set_env_key "ZEROSSL_DNS_RESOLVERS" "1.1.1.1,8.8.8.8"
set_env_key "ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS" "600"
set_env_key "ZEROSSL_DNS_POLL_INTERVAL_SECONDS" "15"
set_env_key "ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY" "30"
set_env_key "ZEROSSL_AUTO_RENEW_ENABLED" "1"
set_env_key "ZEROSSL_DNS_AUTOMATION_HOOK" "/opt/discovery-ops/cloudflare-dns-hook.sh"

chmod 640 "$ENV_FILE"
chown root:discovery-api "$ENV_FILE"
log "discovery.env atualizado (dominio: $ZEROSSL_DOMAIN)"

# ── 2) Torna EAB opcional quando a conta acme.sh ja existe ────────────────
log "Ajustando $SCRIPT para EAB opcional (conta ja registrada)"

# Backup do script
cp "$SCRIPT" "$SCRIPT.bak-fix-$STAMP"

# 2a) Validacao de EAB: so exige se a conta nao estiver registrada
python3 - "$SCRIPT" "$ACCOUNT_DIR" <<'PYEOF'
import sys, re

script = sys.argv[1]
account_dir = sys.argv[2]
account_exists = False
import os
if os.path.isdir(account_dir):
    for f in ("account.key", "account.json"):
        if os.path.isfile(os.path.join(account_dir, f)):
            account_exists = True
            break

with open(script, "r", encoding="utf-8") as fh:
    content = fh.read()

# Substitui a validacao obrigatoria de EAB por validacao condicional
old_validation = '''[[ -n "${ZEROSSL_ACME_EMAIL:-}" ]] || fail "ZEROSSL_ACME_EMAIL nao definido."
[[ -n "${ZEROSSL_ACME_EAB_KID:-}" ]] || fail "ZEROSSL_ACME_EAB_KID nao definido."
[[ -n "${ZEROSSL_ACME_EAB_HMAC_KEY:-}" ]] || fail "ZEROSSL_ACME_EAB_HMAC_KEY nao definido."'''

new_validation = '''[[ -n "${ZEROSSL_ACME_EMAIL:-}" ]] || fail "ZEROSSL_ACME_EMAIL nao definido."
# EAB so e obrigatorio quando a conta acme.sh ainda nao esta registrada.
if [[ ! -f "$ZEROSSL_ACME_HOME/ca/acme.zerossl.com/v2/DV90/account.key" && ! -f "$ZEROSSL_ACME_HOME/ca/acme.zerossl.com/v2/DV90/account.json" ]]; then
  [[ -n "${ZEROSSL_ACME_EAB_KID:-}" ]] || fail "ZEROSSL_ACME_EAB_KID nao definido (necessario para registrar conta)."
  [[ -n "${ZEROSSL_ACME_EAB_HMAC_KEY:-}" ]] || fail "ZEROSSL_ACME_EAB_HMAC_KEY nao definido (necessario para registrar conta)."
fi'''

if old_validation in content:
    content = content.replace(old_validation, new_validation)
    print("validacao EAB ajustada")
else:
    print("AVISO: padrao de validacao EAB nao encontrado")

# 2b) register_account: pula se a conta ja existe
old_register = '''register_account() {
  install -d -m 750 -o root -g discovery-api "$ZEROSSL_ACME_HOME"
  "$ZEROSSL_ACME_SH" --home "$ZEROSSL_ACME_HOME" \\
    --register-account \\
    --server "$ZEROSSL_ACME_SERVER" \\
    -m "$ZEROSSL_ACME_EMAIL" \\
    --eab-kid "$ZEROSSL_ACME_EAB_KID" \\
    --eab-hmac-key "$ZEROSSL_ACME_EAB_HMAC_KEY" >/dev/null
}'''

new_register = '''register_account() {
  install -d -m 750 -o root -g discovery-api "$ZEROSSL_ACME_HOME"
  # Se a conta ja esta registrada, nao precisa de EAB novamente.
  if [[ -f "$ZEROSSL_ACME_HOME/ca/acme.zerossl.com/v2/DV90/account.key" || -f "$ZEROSSL_ACME_HOME/ca/acme.zerossl.com/v2/DV90/account.json" ]]; then
    log "Conta ACME ja registrada; pulando register-account."
    return 0
  fi
  "$ZEROSSL_ACME_SH" --home "$ZEROSSL_ACME_HOME" \\
    --register-account \\
    --server "$ZEROSSL_ACME_SERVER" \\
    -m "$ZEROSSL_ACME_EMAIL" \\
    --eab-kid "$ZEROSSL_ACME_EAB_KID" \\
    --eab-hmac-key "$ZEROSSL_ACME_EAB_HMAC_KEY" >/dev/null
}'''

if old_register in content:
    content = content.replace(old_register, new_register)
    print("register_account ajustado")
else:
    print("AVISO: padrao register_account nao encontrado")

with open(script, "w", encoding="utf-8") as fh:
    fh.write(content)
PYEOF

chmod 750 "$SCRIPT"
chown root:discovery-api "$SCRIPT"
bash -n "$SCRIPT" && log "sintaxe do script OK"

log "Correcao concluida."
