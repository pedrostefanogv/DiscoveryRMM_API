#!/usr/bin/env bash
set -euo pipefail

log() {
  printf '[selfupdate] %s %s\n' "$(date +%H:%M:%S)" "$*"
}

warn() {
  printf '[selfupdate][aviso] %s %s\n' "$(date +%H:%M:%S)" "$*" >&2
}

fail() {
  printf '[selfupdate][erro] %s %s\n' "$(date +%H:%M:%S)" "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || fail "Comando obrigatorio ausente: $1"
}

require_cmd git
require_cmd dotnet
require_cmd flock
require_cmd npm

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

if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
  fail "Nao execute este script como root. Use 'systemctl start discovery-selfupdate.service' ou 'sudo -u discovery-api /opt/discovery-ops/selfupdate-discovery-api.sh'."
fi

if [[ ! -x . ]]; then
  cd /
fi

DISCOVERY_API_BASE="${DISCOVERY_API_BASE:-/opt/discovery-api}"
DISCOVERY_API_SOURCE="${DISCOVERY_API_SOURCE:-$DISCOVERY_API_BASE/source}"
DISCOVERY_API_RELEASES="${DISCOVERY_API_RELEASES:-$DISCOVERY_API_BASE/releases}"
DISCOVERY_API_CURRENT="${DISCOVERY_API_CURRENT:-$DISCOVERY_API_BASE/current}"
DISCOVERY_API_PROJECT="${DISCOVERY_API_PROJECT:-src/Discovery.Api/Discovery.Api.csproj}"
DISCOVERY_GIT_REPO="${DISCOVERY_GIT_REPO:-}"
DISCOVERY_GIT_BRANCH="${DISCOVERY_GIT_BRANCH:-release}"
DISCOVERY_SITE_GIT_REPO="${DISCOVERY_SITE_GIT_REPO:-https://github.com/pedrostefanogv/DiscoveryRMM_Site}"
DISCOVERY_SITE_BASE="${DISCOVERY_SITE_BASE:-/opt/discovery-site}"
DISCOVERY_SITE_SOURCE="${DISCOVERY_SITE_SOURCE:-$DISCOVERY_SITE_BASE/source}"
DISCOVERY_SITE_RELEASES="${DISCOVERY_SITE_RELEASES:-$DISCOVERY_SITE_BASE/releases}"
DISCOVERY_SITE_CURRENT="${DISCOVERY_SITE_CURRENT:-$DISCOVERY_SITE_BASE/current}"
DISCOVERY_SITE_API_URL="${DISCOVERY_SITE_API_URL:-}"
DISCOVERY_SITE_REALTIME_PROVIDER="${DISCOVERY_SITE_REALTIME_PROVIDER:-both}"
DISCOVERY_SITE_NATS_ENABLED="${DISCOVERY_SITE_NATS_ENABLED:-true}"
DISCOVERY_SITE_NATS_URL="${DISCOVERY_SITE_NATS_URL:-}"
DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS="${DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS:-60000}"
if [[ -z "$DISCOVERY_SITE_NATS_URL" ]]; then
  nats_public_host="${DISCOVERY_SITE_NATS_PUBLIC_HOST:-${Authentication__Fido2__ServerDomain:-${Nats__ServerHostExternal:-}}}"
  nats_public_host="${nats_public_host#http://}"
  nats_public_host="${nats_public_host#https://}"
  nats_public_host="${nats_public_host%%/*}"
  if [[ -n "$nats_public_host" ]]; then
    DISCOVERY_SITE_NATS_URL="wss://${nats_public_host}/nats/"
  else
    DISCOVERY_SITE_NATS_URL="/nats/"
  fi
fi
DISCOVERY_KEEP_RELEASES="${DISCOVERY_KEEP_RELEASES:-5}"
DISCOVERY_DOTNET_RUNTIME="${DISCOVERY_DOTNET_RUNTIME:-}"
DISCOVERY_CLEAN_BUILD="${DISCOVERY_CLEAN_BUILD:-1}"

