#!/usr/bin/env bash
set -euo pipefail

KEEP=5

cleanup_dir() {
  local releases_dir="$1"
  mapfile -t dirs < <(ls -1dt "$releases_dir"/* 2>/dev/null || true)
  local total="${#dirs[@]}"
  local to_delete=$(( total - KEEP ))

  echo "[$releases_dir] Total: $total | Mantendo: $KEEP | Removendo: $to_delete"
  if (( total <= KEEP )); then
    echo "  Nada a remover."
    return
  fi

  echo "  Mantendo:"
  for d in "${dirs[@]:0:$KEEP}"; do
    echo "    $(basename "$d")  owner=$(stat -c '%U' "$d")"
  done

  echo "  Removendo:"
  for old in "${dirs[@]:$KEEP}"; do
    chown -R discovery-api:discovery-api "$old" 2>/dev/null || true
    if rm -rf "$old"; then
      echo "    removido: $(basename "$old")"
    else
      echo "    ERRO ao remover: $(basename "$old")"
    fi
  done
}

cleanup_dir /opt/discovery-api/releases
cleanup_dir /opt/discovery-site/releases

echo ""
echo "=== Resultado ==="
echo "API releases restantes : $(ls /opt/discovery-api/releases | wc -l)"
echo "Site releases restantes: $(ls /opt/discovery-site/releases | wc -l)"
df -h / | tail -1
