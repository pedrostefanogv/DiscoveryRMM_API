#!/usr/bin/env bash
set -euo pipefail

log() {
  printf '[selfupdate] %s\n' "$*"
}

warn() {
  printf '[selfupdate][aviso] %s\n' "$*" >&2
}

fail() {
  printf '[selfupdate][erro] %s\n' "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || fail "Comando obrigatorio ausente: $1"
}

# ── Merge RemoteAccess settings into /etc/discovery-api/discovery.env ─────

# Atualiza chaves críticas no env existente (mesmo se RemoteAccess já está configurado).
# Garante que correções de segurança/configuração sejam aplicadas em updates.
_patch_critical_env_keys() {
  local env_file="$1"
  if [[ ! -f "$env_file" ]]; then return; fi

  local tmp_file; tmp_file="$(mktemp)"
  cp "$env_file" "$tmp_file"

  # Função auxiliar: define ou atualiza uma chave no env
  _set_env_key() {
    local key="$1" value="$2"
    if grep -q "^${key}=" "$tmp_file" 2>/dev/null; then
      sed -i "s|^${key}=.*|${key}=${value}|" "$tmp_file"
    else
      printf '%s=%s\n' "$key" "$value" >> "$tmp_file"
    fi
  }

  # Correções críticas aplicadas em todo self-update:
  # - Access token TTL: 30 → 60 min (reduz renovações e 401 em sessões longas)
  _set_env_key "Authentication__Jwt__AccessTokenExpirationMinutes" "60"
  # - 1 sessão remota por agente (força sobreposição via flag Force)
  _set_env_key "RemoteAccess__MaxConcurrentSessionsPerAgent" "1"

  if command -v sudo >/dev/null 2>&1; then
    sudo install -m 640 -o root -g discovery-api "$tmp_file" "$env_file" 2>/dev/null \
      || install -m 640 "$tmp_file" "$env_file" 2>/dev/null \
      || warn "Falha ao aplicar patches criticos em $env_file"
  else
    install -m 640 "$tmp_file" "$env_file" 2>/dev/null \
      || warn "Falha ao aplicar patches criticos em $env_file"
  fi

  rm -f "$tmp_file"
  log "Patches criticos aplicados ao $env_file (AccessTokenTTL=60, MaxSessionsPerAgent=1)"
}

_merge_env_remote_access() {
  local env_file="/etc/discovery-api/discovery.env"
  if [[ ! -f "$env_file" ]]; then
    log "Arquivo $env_file nao encontrado. Pulando merge de RemoteAccess."
    return
  fi

  # Só executa merge se ainda não tem RemoteAccess no env (evita overwrite de customizações)
  if grep -q '^RemoteAccess__' "$env_file" 2>/dev/null; then
    log "RemoteAccess ja configurado no $env_file; pulando merge."
    _patch_critical_env_keys "$env_file"
    return
  fi

  # Gera chave JWT aleatoria se nao existir
  local jwt_key
  jwt_key="$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom 2>/dev/null | head -c 64 || true)"
  if [[ -z "$jwt_key" ]]; then
    jwt_key="$(openssl rand -base64 48 2>/dev/null | tr -d '\n/+=' | head -c 64 || true)"
  fi
  if [[ -z "$jwt_key" ]]; then
    jwt_key="discovery-nats-jwt-secret-dev"
    warn "Nao foi possivel gerar chave JWT aleatoria; usando fallback inseguro. Configure RemoteAccess__Nats__JwtSigningKey manualmente."
  fi

  log "Adicionando configuracoes RemoteAccess ao $env_file"
  local tmp_file; tmp_file="$(mktemp)"
  cp "$env_file" "$tmp_file"

  cat >> "$tmp_file" <<'REMOTEACCESS_EOF'
RemoteAccess__Enabled=true
RemoteAccess__DefaultTtlMinutes=30
RemoteAccess__MaxSessionDurationMinutes=120
RemoteAccess__MaxConcurrentSessionsPerAgent=1
RemoteAccess__MaxConcurrentSessionsPerUser=5
REMOTEACCESS_EOF
  printf 'RemoteAccess__Nats__JwtSigningKey=%s\n' "$jwt_key" >> "$tmp_file"
  cat >> "$tmp_file" <<'REMOTEACCESS_EOF'
RemoteAccess__Nats__FrameSubjectPrefix=remote.session
RemoteAccess__Nats__MaxPayloadBytes=2097152
RemoteAccess__Nats__ExpirationCheckIntervalSeconds=15
RemoteAccess__WebRtc__Enabled=true
RemoteAccess__WebRtc__StunUrls__0=stun:stun.l.google.com:19302
RemoteAccess__WebRtc__TurnCredentialTtlMinutes=60
RemoteAccess__WebRtc__IceTimeoutSeconds=5
RemoteAccess__Quality__DefaultProfile=high
RemoteAccess__Quality__AdaptiveEnabled=true
RemoteAccess__Quality__MinFps=5
RemoteAccess__Quality__MaxFps=30
RemoteAccess__Quality__DefaultCodec=auto
RemoteAccess__Recording__Enabled=true
RemoteAccess__Recording__DefaultOn=false
RemoteAccess__Recording__StorageProvider=Local
RemoteAccess__Recording__Local__BasePath=/var/discovery/recordings
RemoteAccess__Recording__Local__MaxDiskUsageGb=50
REMOTEACCESS_EOF

  # Usa sudo para instalar com as mesmas permissões (640 root:discovery-api)
  if command -v sudo >/dev/null 2>&1; then
    sudo install -m 640 -o root -g discovery-api "$tmp_file" "$env_file" 2>/dev/null || {
      warn "Nao foi possivel usar sudo para instalar $env_file; tentando sem..."
      install -m 640 "$tmp_file" "$env_file" 2>/dev/null || {
        warn "Falha ao instalar $env_file. Merge de RemoteAccess nao aplicado."
        rm -f "$tmp_file"
        return
      }
    }
  else
    install -m 640 "$tmp_file" "$env_file" 2>/dev/null || {
      warn "Falha ao instalar $env_file. Merge de RemoteAccess nao aplicado."
      rm -f "$tmp_file"
      return
    }
  fi

  rm -f "$tmp_file"
  log "Configuracoes RemoteAccess adicionadas ao $env_file"
}

