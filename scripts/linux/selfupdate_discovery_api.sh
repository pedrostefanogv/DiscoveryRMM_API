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

DISCOVERY_API_BASE="${DISCOVERY_API_BASE:-/opt/discovery-api}"
DISCOVERY_API_SOURCE="${DISCOVERY_API_SOURCE:-$DISCOVERY_API_BASE/source}"
DISCOVERY_API_RELEASES="${DISCOVERY_API_RELEASES:-$DISCOVERY_API_BASE/releases}"
DISCOVERY_API_CURRENT="${DISCOVERY_API_CURRENT:-$DISCOVERY_API_BASE/current}"
DISCOVERY_API_PROJECT="${DISCOVERY_API_PROJECT:-src/Discovery.Api/Discovery.Api.csproj}"
DISCOVERY_GIT_REPO="${DISCOVERY_GIT_REPO:-}"
DISCOVERY_GIT_BRANCH="${DISCOVERY_GIT_BRANCH:-main}"
DISCOVERY_GIT_TOKEN_FILE="${DISCOVERY_GIT_TOKEN_FILE:-/etc/discovery-api/github.token}"
DISCOVERY_KEEP_RELEASES="${DISCOVERY_KEEP_RELEASES:-5}"
DISCOVERY_DOTNET_RUNTIME="${DISCOVERY_DOTNET_RUNTIME:-linux-x64}"

LOCK_FILE="/opt/discovery-ops/selfupdate.lock"
mkdir -p "$(dirname "$LOCK_FILE")"
exec 9>"$LOCK_FILE"
if ! flock -n 9; then
  log "Outro processo de self-update ja esta em execucao."
  exit 0
fi

[[ -n "$DISCOVERY_GIT_REPO" ]] || fail "DISCOVERY_GIT_REPO nao definido."

mkdir -p "$DISCOVERY_API_RELEASES"

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
  exit 0
fi

log "Atualizacao detectada. Aplicando commit $REMOTE_REV"
git -C "$DISCOVERY_API_SOURCE" checkout "$DISCOVERY_GIT_BRANCH"
git -C "$DISCOVERY_API_SOURCE" reset --hard "origin/$DISCOVERY_GIT_BRANCH"

RELEASE_ID="$(date +%Y%m%d%H%M%S)-${REMOTE_REV:0:8}"
NEW_RELEASE="$DISCOVERY_API_RELEASES/$RELEASE_ID"
mkdir -p "$NEW_RELEASE"

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

mapfile -t RELEASE_DIRS < <(ls -1dt "$DISCOVERY_API_RELEASES"/* 2>/dev/null || true)
if (( ${#RELEASE_DIRS[@]} > DISCOVERY_KEEP_RELEASES )); then
  for old_release in "${RELEASE_DIRS[@]:DISCOVERY_KEEP_RELEASES}"; do
    rm -rf "$old_release"
  done
fi

log "Self-update concluido com sucesso"