DETECTED_ARCH="$(detect_system_architecture)"
if DETECTED_DOTNET_RUNTIME="$(map_arch_to_dotnet_runtime "$DETECTED_ARCH")"; then
  :
else
  DETECTED_DOTNET_RUNTIME="linux-x64"
  log "Arquitetura nao mapeada (${DETECTED_ARCH:-desconhecida}); usando runtime padrao linux-x64"
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
[[ -n "$DISCOVERY_SITE_GIT_REPO" ]] || fail "DISCOVERY_SITE_GIT_REPO nao definido."

mkdir -p "$DISCOVERY_API_RELEASES"
mkdir -p "$DISCOVERY_SITE_RELEASES"

export GIT_TERMINAL_PROMPT=0

clone_or_fetch_repo() {
  local repo_url="$1"
  local repo_dir="$2"

  if [[ ! -d "$repo_dir/.git" ]]; then
    log "Repositorio nao encontrado. Clonando $repo_url em $repo_dir"
    mkdir -p "$(dirname "$repo_dir")"
    git clone --branch "$DISCOVERY_GIT_BRANCH" "$repo_url" "$repo_dir"
    return
  fi

  log "Buscando atualizacoes de $repo_dir"
  git -C "$repo_dir" fetch origin "$DISCOVERY_GIT_BRANCH"
}

