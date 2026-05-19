#!/usr/bin/env python3
"""Patch /opt/discovery-ops/selfupdate-discovery-api.sh com as 3 correcoes de cleanup."""
import sys

SCRIPT = '/opt/discovery-ops/selfupdate-discovery-api.sh'

with open(SCRIPT, 'r') as f:
    content = f.read()

original = content

# ──────────────────────────────────────────────────────────────
# 1. Adicionar warn() logo antes de fail()
# ──────────────────────────────────────────────────────────────
WARN_FN = '''\
warn() {
  printf '[selfupdate][aviso] %s\\n' "$*" >&2
}

'''
if 'warn()' not in content:
    content = content.replace('fail() {', WARN_FN + 'fail() {', 1)
    print('[1] warn(): inserido')
else:
    print('[1] warn(): ja existe, pulando')

# ──────────────────────────────────────────────────────────────
# 2. Tornar cleanup_old_releases robusto a falhas de permissao
# ──────────────────────────────────────────────────────────────
OLD_CLEANUP = '''\
cleanup_old_releases() {
  local releases_dir="$1"

  mapfile -t RELEASE_DIRS < <(ls -1dt "$releases_dir"/* 2>/dev/null || true)
  if (( ${#RELEASE_DIRS[@]} > DISCOVERY_KEEP_RELEASES )); then
    for old_release in "${RELEASE_DIRS[@]:DISCOVERY_KEEP_RELEASES}"; do
      rm -rf "$old_release"
    done
  fi
}'''

NEW_CLEANUP = '''\
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
      warn "Sem permissao para remover: $(basename "$old_release") -- corrija com: chown -R discovery-api:discovery-api $old_release"
    fi
  done
}'''

if OLD_CLEANUP in content:
    content = content.replace(OLD_CLEANUP, NEW_CLEANUP, 1)
    print('[2] cleanup_old_releases: corrigido')
elif NEW_CLEANUP in content:
    print('[2] cleanup_old_releases: ja corrigido, pulando')
else:
    print('[2] ERRO: padrao de cleanup_old_releases nao encontrado', file=sys.stderr)
    sys.exit(1)

# ──────────────────────────────────────────────────────────────
# 3. Chamar cleanup no early-exit "sem atualizacoes"
# ──────────────────────────────────────────────────────────────
OLD_EXIT = '''\
if [[ "$API_CHANGED" -eq 0 && "$SITE_CHANGED" -eq 0 ]]; then
  log "Sem atualizacoes no branch $DISCOVERY_GIT_BRANCH para API e portal web"
  exit 0
fi'''

NEW_EXIT = '''\
if [[ "$API_CHANGED" -eq 0 && "$SITE_CHANGED" -eq 0 ]]; then
  log "Sem atualizacoes no branch $DISCOVERY_GIT_BRANCH para API e portal web"
  cleanup_old_releases "$DISCOVERY_API_RELEASES"
  cleanup_old_releases "$DISCOVERY_SITE_RELEASES"
  exit 0
fi'''

if OLD_EXIT in content:
    content = content.replace(OLD_EXIT, NEW_EXIT, 1)
    print('[3] early-exit cleanup: inserido')
elif 'cleanup_old_releases "$DISCOVERY_API_RELEASES"' in content and NEW_EXIT in content:
    print('[3] early-exit cleanup: ja existe, pulando')
else:
    print('[3] ERRO: padrao de early-exit nao encontrado', file=sys.stderr)
    sys.exit(1)

if content == original:
    print('Nenhuma alteracao necessaria.')
    sys.exit(0)

with open(SCRIPT, 'w') as f:
    f.write(content)

print('Script atualizado com sucesso.')
