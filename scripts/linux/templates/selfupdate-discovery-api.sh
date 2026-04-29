#!/usr/bin/env bash
set -euo pipefail

log() {
  printf '[selfupdate] %s\n' "$*"
}

fail() {
  printf '[selfupdate][erro] %s\n' "$*" >&2
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

DISCOVERY_API_BASE="${DISCOVERY_API_BASE:-/opt/discovery-api}"
DISCOVERY_API_SOURCE="${DISCOVERY_API_SOURCE:-$DISCOVERY_API_BASE/source}"
DISCOVERY_API_RELEASES="${DISCOVERY_API_RELEASES:-$DISCOVERY_API_BASE/releases}"
DISCOVERY_API_CURRENT="${DISCOVERY_API_CURRENT:-$DISCOVERY_API_BASE/current}"
DISCOVERY_API_PROJECT="${DISCOVERY_API_PROJECT:-src/Discovery.Api/Discovery.Api.csproj}"
DISCOVERY_GIT_REPO="${DISCOVERY_GIT_REPO:-}"
DISCOVERY_GIT_BRANCH="${DISCOVERY_GIT_BRANCH:-release}"
DISCOVERY_GIT_TOKEN_FILE="${DISCOVERY_GIT_TOKEN_FILE:-/etc/discovery-api/github.token}"
DISCOVERY_SITE_GIT_REPO="${DISCOVERY_SITE_GIT_REPO:-https://github.com/pedrostefanogv/DiscoveryRMM_Site}"
DISCOVERY_SITE_BASE="${DISCOVERY_SITE_BASE:-/opt/discovery-site}"
DISCOVERY_SITE_SOURCE="${DISCOVERY_SITE_SOURCE:-$DISCOVERY_SITE_BASE/source}"
DISCOVERY_SITE_RELEASES="${DISCOVERY_SITE_RELEASES:-$DISCOVERY_SITE_BASE/releases}"
DISCOVERY_SITE_CURRENT="${DISCOVERY_SITE_CURRENT:-$DISCOVERY_SITE_BASE/current}"
DISCOVERY_SITE_API_URL="${DISCOVERY_SITE_API_URL:-}"
DISCOVERY_SITE_REALTIME_PROVIDER="${DISCOVERY_SITE_REALTIME_PROVIDER:-both}"
DISCOVERY_SITE_NATS_ENABLED="${DISCOVERY_SITE_NATS_ENABLED:-true}"
DISCOVERY_SITE_NATS_URL="${DISCOVERY_SITE_NATS_URL:-}"
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

GITHUB_PAT=""
if [[ -f "$DISCOVERY_GIT_TOKEN_FILE" ]]; then
  GITHUB_PAT="$(tr -d '\r\n' < "$DISCOVERY_GIT_TOKEN_FILE")"
fi

ASKPASS_FILE=""
cleanup() {
  if [[ -n "$ASKPASS_FILE" ]]; then
    rm -f "$ASKPASS_FILE"
  fi
}
trap cleanup EXIT

if [[ -n "$GITHUB_PAT" ]]; then
  ASKPASS_FILE="$(mktemp)"
  cat > "$ASKPASS_FILE" <<'EOF'
#!/usr/bin/env sh
case "$1" in
  *Username*) printf '%s\n' "x-access-token" ;;
  *Password*) printf '%s\n' "$GITHUB_PAT" ;;
  *) printf '\n' ;;
esac
EOF
  chmod 700 "$ASKPASS_FILE"
  export GIT_ASKPASS="$ASKPASS_FILE"
  export GIT_TERMINAL_PROMPT=0
  export GITHUB_PAT
else
  log "Token GitHub nao informado; seguindo sem autenticacao (repo publico)"
  export GIT_TERMINAL_PROMPT=0
fi

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
      rm -rf "$old_release"
    done
  fi
}

publish_api_release() {
  local remote_rev="$1"
  local release_id="$(date +%Y%m%d%H%M%S)-${remote_rev:0:8}"
  local release_dir="$DISCOVERY_API_RELEASES/$release_id"

  mkdir -p "$release_dir"
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
  cleanup_old_releases "$DISCOVERY_API_RELEASES"
}

publish_site_release() {
  local remote_rev="$1"
  local release_id="$(date +%Y%m%d%H%M%S)-${remote_rev:0:8}"
  local release_dir="$DISCOVERY_SITE_RELEASES/$release_id"

  mkdir -p "$release_dir"
  npm --prefix "$DISCOVERY_SITE_SOURCE" ci
  env \
    VITE_API_URL="$DISCOVERY_SITE_API_URL" \
    VITE_REALTIME_PROVIDER="$DISCOVERY_SITE_REALTIME_PROVIDER" \
    VITE_NATS_ENABLED="$DISCOVERY_SITE_NATS_ENABLED" \
    VITE_NATS_URL="$DISCOVERY_SITE_NATS_URL" \
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

if [[ "$API_CHANGED" -eq 1 ]]; then
  log "Atualizacao detectada na API. Aplicando commit $API_REMOTE_REV"
  git -C "$DISCOVERY_API_SOURCE" checkout "$DISCOVERY_GIT_BRANCH"
  git -C "$DISCOVERY_API_SOURCE" reset --hard "origin/$DISCOVERY_GIT_BRANCH"
  publish_api_release "$API_REMOTE_REV"
else
  log "Sem atualizacoes na API"
fi

if [[ "$SITE_CHANGED" -eq 1 ]]; then
  log "Atualizacao detectada no portal web. Aplicando commit $SITE_REMOTE_REV"
  git -C "$DISCOVERY_SITE_SOURCE" checkout "$DISCOVERY_GIT_BRANCH"
  git -C "$DISCOVERY_SITE_SOURCE" reset --hard "origin/$DISCOVERY_GIT_BRANCH"
  publish_site_release "$SITE_REMOTE_REV"
else
  log "Sem atualizacoes no portal web"
fi

log "Self-update concluido com sucesso"
