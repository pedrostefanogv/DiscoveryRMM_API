# Discovery RMM installer – system dependency installation
# Requires: common.sh (log, warn, fail)

install_apt_dependencies() {
  local -a all_packages=(
    apt-transport-https ca-certificates curl git gnupg jq lsb-release
    dnsutils nginx openssl postgresql postgresql-contrib redis-server nats-server
    golang-go gcc-mingw-w64-x86-64 binutils-mingw-w64-x86-64 nsis unzip
  )

  local -a missing_packages=()
  local pkg
  for pkg in "${all_packages[@]}"; do
    if ! dpkg -s "$pkg" >/dev/null 2>&1; then
      missing_packages+=("$pkg")
    fi
  done

  if (( ${#missing_packages[@]} == 0 )); then
    log "Todas as dependencias de sistema ja instaladas."
    return
  fi

  log "Instalando dependencias de sistema via apt: ${missing_packages[*]}"
  sudo apt-get update -y
  sudo apt-get install -y "${missing_packages[@]}"
}

# Instala o plugin nsJSON no NSIS para merge de configuracao JSON sem PowerShell.
# Baixa o release oficial do GitHub (Pieter-Dewachter/nsJSON v1.1.1.1).
# O DLL x86-unicode e extraido e copiado para /usr/share/nsis/Plugins/.
install_nsis_nsjson_plugin() {
  local plugin_dir="/usr/share/nsis/Plugins/x86-unicode"
  local dll_path="$plugin_dir/nsJSON.dll"
  local version="1.1.1.1"
  local download_url="https://github.com/Pieter-Dewachter/nsJSON/releases/download/v${version}/NsJSON-${version}.zip"
  local tmp_zip="/tmp/NsJSON-${version}.zip"
  local tmp_dir="/tmp/nsJSON-${version}"

  if [[ -f "$dll_path" ]]; then
    log "nsJSON plugin ja instalado em $dll_path"
    return
  fi

  log "Baixando nsJSON NSIS plugin v${version} de GitHub..."
  if ! curl -fsSL -o "$tmp_zip" "$download_url"; then
    warn "Falha ao baixar nsJSON de $download_url; o build NSIS pode falhar"
    return
  fi

  sudo mkdir -p "$plugin_dir" "$tmp_dir"
  unzip -qo "$tmp_zip" -d "$tmp_dir"
  local extracted_dll
  extracted_dll=$(find "$tmp_dir" -name 'nsJSON.dll' -path '*/x86-unicode/*' -print -quit)
  if [[ -z "$extracted_dll" ]]; then
    extracted_dll=$(find "$tmp_dir" -name 'nsJSON.dll' -print -quit)
  fi

  if [[ -f "$extracted_dll" ]]; then
    sudo cp "$extracted_dll" "$dll_path"
    sudo chmod 644 "$dll_path"
    log "nsJSON plugin instalado com sucesso em $dll_path"
  else
    warn "nsJSON.dll nao encontrado no zip; build NSIS pode falhar"
  fi

  rm -rf "$tmp_zip" "$tmp_dir"
}

ensure_dotnet_sdk() {
  if command -v dotnet >/dev/null 2>&1; then
    log "dotnet ja instalado"; return
  fi

  log "Instalando dotnet SDK 10.0"
  local ubuntu_version
  ubuntu_version="$(. /etc/os-release && printf '%s' "$VERSION_ID")"

  if curl -fsSL "https://packages.microsoft.com/config/ubuntu/${ubuntu_version}/packages-microsoft-prod.deb" -o /tmp/packages-microsoft-prod.deb; then
    sudo dpkg -i /tmp/packages-microsoft-prod.deb
    rm -f /tmp/packages-microsoft-prod.deb
    sudo apt-get update -y
    if sudo apt-get install -y dotnet-sdk-10.0; then return; fi
    log "Falha no apt para dotnet-sdk-10.0, aplicando fallback com dotnet-install.sh"
  else
    log "Repositorio apt da Microsoft indisponivel para Ubuntu ${ubuntu_version}, aplicando fallback com dotnet-install.sh"
  fi

  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh
  sudo mkdir -p /usr/share/dotnet
  sudo /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet
  sudo ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
  rm -f /tmp/dotnet-install.sh

  command -v dotnet >/dev/null 2>&1 || fail "dotnet nao foi instalado com sucesso"
}

ensure_nodejs() {
  local required_major="22"
  local current_major=""

  if command -v node >/dev/null 2>&1; then
    current_major="$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || true)"
    if [[ "$current_major" =~ ^[0-9]+$ ]] && (( current_major >= required_major )); then
      log "Node.js $current_major ja instalado"; return
    fi
    log "Node.js atual insuficiente (${current_major:-desconhecido}); atualizando para uma versao suportada"
  else
    log "Instalando Node.js"
  fi

  curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | sudo gpg --yes --dearmor -o /usr/share/keyrings/nodesource.gpg
  echo "deb [signed-by=/usr/share/keyrings/nodesource.gpg] https://deb.nodesource.com/node_22.x nodistro main" | sudo tee /etc/apt/sources.list.d/nodesource.list >/dev/null
  sudo apt-get update -y
  sudo apt-get install -y nodejs

  command -v node >/dev/null 2>&1 || fail "node nao foi instalado com sucesso"
  command -v npm  >/dev/null 2>&1 || fail "npm nao foi instalado com sucesso"
}

# ── Wails v3 CLI (wails3) ─────────────────────────────────────────────────

# O agent Go foi migrado para Wails v3 (github.com/wailsapp/wails/v3), que
# usa o binario `wails3` (nao mais `wails` v2). O wails3 e necessario durante
# o build do agent para regenerar os bindings do frontend (frontend/bindings)
# quando ha mudancas na API Go exposta.
#
# O processo de build roda sob o usuario de servico `discovery-api`, que nao
# tem HOME gravavel -- por isso instalamos o binario em /usr/local/bin (global,
# acessivel a todos e presente no PATH padrao do systemd), e nao em ~/go/bin.
# Versao alinhada com src/go.mod do agent (github.com/wailsapp/wails/v3).
WAILS3_VERSION="${WAILS3_VERSION:-v3.0.0-beta.3}"

ensure_wails3_toolchain() {
  if ! command -v go >/dev/null 2>&1; then
    # No fluxo de install, o Go e instalado por install_apt_dependencies antes.
    # No fluxo de update, o host ja deveria ter o Go; se nao tiver, nao bloqueamos
    # o update inteiro — apenas avisamos e deixamos o auto-install do build seguir.
    warn "go nao encontrado; pulando instalacao do wails3 (instale golang-go antes de buildar o agent)"
    return
  fi

  if command -v wails3 >/dev/null 2>&1; then
    log "wails3 ja instalado: $(command -v wails3)"
    return
  fi

  log "Instalando wails3 ${WAILS3_VERSION} (requerido pelo build do agent Wails v3)..."
  # GOBIN=/usr/local/bin coloca o binario em local acessivel a todos os usuarios,
  # independente do HOME inconsistente do usuario de servico. GOPATH explicito
  # garante um module cache determinístico (execução sob sudo/root).
  if ! sudo env GOBIN=/usr/local/bin GOPATH=/root/go go install "github.com/wailsapp/wails/v3/cmd/wails3@${WAILS3_VERSION}"; then
    warn "Falha ao instalar wails3 via go install; o build do agent dependera do auto-install do script de build (pode nao regerar bindings)."
    warn "Instale manualmente: sudo env GOBIN=/usr/local/bin go install github.com/wailsapp/wails/v3/cmd/wails3@${WAILS3_VERSION}"
    return
  fi

  if [[ -f /usr/local/bin/wails3 ]]; then
    sudo chmod 755 /usr/local/bin/wails3
    log "wails3 instalado em /usr/local/bin/wails3"
  else
    warn "wails3 instalado mas binario nao encontrado em /usr/local/bin; verifique GOBIN/go env"
  fi
}

ensure_service_user() {
  if id -u discovery-api >/dev/null 2>&1; then
    log "Usuario de servico discovery-api ja existe"; return
  fi
  log "Criando usuario de servico discovery-api"
  sudo useradd --system --no-create-home --home-dir /opt/discovery-api --shell /usr/sbin/nologin discovery-api
}

# Garante um HOME gravavel para o usuario de servico discovery-api.
# O processo de build do agent (go install do wails3, cache do Go, etc.) roda
# sob esse usuario; sem HOME, o Go nao consegue gravar GOPATH/GOBIN e o build
# do agent falha ou degrada silenciosamente.
ensure_service_user_home() {
  local home_dir="/var/lib/discovery-api"
  if ! sudo test -d "$home_dir"; then
    log "Criando HOME gravavel para discovery-api em $home_dir"
    sudo install -d -m 750 -o discovery-api -g discovery-api "$home_dir"
  fi
  # Garante que o usuario tenha o HOME apontando para o diretorio gravavel.
  # usermod fica em /usr/sbin, nem sempre no PATH de um usuario comum.
  local usermod_bin
  usermod_bin="$(command -v usermod || printf '/usr/sbin/usermod')"
  local current_home
  current_home="$(sudo getent passwd discovery-api | cut -d: -f6 2>/dev/null || true)"
  if [[ -n "$current_home" && "$current_home" != "$home_dir" ]]; then
    log "Ajustando HOME do discovery-api: $current_home -> $home_dir"
    sudo "$usermod_bin" -d "$home_dir" discovery-api
  fi
}

create_directories() {
  log "Criando estrutura de diretorios"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_API_BASE"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_API_RELEASES"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_API_SHARED"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_API_SOURCE"
  sudo install -d -m 755 -o discovery-api -g discovery-api "$DISCOVERY_SITE_BASE"
  sudo install -d -m 755 -o discovery-api -g discovery-api "$DISCOVERY_SITE_RELEASES"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_SITE_SOURCE"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_AGENT_SRC"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_AGENT_ARTIFACTS"
  sudo install -d -m 750 -o discovery-api -g discovery-api "$DISCOVERY_OPS_DIR"
  sudo install -d -m 750 -o root -g discovery-api /etc/discovery-api
  sudo install -d -m 750 -o root -g discovery-api /etc/discovery-api/certs

  cleanup_pipe_prefixed_artifacts "/root"
  cleanup_pipe_prefixed_artifacts "$DISCOVERY_API_BASE"
}

cleanup_pipe_prefixed_artifacts() {
  local scan_dir="$1"
  [[ -n "$scan_dir" && -d "$scan_dir" ]] || return 0

  local artifact_list
  artifact_list="$(sudo find "$scan_dir" -maxdepth 1 -type f -name '|*' -print 2>/dev/null || true)"
  [[ -n "$artifact_list" ]] || return 0

  warn "Encontrados arquivos com prefixo '|' em $scan_dir (provavel erro de quoting/pipeline). Removendo artefatos."
  local artifact
  while IFS= read -r artifact; do
    [[ -n "$artifact" ]] || continue
    warn "Removendo artefato: $artifact"
    sudo rm -f -- "$artifact" || rm -f -- "$artifact" || true
  done <<< "$artifact_list"
}

ensure_repo_git_ownership() {
  local repo_dir="$1"
  if ! sudo test -d "$repo_dir/.git"; then return 0; fi

  local mismatch_path
  mismatch_path="$(sudo find "$repo_dir/.git" -not -user discovery-api -print -quit 2>/dev/null || true)"
  [[ -n "$mismatch_path" ]] || return 0

  warn "Ownership inconsistente em $repo_dir/.git detectado ($mismatch_path). Corrigindo para discovery-api."
  sudo chown -R discovery-api:discovery-api "$repo_dir/.git"
}

ensure_repo_tree_ownership() {
  local repo_dir="$1"
  if ! sudo test -d "$repo_dir"; then return 0; fi

  local mismatch_path
  mismatch_path="$(sudo find "$repo_dir" -not -user discovery-api -print -quit 2>/dev/null || true)"
  [[ -n "$mismatch_path" ]] || return 0

  warn "Ownership inconsistente no working tree de $repo_dir detectado ($mismatch_path). Corrigindo para discovery-api."
  sudo chown -R discovery-api:discovery-api "$repo_dir"
}

setup_git_askpass() {
  if [[ -z "${GITHUB_PAT:-}" ]]; then
    log "GITHUB_PAT vazio; seguindo sem autenticacao GitHub (repo publico)"
    return
  fi

  local askpass_tmp
  askpass_tmp="$(mktemp)"
  cat > "$askpass_tmp" <<'EOF'
#!/usr/bin/env sh
case "$1" in
  *Username*) printf '%s\n' "x-access-token" ;;
  *Password*) printf '%s\n' "$GITHUB_PAT" ;;
  *) printf '\n' ;;
esac
EOF
  ASKPASS_FILE="$DISCOVERY_OPS_DIR/git-askpass.sh"
  sudo install -m 750 -o discovery-api -g discovery-api "$askpass_tmp" "$ASKPASS_FILE"
  rm -f "$askpass_tmp"
  export GIT_ASKPASS="$ASKPASS_FILE"
  export GIT_TERMINAL_PROMPT=0
  export GITHUB_PAT
}