cleanup_old_releases() {
  local releases_dir="$1"

  mapfile -t RELEASE_DIRS < <(ls -1dt "$releases_dir"/* 2>/dev/null || true)
  if (( ${#RELEASE_DIRS[@]} > DISCOVERY_KEEP_RELEASES )); then
    for old_release in "${RELEASE_DIRS[@]:DISCOVERY_KEEP_RELEASES}"; do
      # Corrige ownership caso a release tenha sido criada como root (deploy manual)
      sudo chown -R discovery-api:discovery-api "$old_release" 2>/dev/null || true
      rm -rf "$old_release" || sudo rm -rf "$old_release" || true
    done
  fi
}

ensure_tree_writable_by_current_user() {
  local tree_dir="$1"
  local label="$2"
  local current_user
  current_user="$(id -un)"

  [[ -d "$tree_dir" ]] || fail "Diretorio esperado nao encontrado para ${label}: $tree_dir"

  local mismatch_path
  mismatch_path="$(find "$tree_dir" -not -user "$current_user" -print -quit 2>/dev/null || true)"
  if [[ -n "$mismatch_path" ]]; then
    fail "Ownership inconsistente em ${label}: $mismatch_path. Corrija com 'chown -R ${current_user}:${current_user} $tree_dir' ou execute o fluxo oficial de update."
  fi

  [[ -w "$tree_dir" ]] || fail "Sem permissao de escrita em ${label}: $tree_dir"
}

clean_api_build_cache() {
  if [[ "${DISCOVERY_CLEAN_BUILD:-1}" != "1" ]]; then
    return
  fi
  ensure_tree_writable_by_current_user "$DISCOVERY_API_SOURCE" "source da API"
  log "Limpando cache de build da API (obj/ bin/)"
  find "$DISCOVERY_API_SOURCE" -maxdepth 4 \( -name obj -o -name bin \) -type d -exec rm -rf {} +
}

clean_site_build_cache() {
  if [[ "${DISCOVERY_CLEAN_BUILD:-1}" != "1" ]]; then
    return
  fi
  ensure_tree_writable_by_current_user "$DISCOVERY_SITE_SOURCE" "source do portal web"
  log "Limpando cache de build do portal web (node_modules/.cache e dist/)"
  rm -rf "$DISCOVERY_SITE_SOURCE/node_modules/.cache" "$DISCOVERY_SITE_SOURCE/dist"
}

# ── Merge RemoteAccess settings into /etc/discovery-api/discovery.env ─────

# Atualiza chaves críticas no env existente (mesmo se RemoteAccess já está configurado).
_patch_critical_env_keys() {
  local env_file="$1"
  if [[ ! -f "$env_file" ]]; then return; fi

  local tmp_file; tmp_file="$(mktemp)"
  cp "$env_file" "$tmp_file"

  _set_env_key() {
    local key="$1" value="$2"
    if grep -q "^${key}=" "$tmp_file" 2>/dev/null; then
      sed -i "s|^${key}=.*|${key}=${value}|" "$tmp_file"
    else
      printf '%s=%s\n' "$key" "$value" >> "$tmp_file"
    fi
  }

  _set_env_key "Authentication__Jwt__AccessTokenExpirationMinutes" "60"
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

  # So executa merge se ainda nao tem RemoteAccess no env (evita overwrite de customizacoes)
  if grep -q '^RemoteAccess__' "$env_file" 2>/dev/null; then
    _patch_critical_env_keys "$env_file"
    return
  fi

  # Gera chave JWT aleatoria se nao existir
  local jwt_key
  jwt_key="$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom 2>/dev/null | head -c 64 || true)"
  if [[ -z "$jwt_key" ]]; then
    jwt_key="discovery-nats-jwt-secret-dev"
    warn "Nao foi possivel gerar chave JWT aleatoria; usando fallback inseguro."
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

  if ! sudo install -m 640 -o root -g discovery-api "$tmp_file" "$env_file" 2>/dev/null; then
    warn "Falha ao instalar $env_file. Merge de RemoteAccess nao aplicado."
  fi
  rm -f "$tmp_file"
  log "Configuracoes RemoteAccess adicionadas ao $env_file"
}

publish_api_release() {
  local remote_rev="$1"
  local release_id="$(date +%Y%m%d%H%M%S)-${remote_rev:0:8}"
  local release_dir="$DISCOVERY_API_RELEASES/$release_id"

  mkdir -p "$release_dir"
  clean_api_build_cache
  dotnet publish "$DISCOVERY_API_SOURCE/$DISCOVERY_API_PROJECT" \
    -c Release \
    -r "$DISCOVERY_DOTNET_RUNTIME" \
    --self-contained false \
    -o "$release_dir" \
    /p:UseAppHost=true

  rm -f "$release_dir"/appsettings*.json || true
  [[ -x "$release_dir/Discovery.Api" ]] || fail "Binario Discovery.Api nao gerado na release $release_id"

  ln -sfn "$release_dir" "$DISCOVERY_API_CURRENT"
  log "Release ativa da API atualizada para $release_id"

  # ── Merge novas configuracoes no env (RemoteAccess, etc) ──────────────
  _merge_env_remote_access

  cleanup_old_releases "$DISCOVERY_API_RELEASES"
}

publish_site_release() {
  local remote_rev="$1"
  local release_id="$(date +%Y%m%d%H%M%S)-${remote_rev:0:8}"
  local release_dir="$DISCOVERY_SITE_RELEASES/$release_id"

  mkdir -p "$release_dir"
  clean_site_build_cache
  npm --prefix "$DISCOVERY_SITE_SOURCE" ci
  env \
    VITE_API_URL="$DISCOVERY_SITE_API_URL" \
    VITE_REALTIME_PROVIDER="$DISCOVERY_SITE_REALTIME_PROVIDER" \
    VITE_NATS_ENABLED="$DISCOVERY_SITE_NATS_ENABLED" \
    VITE_NATS_URL="$DISCOVERY_SITE_NATS_URL" \
    VITE_AGENT_OFFLINE_FALLBACK_MS="$DISCOVERY_SITE_AGENT_OFFLINE_FALLBACK_MS" \
    npm --prefix "$DISCOVERY_SITE_SOURCE" run build

  [[ -f "$DISCOVERY_SITE_SOURCE/dist/index.html" ]] || fail "Build do portal web nao gerou dist/index.html"

  cp -a "$DISCOVERY_SITE_SOURCE/dist/." "$release_dir/"
  find "$release_dir" -type d -exec chmod 755 {} +
  find "$release_dir" -type f -exec chmod 644 {} +
  ln -sfn "$release_dir" "$DISCOVERY_SITE_CURRENT"
  log "Release ativa do portal web atualizada para $release_id"
  cleanup_old_releases "$DISCOVERY_SITE_RELEASES"
}

clone_or_fetch_repo "$DISCOVERY_GIT_REPO" "$DISCOVERY_API_SOURCE"
clone_or_fetch_repo "$DISCOVERY_SITE_GIT_REPO" "$DISCOVERY_SITE_SOURCE"

# Atualiza tambem o repositorio do agent (se configurado)
DISCOVERY_AGENT_GIT_REPO="${DISCOVERY_AGENT_GIT_REPO:-}"
DISCOVERY_AGENT_SRC="${DISCOVERY_AGENT_SRC:-/opt/discovery-agent-src}"
if [[ -n "$DISCOVERY_AGENT_GIT_REPO" ]] && [[ -d "$DISCOVERY_AGENT_SRC/.git" ]]; then
  log "Atualizando repositorio do agent (fetch only)"
  git -C "$DISCOVERY_AGENT_SRC" fetch origin "$DISCOVERY_GIT_BRANCH" 2>/dev/null || log "Fetch do agent nao disponivel para branch '$DISCOVERY_GIT_BRANCH'"
fi

API_LOCAL_REV="$(git -C "$DISCOVERY_API_SOURCE" rev-parse HEAD 2>/dev/null || true)"
API_REMOTE_REV="$(git -C "$DISCOVERY_API_SOURCE" rev-parse "origin/$DISCOVERY_GIT_BRANCH")"
SITE_LOCAL_REV="$(git -C "$DISCOVERY_SITE_SOURCE" rev-parse HEAD 2>/dev/null || true)"
SITE_REMOTE_REV="$(git -C "$DISCOVERY_SITE_SOURCE" rev-parse "origin/$DISCOVERY_GIT_BRANCH")"

API_CHANGED=0
SITE_CHANGED=0
[[ "$API_LOCAL_REV" != "$API_REMOTE_REV" ]] && API_CHANGED=1
[[ "$SITE_LOCAL_REV" != "$SITE_REMOTE_REV" ]] && SITE_CHANGED=1

if [[ "$API_CHANGED" -eq 0 && "$SITE_CHANGED" -eq 0 ]]; then
  log "Sem atualizacoes no branch $DISCOVERY_GIT_BRANCH para API e portal web"
  exit 0
fi

# Guarda symlink anterior para rollback em caso de falha
API_ROLLBACK_RELEASE="" SITE_ROLLBACK_RELEASE=""
if [[ -L "$DISCOVERY_API_CURRENT" ]]; then
  API_ROLLBACK_RELEASE="$(readlink -f "$DISCOVERY_API_CURRENT" 2>/dev/null || true)"
fi
if [[ -L "$DISCOVERY_SITE_CURRENT" ]]; then
  SITE_ROLLBACK_RELEASE="$(readlink -f "$DISCOVERY_SITE_CURRENT" 2>/dev/null || true)"
fi

API_PUBLISHED=0
SITE_PUBLISHED=0
API_RESTARTED=0

if [[ "$API_CHANGED" -eq 1 ]]; then
  log "Atualizacao detectada na API. Aplicando commit $API_REMOTE_REV"
  git -C "$DISCOVERY_API_SOURCE" checkout "$DISCOVERY_GIT_BRANCH"
  git -C "$DISCOVERY_API_SOURCE" reset --hard "origin/$DISCOVERY_GIT_BRANCH"
  git -C "$DISCOVERY_API_SOURCE" clean -fd 2>/dev/null || true
  if publish_api_release "$API_REMOTE_REV"; then
    API_PUBLISHED=1
  else
    warn "Falha ao publicar release da API. Tentando rollback."
    if [[ -n "$API_ROLLBACK_RELEASE" && -d "$API_ROLLBACK_RELEASE" ]]; then
      ln -sfn "$API_ROLLBACK_RELEASE" "$DISCOVERY_API_CURRENT"
      log "Rollback da API para release anterior: $API_ROLLBACK_RELEASE"
    fi
  fi
else
  log "Sem atualizacoes na API"
fi

if [[ "$SITE_CHANGED" -eq 1 ]]; then
  log "Atualizacao detectada no portal web. Aplicando commit $SITE_REMOTE_REV"
  git -C "$DISCOVERY_SITE_SOURCE" checkout "$DISCOVERY_GIT_BRANCH"
  git -C "$DISCOVERY_SITE_SOURCE" reset --hard "origin/$DISCOVERY_GIT_BRANCH"
  git -C "$DISCOVERY_SITE_SOURCE" clean -fd 2>/dev/null || true
  if publish_site_release "$SITE_REMOTE_REV"; then
    SITE_PUBLISHED=1
  else
    warn "Falha ao publicar release do portal web. Tentando rollback."
    if [[ -n "$SITE_ROLLBACK_RELEASE" && -d "$SITE_ROLLBACK_RELEASE" ]]; then
      ln -sfn "$SITE_ROLLBACK_RELEASE" "$DISCOVERY_SITE_CURRENT"
      log "Rollback do portal web para release anterior: $SITE_ROLLBACK_RELEASE"
    fi
  fi
else
  log "Sem atualizacoes no portal web"
fi

# Reinicia servicos se houve alteracao publicada
if [[ "$API_PUBLISHED" -eq 1 ]]; then
  log "Reiniciando discovery-api com a nova release"
  if sudo systemctl restart discovery-api 2>/dev/null; then
    API_RESTARTED=1
    # Health check rapido pos-restart
    sleep 3
    if curl -fsS "http://127.0.0.1:8080/health" >/dev/null 2>&1; then
      log "discovery-api respondeu com sucesso apos restart."
    else
      warn "discovery-api nao respondeu ao health check apos restart. Aguardando mais 10s..."
      sleep 10
      if ! curl -fsS "http://127.0.0.1:8080/health" >/dev/null 2>&1; then
        warn "discovery-api ainda sem resposta. Tentando rollback."
        if [[ -n "$API_ROLLBACK_RELEASE" && -d "$API_ROLLBACK_RELEASE" ]]; then
          ln -sfn "$API_ROLLBACK_RELEASE" "$DISCOVERY_API_CURRENT"
          sudo systemctl restart discovery-api 2>/dev/null || true
          log "Rollback da API executado devido a falha no health check."
        fi
      fi
    fi
  else
    warn "Nao foi possivel reiniciar discovery-api (sem sudo?)."
  fi
fi

if [[ "$SITE_PUBLISHED" -eq 1 ]]; then
  if sudo nginx -t >/dev/null 2>&1; then
    sudo systemctl reload nginx 2>/dev/null || log "Nao foi possivel recarregar nginx (sem sudo?)."
  else
    warn "Configuracao do nginx invalida; pulando reload."
  fi
fi

# Dispara rebuild do agent se o repo foi atualizado
if [[ -n "$DISCOVERY_AGENT_GIT_REPO" ]] && [[ -d "$DISCOVERY_AGENT_SRC/.git" ]]; then
  local agent_local agent_remote
  agent_local="$(git -C "$DISCOVERY_AGENT_SRC" rev-parse HEAD 2>/dev/null || true)"
  agent_remote="$(git -C "$DISCOVERY_AGENT_SRC" rev-parse "origin/$DISCOVERY_GIT_BRANCH" 2>/dev/null || true)"
  if [[ -n "$agent_local" && -n "$agent_remote" && "$agent_local" != "$agent_remote" ]]; then
    git -C "$DISCOVERY_AGENT_SRC" checkout "$DISCOVERY_GIT_BRANCH" 2>/dev/null || true
    git -C "$DISCOVERY_AGENT_SRC" reset --hard "origin/$DISCOVERY_GIT_BRANCH" 2>/dev/null || true
    log "Agent atualizado; disparando rebuild via API..."
    if sudo systemctl is-active --quiet discovery-api.service 2>/dev/null; then
      curl -s -X POST "http://127.0.0.1:8080/api/v1/agent-updates/build/rebuild" \
        -H "Content-Type: application/json" \
        --max-time 300 >/dev/null 2>&1 \
        && log "Rebuild do agent disparado com sucesso." \
        || warn "Falha ao disparar rebuild do agent via API."
    fi
  fi
fi

log "Self-update concluido com sucesso"
