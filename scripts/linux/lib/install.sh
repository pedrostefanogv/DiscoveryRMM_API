# Discovery RMM installer – system dependency installation
# Requires: common.sh (log, warn, fail)

# ── Update de pacotes do sistema (antes de qualquer update da stack) ──────

# Resolve o valor efetivo de DISCOVERY_APPLY_SYSTEM_UPDATES, na prioridade:
#   1) ambiente do instalador (--config/export);
#   2) valor ja gravado em discovery.env (instalacao existente: PRESERVA);
#   3) padrao 1 — NOVAS instalacoes verificam/aplicam os updates do SO.
# Quando o discovery.env existe mas nao tem a chave (instalacao antiga), o
# padrao permanece 0 para nao habilitar upgrade de SO sem aviso.
resolve_apply_system_updates_value() {
  local env_file="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"
  local value="${DISCOVERY_APPLY_SYSTEM_UPDATES:-}"
  local default_value="1"
  if [[ -z "$value" ]] && sudo test -f "$env_file" 2>/dev/null; then
    default_value="0"
    value="$(sudo awk -F= '/^DISCOVERY_APPLY_SYSTEM_UPDATES=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)"
  fi
  # Tolera CR/espacos (discovery.env editado no Windows): sem isso um "1" com
  # CR nao casaria o case e cairia no default 0, desabilitando updates.
  value="$(printf '%s' "$value" | tr -d '\r' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "${value:-$default_value}" in
    0) printf '0' ;;
    1) printf '1' ;;
    *) printf '%s' "$default_value" ;;
  esac
}

# Atualiza a lista de pacotes e faz upgrade completo do SO.
# Roda quando DISCOVERY_APPLY_SYSTEM_UPDATES=1 — padrao das novas instalacoes
# (ver discovery-install.env.example); defina 0 para pular.
# O apt-get upgrade e lento, pode restartar servicos de infra (postgres/nginx)
# e travar o update da aplicacao se um pacote problematico aparecer. A
# ferramenta correta para atualizar o SO tambem e o fluxo de patches do proprio
# sistema.
# --force-confold preserva arquivos de config locais e nunca trava em prompt
# interativo (DEBIAN_FRONTEND=noninteractive).
apply_system_updates() {
  # O discovery.env NAO e carregado no ambiente do instalador, e esta funcao
  # roda ANTES de load_update_defaults. O helper le do arquivo quando falta no
  # ambiente — sem isso, definir DISCOVERY_APPLY_SYSTEM_UPDATES no discovery.env
  # nao tinha efeito nenhum e o update sempre reportava "pulado".
  local apply_updates
  apply_updates="$(resolve_apply_system_updates_value)"

  if [[ "$apply_updates" != "1" ]]; then
    log "Update de pacotes do SO desativado (DISCOVERY_APPLY_SYSTEM_UPDATES=0)."
    return 0
  fi

  log "Atualizando sistema operacional (apt-get update + upgrade)..."
  if ! sudo env DEBIAN_FRONTEND=noninteractive apt-get update -y; then
    warn "apt-get update falhou; seguindo com upgrade (pode falhar se os indices nao atualizarem)"
  fi
  if ! sudo env DEBIAN_FRONTEND=noninteractive apt-get upgrade -y \
    -o Dpkg::Options::="--force-confdef" \
    -o Dpkg::Options::="--force-confold"; then
    warn "apt-get upgrade falhou; seguindo com o update da aplicacao (nao-bloqueante)."
    return 0
  fi
  log "Upgrade do sistema concluido."
}