detect_system_architecture() {
  local arch=""

  if command -v dpkg >/dev/null 2>&1; then
    arch="$(dpkg --print-architecture 2>/dev/null || true)"
  fi

  if [[ -z "$arch" ]]; then
    arch="$(uname -m 2>/dev/null || true)"
  fi

  printf '%s' "$arch"
}

map_arch_to_dotnet_runtime() {
  local arch_raw="${1:-}"
  local arch
  arch="$(printf '%s' "$arch_raw" | tr '[:upper:]' '[:lower:]')"

  case "$arch" in
    amd64|x86_64)
      printf 'linux-x64'
      ;;
    arm64|aarch64)
      printf 'linux-arm64'
      ;;
    *)
      return 1
      ;;
  esac
}

validate_dotnet_runtime() {
  local runtime="$1"
  case "$runtime" in
    linux-x64|linux-arm64)
      return
      ;;
    *)
      fail "DISCOVERY_DOTNET_RUNTIME invalido: $runtime (use linux-x64 ou linux-arm64)"
      ;;
  esac
}

require_cmd git
require_cmd dotnet
require_cmd flock

DISCOVERY_API_BASE="${DISCOVERY_API_BASE:-/opt/discovery-api}"
DISCOVERY_API_SOURCE="${DISCOVERY_API_SOURCE:-$DISCOVERY_API_BASE/source}"
DISCOVERY_API_RELEASES="${DISCOVERY_API_RELEASES:-$DISCOVERY_API_BASE/releases}"
DISCOVERY_API_CURRENT="${DISCOVERY_API_CURRENT:-$DISCOVERY_API_BASE/current}"
DISCOVERY_API_PROJECT="${DISCOVERY_API_PROJECT:-src/Discovery.Api/Discovery.Api.csproj}"
DISCOVERY_GIT_REPO="${DISCOVERY_GIT_REPO:-}"
DISCOVERY_GIT_BRANCH="${DISCOVERY_GIT_BRANCH:-main}"

DISCOVERY_KEEP_RELEASES="${DISCOVERY_KEEP_RELEASES:-5}"
DISCOVERY_DOTNET_RUNTIME="${DISCOVERY_DOTNET_RUNTIME:-}"
DISCOVERY_CLEAN_BUILD="${DISCOVERY_CLEAN_BUILD:-1}"

DETECTED_ARCH="$(detect_system_architecture)"
if DETECTED_DOTNET_RUNTIME="$(map_arch_to_dotnet_runtime "$DETECTED_ARCH")"; then
  :
else
  DETECTED_DOTNET_RUNTIME="linux-x64"
  warn "Arquitetura nao mapeada (${DETECTED_ARCH:-desconhecida}); usando runtime padrao linux-x64"
fi

if [[ -z "$DISCOVERY_DOTNET_RUNTIME" ]]; then
  DISCOVERY_DOTNET_RUNTIME="$DETECTED_DOTNET_RUNTIME"
fi
validate_dotnet_runtime "$DISCOVERY_DOTNET_RUNTIME"

LOCK_FILE="/opt/discovery-ops/selfupdate.lock"
mkdir -p "$(dirname "$LOCK_FILE")"
exec 9>"$LOCK_FILE"
if ! flock -n 9; then
  log "Outro processo de self-update ja esta em execucao."
  exit 0
fi

[[ -n "$DISCOVERY_GIT_REPO" ]] || fail "DISCOVERY_GIT_REPO nao definido."

