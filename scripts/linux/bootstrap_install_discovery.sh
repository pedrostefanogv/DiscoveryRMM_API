#!/usr/bin/env bash
set -euo pipefail

log() {
  printf '[bootstrap] %s\n' "$*"
}

fail() {
  printf '[bootstrap][erro] %s\n' "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || fail "Comando obrigatorio ausente: $1"
}

normalize_branch() {
  local raw="$1"
  local normalized
  normalized="$(printf '%s' "$raw" | tr '[:upper:]' '[:lower:]')"

  case "$normalized" in
    lts|release|beta|dev)
      printf '%s' "$normalized"
      return
      ;;
  esac

  [[ "$raw" =~ ^[A-Za-z0-9._/-]+$ ]] || fail "Branch invalida: $raw"
  printf '%s' "$raw"
}

choose_branch_interactive() {
  while true; do
    echo >&2
    echo "Escolha o canal/branch para bootstrap:" >&2
    echo "1) lts" >&2
    echo "2) release" >&2
    echo "3) beta" >&2
    echo "4) dev" >&2
    echo "5) custom" >&2

    local selected_option
    read -r -p "Opcao [2]: " selected_option
    selected_option="${selected_option:-2}"
    selected_option="$(printf '%s' "$selected_option" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"

    case "$(printf '%s' "$selected_option" | tr '[:upper:]' '[:lower:]')" in
      1|lts)
        printf 'lts'
        return
        ;;
      2|release)
        printf 'release'
        return
        ;;
      3|beta)
        printf 'beta'
        return
        ;;
      4|dev)
        printf 'dev'
        return
        ;;
      5|custom)
        local custom_branch
        read -r -p "Informe a branch custom: " custom_branch
        custom_branch="$(printf '%s' "$custom_branch" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
        [[ -n "$custom_branch" ]] || {
          echo "Branch custom nao informada. Tente novamente." >&2
          continue
        }
        printf '%s' "$(normalize_branch "$custom_branch")"
        return
        ;;
      *)
        echo "Opcao invalida: $selected_option. Use 1-5 ou o nome da branch (lts/release/beta/dev)." >&2
        ;;
    esac
  done
}

BOOTSTRAP_REPO_URL="${DISCOVERY_BOOTSTRAP_REPO_URL:-https://github.com/pedrostefanogv/DiscoveryRMM_API.git}"
BOOTSTRAP_BRANCH="${DISCOVERY_BOOTSTRAP_BRANCH:-${DISCOVERY_RELEASE_CHANNEL:-${DISCOVERY_GIT_BRANCH:-release}}}"
BOOTSTRAP_WORKDIR=""

INSTALLER_ARGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --branch|-b)
      BOOTSTRAP_BRANCH="${2:-}"
      [[ -n "$BOOTSTRAP_BRANCH" ]] || fail "Parametro --branch exige valor"
      shift 2
      ;;
    --repo-url)
      BOOTSTRAP_REPO_URL="${2:-}"
      [[ -n "$BOOTSTRAP_REPO_URL" ]] || fail "Parametro --repo-url exige valor"
      shift 2
      ;;
    --workdir)
      BOOTSTRAP_WORKDIR="${2:-}"
      [[ -n "$BOOTSTRAP_WORKDIR" ]] || fail "Parametro --workdir exige valor"
      shift 2
      ;;
    --)
      shift
      while [[ $# -gt 0 ]]; do
        INSTALLER_ARGS+=("$1")
        shift
      done
      ;;
    *)
      INSTALLER_ARGS+=("$1")
      shift
      ;;
  esac
done

require_cmd git
require_cmd bash
require_cmd mktemp

if [[ -z "${DISCOVERY_BOOTSTRAP_BRANCH:-}" && -z "${DISCOVERY_RELEASE_CHANNEL:-}" && -z "${DISCOVERY_GIT_BRANCH:-}" ]]; then
  non_interactive=0
  for arg in "${INSTALLER_ARGS[@]}"; do
    if [[ "$arg" == "--non-interactive" ]]; then
      non_interactive=1
      break
    fi
  done

  if [[ "$non_interactive" -eq 0 ]]; then
    BOOTSTRAP_BRANCH="$(choose_branch_interactive)"
  fi
fi

BOOTSTRAP_BRANCH="$(normalize_branch "$BOOTSTRAP_BRANCH")"

if [[ -z "$BOOTSTRAP_WORKDIR" ]]; then
  BOOTSTRAP_WORKDIR="$(mktemp -d /tmp/discovery-bootstrap.XXXXXX)"
fi

log "Clonando repositorio em $BOOTSTRAP_WORKDIR (branch: $BOOTSTRAP_BRANCH)"
git clone --depth 1 --branch "$BOOTSTRAP_BRANCH" "$BOOTSTRAP_REPO_URL" "$BOOTSTRAP_WORKDIR"

INSTALLER_PATH="$BOOTSTRAP_WORKDIR/scripts/linux/install_discovery_server.sh"
[[ -f "$INSTALLER_PATH" ]] || fail "Instalador nao encontrado em $INSTALLER_PATH"

log "Executando instalador"
exec bash "$INSTALLER_PATH" "${INSTALLER_ARGS[@]}"