install_apt_dependencies() {
  local -a all_packages=(
    apt-transport-https ca-certificates curl git gnupg jq lsb-release
    dnsutils nginx openssl postgresql postgresql-contrib redis-server nats-server
    golang-go gcc-mingw-w64-x86-64 binutils-mingw-w64-x86-64 nsis unzip
    # Build toolchain para compilar o wails3 (pre-requisito oficial do Wails v3)
    build-essential pkg-config
    # GTK4 + WebKitGTK 6.0 (stack default do Wails v3). Necessario para o
    # `go install` do wails3 (o release beta nao publica binario pre-compilado).
    libgtk-4-dev libwebkitgtk-6.0-dev
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

  if [[ -f "$dll_path" ]]; then
    log "nsJSON plugin ja instalado em $dll_path"
    return
  fi

  # Diretorio temporario proprio do usuario (mktemp): o instalador pode rodar
  # como usuario instalador (bootstrap_root_execution), e sobras fixas em /tmp
  # criadas por root numa execucao anterior nao sao gravaveis por ele
  # ("cannot create /tmp/nsJSON-...: Permission denied").
  local work_dir
  work_dir="$(mktemp -d "${TMPDIR:-/tmp}/nsJSON-${version}.XXXXXX")"
  local tmp_zip="$work_dir/NsJSON-${version}.zip"

  log "Baixando nsJSON NSIS plugin v${version} de GitHub..."
  if ! curl -fsSL --connect-timeout 10 --retry 2 --retry-delay 2 -o "$tmp_zip" "$download_url"; then
    warn "Falha ao baixar nsJSON de $download_url; o build NSIS pode falhar"
    rm -rf "$work_dir"
    return
  fi

  sudo mkdir -p "$plugin_dir"
  if ! unzip -qo "$tmp_zip" -d "$work_dir"; then
    warn "Falha ao extrair nsJSON ($tmp_zip); o build NSIS pode falhar"
    rm -rf "$work_dir"
    return
  fi

  local extracted_dll
  extracted_dll=$(find "$work_dir" -name 'nsJSON.dll' -path '*/x86-unicode/*' -print -quit)
  if [[ -z "$extracted_dll" ]]; then
    extracted_dll=$(find "$work_dir" -name 'nsJSON.dll' -print -quit)
  fi

  if [[ -f "$extracted_dll" ]]; then
    sudo cp "$extracted_dll" "$dll_path"
    sudo chmod 644 "$dll_path"
    log "nsJSON plugin instalado com sucesso em $dll_path"
  else
    warn "nsJSON.dll nao encontrado no zip; build NSIS pode falhar"
  fi

  rm -rf "$work_dir"
}

ensure_dotnet_sdk() {
  if command -v dotnet >/dev/null 2>&1; then
    log "dotnet ja instalado"; return
  fi

  log "Instalando dotnet SDK 10.0"

  # Diretorio temporario do proprio usuario (mktemp): evita caminhos fixos em
  # /tmp que podem nao ser gravaveis quando o instalador roda como usuario
  # instalador (arquivos criados por root em execucao anterior, ou o proprio
  # sudo mkdir deixando o diretorio com dono root).
  local work_dir
  work_dir="$(mktemp -d "${TMPDIR:-/tmp}/dotnet-install.XXXXXX")"
  local ms_deb="$work_dir/packages-microsoft-prod.deb"
  local dotnet_installer="$work_dir/dotnet-install.sh"

  local ubuntu_version
  # VERSION_ID esta ausente em Debian testing/sid — nunca expandir sem default
  # (set -u abortaria o instalador).
  ubuntu_version="$(. /etc/os-release && printf '%s' "${VERSION_ID:-}")"
  if [[ -z "$ubuntu_version" ]]; then
    log "VERSION_ID ausente em /etc/os-release; usando fallback dotnet-install.sh"
  elif curl -fsSL --connect-timeout 10 --retry 2 "https://packages.microsoft.com/config/ubuntu/${ubuntu_version}/packages-microsoft-prod.deb" -o "$ms_deb"; then
    sudo dpkg -i "$ms_deb"
    rm -f "$ms_deb"
    sudo apt-get update -y
    if sudo apt-get install -y dotnet-sdk-10.0; then rm -rf "$work_dir"; return; fi
    log "Falha no apt para dotnet-sdk-10.0, aplicando fallback com dotnet-install.sh"
  else
    log "Repositorio apt da Microsoft indisponivel para Ubuntu ${ubuntu_version}, aplicando fallback com dotnet-install.sh"
  fi

  curl -fsSL --connect-timeout 10 --retry 2 https://dot.net/v1/dotnet-install.sh -o "$dotnet_installer"
  chmod +x "$dotnet_installer"
  sudo mkdir -p /usr/share/dotnet
  sudo "$dotnet_installer" --channel 10.0 --install-dir /usr/share/dotnet
  sudo ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
  rm -rf "$work_dir"

  command -v dotnet >/dev/null 2>&1 || fail "dotnet nao foi instalado com sucesso"
}

ensure_nodejs() {
  local required_major="22"
  local current_major=""

  if command -v node >/dev/null 2>&1; then
    current_major="$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || true)"
    if [[ "$current_major" =~ ^[0-9]+$ ]] && (( current_major >= required_major )); then
      log "Node.js $current_major ja instalado"
    else
      log "Node.js atual insuficiente (${current_major:-desconhecido}); atualizando para uma versao suportada"
      install_nodejs_from_nodesource
    fi
  else
    log "Instalando Node.js"
    install_nodejs_from_nodesource
  fi

  command -v node >/dev/null 2>&1 || fail "node nao foi instalado com sucesso"
  command -v npm  >/dev/null 2>&1 || fail "npm nao foi instalado com sucesso"

  ensure_npm_cli
}

install_nodejs_from_nodesource() {
  curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | sudo gpg --yes --dearmor -o /usr/share/keyrings/nodesource.gpg
  echo "deb [signed-by=/usr/share/keyrings/nodesource.gpg] https://deb.nodesource.com/node_22.x nodistro main" | sudo tee /etc/apt/sources.list.d/nodesource.list >/dev/null
  sudo apt-get update -y
  sudo apt-get install -y nodejs
}

# O pacote do Node 22 traz npm 10.x, bem atras do atual (12.x). Instala a
# versao configurada em DISCOVERY_NPM_VERSION (default: latest).
# Falha aqui NAO derruba a instalacao: o build segue com o npm disponivel.
ensure_npm_cli() {
  local desired="${DISCOVERY_NPM_VERSION:-latest}"
  local before after
  before="$(npm --version 2>/dev/null || true)"

  log "Instalando/atualizando npm para '$desired' (atual: ${before:-desconhecido})"
  if ! sudo npm install -g "npm@$desired" >/dev/null 2>&1; then
    warn "Falha ao instalar/atualizar o npm para '$desired'; mantendo ${before:-a versao do pacote Node}"
    return 0
  fi

  after="$(npm --version 2>/dev/null || true)"
  if [[ -n "$after" && "$after" != "$before" ]]; then
    log "npm atualizado: ${before:-desconhecido} -> $after"
  else
    log "npm ${after:-$desired} instalado"
  fi
}

# ── Wails CLI (wails v2 / wails3 v3) ─────────────────────────────────────

# O agent Go usa Wails (v2 ou v3). O CLI e necessario durante o build do agent
# para regenerar os bindings do frontend (frontend/bindings) quando ha mudancas
# na API Go exposta. O processo de build roda sob o usuario de servico
# `discovery-api`, que nao tem HOME gravavel -- por isso instalamos o binario em
# /usr/local/bin (global, acessivel a todos e presente no PATH padrao do systemd).
# A versao/direcao e lida do go.mod LOCAL do agent APOS o git pull/clone, pois o
# servidor segue a branch instalada (ex.: release=v2, dev=v3.0.0-beta.11).
# Este fallback e usado apenas quando o go.mod nao e lido.
WAILS_VERSION_FALLBACK="v3.0.0-beta.11"

# Stack GTK padrao do Wails v3 (Ubuntu 24.04+ / Debian 13+), necessaria para
# compilar o wails3 via `go install` (releases beta nao publicam binario
# pre-compilado). Em distros mais antigas, usar GTK3. O Wails v2 usa GTK3.
WAILS_APT_DEPS_G4="build-essential pkg-config libgtk-4-dev libwebkitgtk-6.0-dev"
WAILS_APT_DEPS_GTK3="build-essential pkg-config libgtk-3-dev libwebkit2gtk-4.1-dev"

# Instala os pre-requisitos de build do Wails (GTK4/WebKitGTK p/ v3; GTK3 p/ v2
# ou fallback quando WebKitGTK 6.0 nao existe na distro). Separado de
# install_apt_dependencies porque, no fluxo de update, este nao re-roda.
ensure_wails3_apt_deps() {
  command -v apt-get >/dev/null 2>&1 || { warn "apt-get nao encontrado; nao e possivel garantir deps do wails"; return; }

  local major="${WAILS_MAJOR:-3}"
  local -a base_deps
  if [[ "$major" == "2" ]]; then
    mapfile -t base_deps <<< "${WAILS_APT_DEPS_GTK3// /$'\n'}"
  else
    mapfile -t base_deps <<< "${WAILS_APT_DEPS_G4// /$'\n'}"
  fi

  local -a deps=("${base_deps[@]}")
  # Se libwebkitgtk-6.0-dev nao existir no repositorio, usa o legado GTK3.
  if ! apt-cache show libwebkitgtk-6.0-dev >/dev/null 2>&1; then
    warn "libwebkitgtk-6.0-dev indisponivel; usando stack legado GTK3."
    mapfile -t deps <<< "${WAILS_APT_DEPS_GTK3// /$'\n'}"
  fi

  local -a missing=()
  local pkg
  for pkg in "${deps[@]}"; do
    if ! dpkg -s "$pkg" >/dev/null 2>&1; then
      missing+=("$pkg")
    fi
  done

  if (( ${#missing[@]} == 0 )); then
    return
  fi

  log "Instalando dependencias de build do wails via apt: ${missing[*]}"
  sudo apt-get update -y
  sudo apt-get install -y "${missing[@]}" || \
    warn "Falha ao instalar deps de build do wails; o go install do wails pode falhar"
}

# Resolve versao/major do Wails a partir do go.mod LOCAL do agent (pos-pull).
# Preenche: WAILS_MAJOR (2|3), WAILS_VERSION, WAILS_BIN (wails|wails3), WAILS_PKG.
#
# IMPORTANTE: o clone do agent pertence ao usuario discovery-api e
# /opt/discovery-agent-src e 0750; o instalador roda como usuario instalador
# (discovery) e NAO tem leitura direta — sem sudo a deteccao falhava e o
# instalador caia no fallback fixo, instalando um Wails diferente do que o
# agent realmente usa (ex.: beta.11 em vez de beta.28).
resolve_wails_version() {
  local agent_src="${DISCOVERY_AGENT_SRC:-/opt/discovery-agent-src}"
  local -a candidates=(
    "$agent_src/src/go.mod"
    "$agent_src/go.mod"
    "$agent_src/cmd/go.mod"
    "$agent_src/agent/go.mod"
    "$agent_src/app/go.mod"
  )
  local path
  while IFS= read -r path; do
    [[ -n "$path" ]] && candidates+=("$path")
  done < <(sudo find "$agent_src" -maxdepth 4 -name go.mod -not -path '*/.git/*' 2>/dev/null || true)

  local go_mod="" resolved="" candidate="" line=""
  for candidate in "${candidates[@]}"; do
    line="$(sudo grep -hE 'wailsapp/wails/v(2|3)[[:space:]]+v[0-9]' "$candidate" 2>/dev/null | head -n1 || true)"
    if [[ -n "$line" ]]; then
      go_mod="$candidate"
      resolved="$line"
      break
    fi
  done

  local version=""
  local major=""
  if [[ -n "$resolved" ]]; then
    major="$(printf '%s' "$resolved" | sed -nE 's/.*wailsapp\/wails\/v([23]).*/\1/p')"
    version="$(printf '%s' "$resolved" | sed -nE 's/.*wailsapp\/wails\/v[23][[:space:]]+[[:space:]]*(v[^[:space:]]+).*/\1/p')"
  fi

  if [[ -z "$version" ]]; then
    warn "Versao do wails nao detectada no go.mod do agent (${go_mod:-$agent_src}); usando fallback ${WAILS_VERSION_FALLBACK}"
    major="3"; version="$WAILS_VERSION_FALLBACK"
  else
    log "Wails detectado no go.mod do agent ($go_mod): $version"
  fi

  WAILS_MAJOR="${major:-3}"
  WAILS_VERSION="$version"
  if [[ "$WAILS_MAJOR" == "2" ]]; then
    WAILS_BIN="wails" WAILS_PKG="github.com/wailsapp/wails/v2/cmd/wails" WAILS_CLI_LABEL="wails (v2)"
  else
    WAILS_MAJOR="3" WAILS_BIN="wails3" WAILS_PKG="github.com/wailsapp/wails/v3/cmd/wails3" WAILS_CLI_LABEL="wails3 (v3)"
  fi
  log "Wails resolvido via go.mod: ${WAILS_CLI_LABEL} ${WAILS_VERSION}"
}

# Garante o CLI do Wails na versao exata exigida pelo go.mod do agent.
# Usa sentinela /opt/discovery-ops/wails-cli.version para saber o que esta
# instalado e permitir UPGRADE E DOWNGRADE automaticos (v2<->v3, beta<->stable).
ensure_wails_toolchain() {
  if ! command -v go >/dev/null 2>&1; then
    warn "go nao encontrado; pulando instalacao do cli wails (instale golang-go antes de buildar)"
    return
  fi

  resolve_wails_version

  local sentinel_file="/opt/discovery-ops/wails-cli.version"
  local installed_marker=""
  if [[ -f "$sentinel_file" ]]; then
    installed_marker="$(cat "$sentinel_file" 2>/dev/null || true)"
  fi

  local want_marker="${WAILS_BIN}|${WAILS_VERSION}"
  if [[ "$installed_marker" == "$want_marker" ]] && command -v "$WAILS_BIN" >/dev/null 2>&1; then
    log "${WAILS_CLI_LABEL} ${WAILS_VERSION} ja instalado (sentinela ok)"
    return
  fi

  ensure_wails3_apt_deps

  log "Instalando ${WAILS_CLI_LABEL} ${WAILS_VERSION} (requerido pelo build do agent)..."
  if ! sudo env GOBIN=/usr/local/bin GOPATH=/root/go go install "${WAILS_PKG}@${WAILS_VERSION}"; then
    warn "Falha ao instalar ${WAILS_BIN}; o build do agent dependera do auto-install (pode nao regerar bindings)."
    return
  fi

  local installed_bin="/usr/local/bin/${WAILS_BIN}"
  if [[ -x "$installed_bin" ]]; then
    sudo chmod 755 "$installed_bin"
    # Grava versao no sentinela para upgrade/downgrade futuro.
    printf '%s\n' "$want_marker" | sudo tee "$sentinel_file" >/dev/null
    log "${WAILS_CLI_LABEL} ${WAILS_VERSION} instalado em ${installed_bin}"
  else
    warn "${WAILS_BIN} instalado mas binario nao encontrado em /usr/local/bin; verifique GOBIN/go env"
  fi
}

# Alias de compatibilidade (nome anterior mantido).
ensure_wails3_toolchain() {
  ensure_wails_toolchain
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
# sob esse usuario; sem HOME gravavel, o Go nao consegue gravar GOPATH/GOBIN e
# o build do agent falha ou degrada silenciosamente.
#
# Nao usamos `usermod -d` aqui: o HOME efetivo do processo e definido por
# `Environment=HOME=/var/lib/discovery-api` no systemd service, e o `usermod`
# falha com "user is currently used by process" quando discovery-api esta ativo
# durante um update (servico em execucao). Aqui so garantimos que o diretorio
# exista e seja gravavel pelo usuario de servico.
ensure_service_user_home() {
  local home_dir="/var/lib/discovery-api"
  if ! sudo test -d "$home_dir"; then
    log "Criando HOME gravavel para discovery-api em $home_dir"
    sudo install -d -m 750 -o discovery-api -g discovery-api "$home_dir"
  fi

  # Garante write exitoso (defensivo): ajusta ownership se necessario.
  local mismatch
  mismatch="$(sudo find "$home_dir" -maxdepth 1 -not -user discovery-api -print -quit 2>/dev/null || true)"
  if [[ -n "$mismatch" ]]; then
    warn "Ownership inconsistente em $home_dir ($mismatch); corrigindo para discovery-api."
    sudo chown -R discovery-api:discovery-api "$home_dir"
  fi
}

# Garante o diretorio do shallow clone do winget-pkgs usado pelo
# WingetManifestsSyncService (AppCatalog:Winget:ClonePath). O servico roda como
# discovery-api e nao consegue criar diretorios em /var/lib (root), por isso o
# clone vive dentro do HOME do servico.
ensure_winget_clone_dir() {
  local clone_dir="/var/lib/discovery-api/winget-pkgs"
  if ! sudo test -d "$clone_dir"; then
    log "Criando diretorio do clone winget-pkgs em $clone_dir"
    sudo install -d -m 750 -o discovery-api -g discovery-api "$clone_dir"
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
  if [[ "${DISCOVERY_OBJECT_STORAGE_MODE:-skip}" == "rustfs" ]]; then
    # Base do RustFS; o dono (usuario rustfs) e criado em setup_object_storage_rustfs.
    sudo install -d -m 755 /opt/rustfs
  fi

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