mkdir -p "$DISCOVERY_API_RELEASES"

export GIT_TERMINAL_PROMPT=0

cleanup_old_releases() {
  local releases_dir="$1"

  mapfile -t RELEASE_DIRS < <(ls -1dt "$releases_dir"/* 2>/dev/null || true)
  if (( ${#RELEASE_DIRS[@]} <= DISCOVERY_KEEP_RELEASES )); then
    return
  fi
  log "Removendo $(( ${#RELEASE_DIRS[@]} - DISCOVERY_KEEP_RELEASES )) release(s) antiga(s) em $releases_dir"
  for old_release in "${RELEASE_DIRS[@]:DISCOVERY_KEEP_RELEASES}"; do
    if rm -rf "$old_release" 2>/dev/null; then
      log "Release antiga removida: $(basename "$old_release")"
    else
      warn "Sem permissao para remover release antiga: $(basename "$old_release") — corrija com: chown -R discovery-api:discovery-api $old_release"
    fi
  done
}

clean_api_build_cache() {
  if [[ "${DISCOVERY_CLEAN_BUILD:-1}" != "1" ]]; then
    return
  fi
  log "Limpando cache de build da API (obj/ bin/)"
  find "$DISCOVERY_API_SOURCE" -maxdepth 4 \( -name obj -o -name bin \) -type d -exec rm -rf {} + 2>/dev/null || true
}

if [[ ! -d "$DISCOVERY_API_SOURCE/.git" ]]; then
  log "Repositorio da API nao encontrado. Clonando em $DISCOVERY_API_SOURCE"
  mkdir -p "$(dirname "$DISCOVERY_API_SOURCE")"
  git clone --branch "$DISCOVERY_GIT_BRANCH" "$DISCOVERY_GIT_REPO" "$DISCOVERY_API_SOURCE"
else
  log "Buscando atualizacoes do repositorio da API"
  git -C "$DISCOVERY_API_SOURCE" fetch origin "$DISCOVERY_GIT_BRANCH"
fi

LOCAL_REV="$(git -C "$DISCOVERY_API_SOURCE" rev-parse HEAD 2>/dev/null || true)"
REMOTE_REV="$(git -C "$DISCOVERY_API_SOURCE" rev-parse "origin/$DISCOVERY_GIT_BRANCH")"

if [[ "$LOCAL_REV" == "$REMOTE_REV" ]]; then
  log "Sem atualizacoes no branch $DISCOVERY_GIT_BRANCH"
  cleanup_old_releases "$DISCOVERY_API_RELEASES"
  exit 0
fi

log "Atualizacao detectada. Aplicando commit $REMOTE_REV"
git -C "$DISCOVERY_API_SOURCE" checkout "$DISCOVERY_GIT_BRANCH"
git -C "$DISCOVERY_API_SOURCE" reset --hard "origin/$DISCOVERY_GIT_BRANCH"

RELEASE_ID="$(date +%Y%m%d%H%M%S)-${REMOTE_REV:0:8}"
NEW_RELEASE="$DISCOVERY_API_RELEASES/$RELEASE_ID"
mkdir -p "$NEW_RELEASE"

clean_api_build_cache
dotnet publish "$DISCOVERY_API_SOURCE/$DISCOVERY_API_PROJECT" \
  -c Release \
  -r "$DISCOVERY_DOTNET_RUNTIME" \
  --self-contained false \
  -o "$NEW_RELEASE" \
  /p:UseAppHost=true

rm -f "$NEW_RELEASE"/appsettings*.json || true

[[ -x "$NEW_RELEASE/Discovery.Api" ]] || fail "Binario Discovery.Api nao gerado na release $RELEASE_ID"

ln -sfn "$NEW_RELEASE" "$DISCOVERY_API_CURRENT"
log "Release ativa atualizada para $RELEASE_ID"

# ── Atualiza environment file com novas chaves (RemoteAccess, etc) ─────
_merge_env_remote_access

# ── Reinicia API para carregar novo binario + novas configuracoes ──────
if systemctl list-unit-files discovery-api.service >/dev/null 2>&1; then
  if systemctl is-active --quiet discovery-api.service 2>/dev/null; then
    log "Reiniciando discovery-api para aplicar novo release..."
    systemctl restart discovery-api.service 2>/dev/null || {
      warn "Falha ao reiniciar discovery-api via systemctl; tentando reload..."
      systemctl reload-or-restart discovery-api.service 2>/dev/null || \
        warn "Nao foi possivel reiniciar discovery-api. Reinicie manualmente."
    }
  else
    log "discovery-api nao esta rodando; iniciando..."
    systemctl start discovery-api.service 2>/dev/null || \
      warn "Nao foi possivel iniciar discovery-api."
  fi
fi

cleanup_old_releases "$DISCOVERY_API_RELEASES"

log "Self-update concluido com sucesso"