cleanup_git_askpass() {
  if [[ -n "${ASKPASS_FILE:-}" && -f "$ASKPASS_FILE" ]]; then
    sudo rm -f "$ASKPASS_FILE" || rm -f "$ASKPASS_FILE" || true
  fi
}

clone_or_update_repo() {
  local repo_url="$1"; local repo_dir="$2"
  local -a git_env=( env "GIT_TERMINAL_PROMPT=0" )

  if [[ -n "${GITHUB_PAT:-}" ]]; then
    git_env+=("GIT_ASKPASS=$GIT_ASKPASS" "GITHUB_PAT=$GITHUB_PAT")
  fi

  if ! sudo test -d "$repo_dir/.git"; then
    if sudo test -d "$repo_dir"; then
      local backup_dir="${repo_dir}.bak-$(date +%Y%m%d%H%M%S)"
      log "Diretorio $repo_dir sem .git; movendo para $backup_dir"
      sudo mv "$repo_dir" "$backup_dir"
    fi
    sudo install -d -m 750 -o discovery-api -g discovery-api "$repo_dir"
    log "Clonando repositorio: $repo_url"
    sudo -u discovery-api "${git_env[@]}" git clone --branch "$DISCOVERY_GIT_BRANCH" "$repo_url" "$repo_dir"
  else
    ensure_repo_tree_ownership "$repo_dir"
    ensure_repo_git_ownership "$repo_dir"
    # Verifica se ha mudancas locais antes do reset destrutivo (apenas em modo interativo)
    if [[ "${NON_INTERACTIVE:-0}" -eq 0 ]]; then
      local dirty_files
      dirty_files="$(sudo -u discovery-api "${git_env[@]}" git -C "$repo_dir" status --porcelain 2>/dev/null || true)"
      if [[ -n "$dirty_files" ]]; then
        warn "Repositorio $repo_dir possui mudancas locais que serao descartadas:"
        printf '%s\n' "$dirty_files" | while IFS= read -r line; do warn "  $line"; done
        local confirm
        read -r -p "Confirmar reset --hard + git clean? Mudancas locais serao PERDIDAS. (s/N): " confirm
        confirm="$(printf '%s' "${confirm:-n}" | tr '[:upper:]' '[:lower:]')"
        case "$confirm" in
          s|sim|y|yes) ;;
          *) fail "Atualizacao cancelada pelo usuario devido a mudancas locais em $repo_dir." ;;
        esac
      fi
    fi
    log "Atualizando repositorio existente: $repo_dir"
    sudo -u discovery-api "${git_env[@]}" git -C "$repo_dir" fetch origin "$DISCOVERY_GIT_BRANCH"
    sudo -u discovery-api "${git_env[@]}" git -C "$repo_dir" checkout "$DISCOVERY_GIT_BRANCH"
    sudo -u discovery-api "${git_env[@]}" git -C "$repo_dir" reset --hard "origin/$DISCOVERY_GIT_BRANCH"
    # Limpa arquivos residuais (untracked) que podem causar conflitos de compilacao
    # Ex: command_handler.go que foi deletado do repo mas sobreviveu como untracked
    sudo -u discovery-api "${git_env[@]}" git -C "$repo_dir" clean -fd 2>/dev/null || true
    ensure_repo_tree_ownership "$repo_dir"
  fi
}
