#!/usr/bin/env bash
# discovery-api runtime guard
#
# Detecta "hot upgrade" de dependencias do framework/nativas sob um processo que ja
# estava em execucao. Se /proc/<pid>/maps aponta para arquivos REMOVIDOS (pacote
# atualizado e diretorio antigo apagado), qualquer assembly carregada SOB DEMANDA
# falha com FileNotFoundException - mesmo que o processo continue "vivo" e o
# /health responda 200.
#
# Incidente 2026-11-02: "apt upgrade" trocou dotnet-runtime-10.0 10.0.13 -> 10.0.14
# as 09:04 enquanto a API rodava desde 09:02. O diretorio
# .../shared/Microsoft.NETCore.App/10.0.13 foi removido; a primeira assembly
# carregada sob demanda (System.Runtime.Serialization.Primitives, pedida pela
# Fido2NetLib em AssertionOptions.ToJson) falhou e o login por MFA/FIDO2 passou a
# devolver HTTP 400 ate o servico ser reiniciado.
#
# Uso:
#   discovery-runtime-guard                 # verifica e reinicia se necessario
#   DISCOVERY_GUARD_DRY_RUN=1 ...          # apenas relata (exit 10 se obsoleto)
#   DISCOVERY_GUARD_PID=<pid> ...          # inspeciona um PID arbitrario, nunca reinicia
#   DISCOVERY_GUARD_VERBOSE=1 ...          # loga tambem o caso saudavel
#   DISCOVERY_GUARD_COOLDOWN_SECONDS=900   # intervalo minimo entre restarts
set -uo pipefail

UNIT="${DISCOVERY_API_UNIT:-discovery-api}"
# Diretorios cujo conteudo e carregado sob demanda. Inclui o framework .NET e as
# releases da API (um update troca o symlink "current" e pode remover a release
# antiga enquanto o processo ainda roda nela).
PATH_RE="${DISCOVERY_GUARD_PATH_RE:-/usr/lib/dotnet/shared/|/usr/share/dotnet/shared/|/opt/discovery-api/releases/}"
DRY_RUN="${DISCOVERY_GUARD_DRY_RUN:-0}"
VERBOSE="${DISCOVERY_GUARD_VERBOSE:-0}"
INSPECT_PID="${DISCOVERY_GUARD_PID:-}"
STATE_DIR="${DISCOVERY_GUARD_STATE_DIR:-/run/discovery-runtime-guard}"
COOLDOWN_SECONDS="${DISCOVERY_GUARD_COOLDOWN_SECONDS:-900}"

log() { echo "[runtime-guard] $*"; }

mkdir -p "$STATE_DIR" 2>/dev/null || STATE_DIR="/tmp/discovery-runtime-guard"
mkdir -p "$STATE_DIR" 2>/dev/null || true

# Serializa execucoes concorrentes (timer + hook do apt podem coincidir):
# sem isso dois processos poderiam reiniciar a API ao mesmo tempo.
if command -v flock >/dev/null 2>&1 && exec 9>"$STATE_DIR/lock" 2>/dev/null; then
  if ! flock -n 9; then
    log "outra execucao do guard esta em andamento; saindo."
    exit 0
  fi
fi

if [[ -n "$INSPECT_PID" ]]; then
  PID="$INSPECT_PID"
  RESTART_ALLOWED=0
else
  PID="$(systemctl show "$UNIT" -p MainPID --value 2>/dev/null || echo 0)"
  RESTART_ALLOWED=1
fi

if [[ -z "$PID" || "$PID" == "0" ]]; then
  [[ "$VERBOSE" == "1" ]] && log "$UNIT sem MainPID ativo; nada a fazer."
  exit 0
fi
if [[ ! -r "/proc/$PID/maps" ]]; then
  [[ "$VERBOSE" == "1" ]] && log "maps de /proc/$PID nao legivel; nada a fazer."
  exit 0
fi

# Mapeamentos cujo arquivo foi removido, restritos ao framework/releases.
STALE="$(grep ' (deleted)$' "/proc/$PID/maps" 2>/dev/null | grep -E "$PATH_RE" || true)"
if [[ -z "$STALE" ]]; then
  [[ "$VERBOSE" == "1" ]] && log "$UNIT (pid $PID) OK: nenhuma dependencia de framework removida."
  exit 0
fi

COUNT="$(printf '%s\n' "$STALE" | wc -l | tr -d ' ')"
# SAMPLE mostra apenas o caminho (a linha de /proc/<pid>/maps comeca com 5 campos).
SAMPLE="$(printf '%s\n' "$STALE" | sed -E 's/^([^ ]+ +){5}//; s/ \(deleted\)$//' | head -n 3 | tr '\n' ' ')"

if [[ "$DRY_RUN" == "1" || "$RESTART_ALLOWED" == "0" ]]; then
  log "OBSOLETO: $UNIT (pid $PID) tem $COUNT mapeamentos removidos do framework: $SAMPLE"
  [[ "$DRY_RUN" == "1" ]] && exit 10
  exit 0
fi

# Cooldown: evita laco de restart se o problema nao for resolvivel por restart
# (ex.: pacote do runtime corrompido). /run e tmpfs: zera no boot.
STAMP_FILE="$STATE_DIR/last-restart-$UNIT"
NOW="$(date +%s)"
if [[ -f "$STAMP_FILE" ]]; then
  LAST="$(cat "$STAMP_FILE" 2>/dev/null || echo 0)"
  if [[ "$LAST" =~ ^[0-9]+$ ]] && (( NOW - LAST < COOLDOWN_SECONDS )); then
    log "cooldown ativo: ultimo restart ha $((NOW - LAST))s (limite ${COOLDOWN_SECONDS}s); nao reiniciando. Intervencao manual necessaria."
    exit 0
  fi
fi
printf '%s' "$NOW" > "$STAMP_FILE" 2>/dev/null || true

log "$UNIT (pid $PID) tem $COUNT mapeamentos removidos do framework ($SAMPLE); reiniciando para religar ao runtime atual."
logger -t discovery-runtime-guard "restarting $UNIT: $COUNT framework mappings were deleted by a package upgrade ($SAMPLE)"
if ! systemctl restart "$UNIT"; then
  log "ERRO: falha ao executar systemctl restart $UNIT."
  exit 1
fi

for _ in $(seq 1 30); do
  sleep 1
  systemctl is-active --quiet "$UNIT" && break
done

NEW_PID="$(systemctl show "$UNIT" -p MainPID --value 2>/dev/null || echo 0)"
if [[ "$NEW_PID" != "0" && -r "/proc/$NEW_PID/maps" ]] && ! grep ' (deleted)$' "/proc/$NEW_PID/maps" 2>/dev/null | grep -qE "$PATH_RE"; then
  log "$UNIT reiniciado com sucesso (pid $NEW_PID, sem runtime obsoleto)."
  exit 0
fi
log "ERRO: $UNIT continua com runtime obsoleto apos restart (pid $NEW_PID)."
exit 1