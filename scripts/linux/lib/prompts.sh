# Discovery RMM installer – user prompts and interactive input
# Requires: common.sh (log, warn, fail, wizard_header, wizard_step_label, is_valid_repo_url, generate_random_password, generate_random_admin_login, resolve_fido2_server_domain)

prompt_if_empty() {
  local var_name="$1"
  local prompt_text="$2"
  local secret="${3:-0}"
  local default_value="${4:-}"
  local current_value="${!var_name:-}"
  local reprompt="${WIZARD_REPROMPT:-0}"

  if [[ -n "$current_value" && "$reprompt" -eq 0 ]]; then
    return 0
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    if [[ -n "$current_value" ]]; then
      return 0
    fi
    if [[ -n "$default_value" ]]; then
      printf -v "$var_name" '%s' "$default_value"
      return 0
    fi
    fail "Variavel obrigatoria ausente para modo nao interativo: $var_name"
  fi

  local effective_default="${current_value:-$default_value}"
  local input=""
  while true; do
    if [[ "$secret" -eq 1 ]]; then
      if [[ -n "$current_value" ]]; then
        read -r -s -p "$prompt_text (Enter para manter atual): " input
      elif [[ -n "$effective_default" ]]; then
        read -r -s -p "$prompt_text (pressione Enter para usar o valor padrao): " input
      else
        read -r -s -p "$prompt_text: " input
      fi
      printf '\n'
    else
      if [[ -n "$effective_default" ]]; then
        read -r -p "$prompt_text [$effective_default]: " input
      else
        read -r -p "$prompt_text: " input
      fi
    fi

    if is_wizard_back_input "$input"; then
      return "${WIZARD_BACK_RC:-10}"
    fi

    input="${input:-$effective_default}"
    if [[ -n "$input" ]]; then
      printf -v "$var_name" '%s' "$input"
      return 0
    fi
    echo "Valor obrigatorio nao informado para $var_name. Tente novamente (ou digite 'voltar')." >&2
  done
}

prompt_yes_no() {
  local var_name="$1"
  local prompt_text="$2"
  local default_yn="${3:-n}"
  local current_value="${!var_name:-}"
  local reprompt="${WIZARD_REPROMPT:-0}"

  if [[ -n "$current_value" && "$reprompt" -eq 0 ]]; then
    return 0
  fi

  local norm_default="$default_yn"
  if [[ -n "$current_value" ]]; then
    case "$(printf '%s' "$current_value" | tr '[:upper:]' '[:lower:]')" in
      1|true|yes|y|sim|s) norm_default="y" ;;
      0|false|no|n|nao)   norm_default="n" ;;
    esac
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    printf -v "$var_name" '%s' "$norm_default"
    return 0
  fi

  local input="" normalized=""
  while true; do
    read -r -p "$prompt_text [$norm_default]: " input
    if is_wizard_back_input "$input"; then
      return "${WIZARD_BACK_RC:-10}"
    fi
    input="${input:-$norm_default}"
    normalized="$(printf '%s' "$input" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    case "$normalized" in
      1|true|yes|y|sim|s) printf -v "$var_name" '%s' "y"; return 0 ;;
      0|false|no|n|nao)   printf -v "$var_name" '%s' "n"; return 0 ;;
      *) echo "Resposta invalida: '$input'. Digite y/n (ou s/n, ou 'voltar')." >&2 ;;
    esac
  done
}

prompt_repo_url() {
  local var_name="$1"
  local prompt_text="$2"
  local default_value="${3:-}"
  local current_value="${!var_name:-}"
  local reprompt="${WIZARD_REPROMPT:-0}"

  if [[ -n "$current_value" && "$reprompt" -eq 0 ]]; then
    is_valid_repo_url "$current_value" || fail "URL invalida para $var_name: $current_value"
    return 0
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    if [[ -n "$current_value" ]]; then
      is_valid_repo_url "$current_value" || fail "URL invalida para $var_name: $current_value"
      return 0
    fi
    if [[ -n "$default_value" ]]; then
      printf -v "$var_name" '%s' "$default_value"
      return 0
    fi
    fail "Variavel obrigatoria ausente para modo nao interativo: $var_name"
  fi

  local effective_default="${current_value:-$default_value}"
  while true; do
    local input=""
    if [[ -n "$effective_default" ]]; then
      read -r -p "$prompt_text [$effective_default]: " input
    else
      read -r -p "$prompt_text: " input
    fi
    if is_wizard_back_input "$input"; then
      return "${WIZARD_BACK_RC:-10}"
    fi
    input="${input:-$effective_default}"
    [[ -n "$input" ]] || { echo "Valor obrigatorio nao informado para $var_name" >&2; continue; }
    if is_valid_repo_url "$input"; then
      printf -v "$var_name" '%s' "$input"
      return 0
    fi
    echo "URL invalida. Use https://.../repo.git ou git@host:org/repo.git" >&2
  done
}

is_valid_postgres_identifier() {
  [[ "${1:-}" =~ ^[A-Za-z_][A-Za-z0-9_]{0,62}$ ]]
}

is_valid_postgres_password_value() {
  local pw="${1:-}"
  (( ${#pw} >= 12 )) || return 1
  local pw_lower weak
  pw_lower="$(printf '%s' "$pw" | tr '[:upper:]' '[:lower:]')"
  for weak in "troque-esta-senha" "changeme" "changeme123" "password" "password1" "password123" "123456" "12345678" "admin123" "discovery" "discovery123"; do
    [[ "$pw_lower" == "$weak" ]] && return 1
  done
  return 0
}

prompt_postgres_password() {
  if [[ -z "${POSTGRES_PASSWORD:-}" ]] && sudo test -f /etc/discovery-api/discovery.env; then
    local existing_password
    existing_password="$(sudo sed -n 's/^ConnectionStrings__DefaultConnection=.*Password=\([^;]*\).*/\1/p' /etc/discovery-api/discovery.env | head -n 1)"
    if [[ -n "$existing_password" ]]; then
      POSTGRES_PASSWORD="$existing_password"
      POSTGRES_PASSWORD_AUTO=0
      log "Senha PostgreSQL carregada do discovery.env existente."
    fi
  fi

  if [[ -n "${POSTGRES_PASSWORD:-}" && "${WIZARD_REPROMPT:-0}" -eq 0 ]]; then
    POSTGRES_PASSWORD_AUTO="${POSTGRES_PASSWORD_AUTO:-0}"
    return 0
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    if [[ -n "${POSTGRES_PASSWORD:-}" ]]; then
      POSTGRES_PASSWORD_AUTO="${POSTGRES_PASSWORD_AUTO:-0}"
      return 0
    fi
    POSTGRES_PASSWORD="$(generate_random_password 24)"
    POSTGRES_PASSWORD_AUTO=1
    log "POSTGRES_PASSWORD nao informado; gerando senha aleatoria."
    return 0
  fi

  local generated_password
  generated_password="$(generate_random_password 24)"
  if [[ -n "${POSTGRES_PASSWORD:-}" ]]; then
    echo "Entrada oculta. Pressione Enter para manter a senha atual."
  else
    echo "Entrada oculta. Pressione Enter para usar a senha gerada automaticamente."
  fi

  local input=""
  while true; do
    read -r -s -p "Senha PostgreSQL: " input
    printf '\n'
    if is_wizard_back_input "$input"; then
      return "${WIZARD_BACK_RC:-10}"
    fi
    if [[ -z "$input" ]]; then
      if [[ -n "${POSTGRES_PASSWORD:-}" ]]; then
        return 0
      fi
      POSTGRES_PASSWORD="$generated_password"
      POSTGRES_PASSWORD_AUTO=1
      log "Senha PostgreSQL nao informada; gerada automaticamente."
      return 0
    fi
    if is_valid_postgres_password_value "$input"; then
      POSTGRES_PASSWORD_AUTO=0
      POSTGRES_PASSWORD="$input"
      return 0
    fi
    echo "Senha PostgreSQL invalida: minimo 12 caracteres e nao pode ser um placeholder comum (ex: admin123)." >&2
  done
}

prompt_postgres_configuration() {
  wizard_header "Banco de dados (PostgreSQL)" "$(wizard_step_label "7/10" "7/10")"
  echo "O instalador cria o usuario e o database se nao existirem."
  echo "----------------------------------------"
  prompt_validated_value POSTGRES_DB "Nome do database PostgreSQL" "discovery" \
    is_valid_postgres_identifier \
    "Nome de database invalido: use letras/digitos/underscore (ate 63 chars, iniciando por letra ou _)." || return $?
  prompt_validated_value POSTGRES_USER "Usuario PostgreSQL" "discovery_app" \
    is_valid_postgres_identifier \
    "Usuario PostgreSQL invalido: use letras/digitos/underscore (ate 63 chars, iniciando por letra ou _)." || return $?
  prompt_postgres_password || return $?
}

generate_random_nats_user() {
  local generated_user
  generated_user="discovery_nats_$(generate_random_password 10)"
  generated_user="$(printf '%s' "$generated_user" | tr '[:upper:]' '[:lower:]')"
  printf '%s' "$generated_user"
}

is_valid_nats_user_value() {
  [[ "${1:-}" =~ ^[A-Za-z0-9._-]{1,64}$ ]]
}

is_valid_nats_password_value() {
  [[ "${1:-}" =~ ^[A-Za-z0-9._+!@#%^*-]{12,128}$ ]]
}

prompt_nats_user() {
  if [[ -n "${NATS_USER:-}" && "${WIZARD_REPROMPT:-0}" -eq 0 ]]; then
    NATS_USER_AUTO="${NATS_USER_AUTO:-0}"
    return 0
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    if [[ -n "${NATS_USER:-}" ]]; then
      NATS_USER_AUTO="${NATS_USER_AUTO:-0}"
      return 0
    fi
    NATS_USER="$(generate_random_nats_user)"
    NATS_USER_AUTO=1
    log "NATS_USER nao informado; gerando usuario aleatorio."
    return 0
  fi

  local generated_user default_display
  generated_user="$(generate_random_nats_user)"
  default_display="${NATS_USER:-$generated_user}"
  echo "Pressione Enter para usar [$default_display]."
  local input=""
  while true; do
    read -r -p "Usuario NATS [$default_display]: " input
    if is_wizard_back_input "$input"; then
      return "${WIZARD_BACK_RC:-10}"
    fi
    input="$(printf '%s' "$input" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    if [[ -z "$input" ]]; then
      if [[ -n "${NATS_USER:-}" ]]; then
        return 0
      fi
      NATS_USER="$generated_user"
      NATS_USER_AUTO=1
      log "Usuario NATS nao informado; gerado automaticamente."
      return 0
    fi
    if is_valid_nats_user_value "$input"; then
      NATS_USER_AUTO=0
      NATS_USER="$input"
      return 0
    fi
    echo "Usuario NATS invalido: use 1-64 caracteres de [A-Za-z0-9._-]." >&2
  done
}

prompt_nats_password() {
  if [[ -n "${NATS_PASSWORD:-}" && "${WIZARD_REPROMPT:-0}" -eq 0 ]]; then
    NATS_PASSWORD_AUTO="${NATS_PASSWORD_AUTO:-0}"
    return 0
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    if [[ -n "${NATS_PASSWORD:-}" ]]; then
      NATS_PASSWORD_AUTO="${NATS_PASSWORD_AUTO:-0}"
      return 0
    fi
    NATS_PASSWORD="$(generate_random_password 24)"
    NATS_PASSWORD_AUTO=1
    log "NATS_PASSWORD nao informado; gerando senha aleatoria."
    return 0
  fi

  local generated_password
  generated_password="$(generate_random_password 24)"
  if [[ -n "${NATS_PASSWORD:-}" ]]; then
    echo "Entrada oculta. Pressione Enter para manter a senha NATS atual."
  else
    echo "Entrada oculta. Pressione Enter para usar uma senha NATS gerada automaticamente."
  fi
  local input=""
  while true; do
    read -r -s -p "Senha NATS: " input
    printf '\n'
    if is_wizard_back_input "$input"; then
      return "${WIZARD_BACK_RC:-10}"
    fi
    if [[ -z "$input" ]]; then
      if [[ -n "${NATS_PASSWORD:-}" ]]; then
        return 0
      fi
      NATS_PASSWORD="$generated_password"
      NATS_PASSWORD_AUTO=1
      log "Senha NATS nao informada; gerada automaticamente."
      return 0
    fi
    if is_valid_nats_password_value "$input"; then
      NATS_PASSWORD_AUTO=0
      NATS_PASSWORD="$input"
      return 0
    fi
    echo "Senha NATS invalida: use 12-128 caracteres de [A-Za-z0-9._+!@#%^*-] (sem espacos, aspas, \$ ou backtick)." >&2
  done
}

# ── Cloudflare Tunnel ──────────────────────────────────────────────────────

# O painel da Cloudflare nao exibe o token "puro": ele entrega um comando ja
# montado (cloudflared tunnel run --token ... / cloudflared service install ...).
# Aceitamos o comando inteiro e extraimos o token; o token puro tambem vale.
prompt_cloudflare_tunnel_token() {
  if [[ -n "${CLOUDFLARE_TUNNEL_TOKEN:-}" && "${WIZARD_REPROMPT:-0}" -eq 0 ]]; then
    normalize_cloudflare_tunnel_token
    return 0
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    if [[ -n "${CLOUDFLARE_TUNNEL_TOKEN:-}" ]]; then
      normalize_cloudflare_tunnel_token
      return 0
    fi
    fail "Variavel obrigatoria ausente para modo nao interativo: CLOUDFLARE_TUNNEL_TOKEN"
  fi

  echo "O painel da Cloudflare mostra o token dentro de um comando, por exemplo:"
  echo "  sudo cloudflared service install eyJhIjoi..."
  echo "  cloudflared tunnel run --token eyJhIjoi..."
  echo "Cole o comando completo (ou so o token); o instalador extrai o token."
  echo "----------------------------------------"

  while true; do
    local input=""
    if [[ -n "${CLOUDFLARE_TUNNEL_TOKEN:-}" ]]; then
      read -r -s -p "Cloudflare Tunnel token/comando (Enter para manter atual): " input
    else
      read -r -s -p "Cloudflare Tunnel token/comando (entrada oculta): " input
    fi
    printf '\n'

    if is_wizard_back_input "$input"; then
      return "${WIZARD_BACK_RC:-10}"
    fi

    # Um paste do painel costuma trazer 2-3 linhas (service install + tunnel run).
    # Consumir o restante evita que elas sejam lidas como resposta do proximo prompt.
    drain_cloudflare_tunnel_paste

    if [[ -z "$input" && -n "${CLOUDFLARE_TUNNEL_TOKEN:-}" ]]; then
      return 0
    fi

    local extracted
    extracted="$(extract_cloudflare_tunnel_token "$input")"
    if is_valid_cloudflare_tunnel_token "$extracted"; then
      printf -v CLOUDFLARE_TUNNEL_TOKEN '%s' "$extracted"
      return 0
    fi

    echo "Nao foi possivel identificar o token do Cloudflare no texto informado." >&2
    echo "Cole a linha que contem '--token eyJ...' ou o proprio token (comeca com 'eyJ')." >&2
  done
}

# Descarta as linhas remanescentes de um paste multi-linha do painel da
# Cloudflare (somente enquanto elas pertencem claramente ao bloco colado).
drain_cloudflare_tunnel_paste() {
  local line=""
  # Timeout pequeno (nao-zero): com `-t 0` o bash apenas checa disponibilidade
  # SEM consumir a linha, o que deixaria o while em loop infinito.
  while IFS= read -r -t 0.05 line; do
    case "$line" in
      *cloudflared*|*--token*|*eyJ*) continue ;;
      *) break ;;
    esac
  done
  return 0
}

# ── NATS configuration wizard ──────────────────────────────────────────────

prompt_nats_configuration() {
  load_existing_nats_defaults
  wizard_header "Mensageria (NATS)" "$(wizard_step_label "8/10" "8/10")"
  echo "Configure as credenciais e hosts do NATS local."
  echo "Esses dados sao usados pela API e agentes para mensagens."
  echo "Se usuario ou senha ficarem em branco, o instalador gera valores aleatorios."
  echo "JetStream para replay de comandos fan-out e configurado automaticamente."
  echo "Para ajustar TTL/limites, use as variaveis NATS_JS_* no arquivo de config."
  echo "----------------------------------------"

  prompt_nats_user || return $?
  prompt_nats_password || return $?
  prompt_validated_value NATS_AUTH_USER "Usuario de bypass da API no NATS (auth_users)" "$NATS_USER" \
    is_valid_nats_user_value \
    "Usuario NATS invalido: use 1-64 caracteres de [A-Za-z0-9._-]." || return $?
  while true; do
    prompt_if_empty NATS_AUTH_PASSWORD "Senha de bypass da API no NATS" 1 "$NATS_PASSWORD" || return $?
    if is_valid_nats_password_value "$NATS_AUTH_PASSWORD"; then
      break
    fi
    if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
      fail "NATS_AUTH_PASSWORD invalida: use 12-128 caracteres de [A-Za-z0-9._+!@#%^*-]."
    fi
    echo "Senha NATS invalida: use 12-128 caracteres de [A-Za-z0-9._+!@#%^*-]." >&2
    NATS_AUTH_PASSWORD=""
  done
  prompt_if_empty NATS_BIND_HOST "Host de bind do NATS (ex: 0.0.0.0 ou 127.0.0.1)" 0 "0.0.0.0" || return $?
  prompt_if_empty NATS_MONITOR_HOST "Host do monitor HTTP do NATS (porta 8222)" 0 "127.0.0.1" || return $?
  # Aceita y/n (padrao dos demais prompts); normalize_nats_settings converte para 1/0.
  prompt_yes_no NATS_AUTH_CALLOUT_ENABLED "Habilitar NATS auth callout? (y/n)" "y" || return $?
  prompt_if_empty NATS_AUTH_CALLOUT_SUBJECT "Subject do auth callout" 0 "\$SYS.REQ.USER.AUTH" || return $?
}

# ── Remote session duration wizard ─────────────────────────────────────────

# Carrega o valor existente do discovery.env no modo update (se a variavel
# nao foi informada), preservando a duracao configurada anteriormente.
load_existing_remote_session_defaults() {
  if [[ -n "${REMOTE_SESSION_MAX_DURATION_HOURS:-}" ]]; then
    return
  fi
  local env_file="/etc/discovery-api/discovery.env"
  # `return 0` explicito: sem isso a funcao devolve 1 quando ainda nao existe
  # discovery.env (instalacao nova) e, por estar sob `set -e`, o chamador
  # (prompt_remote_session_duration) aborta o instalador em silencio logo
  # apos a etapa do NATS.
  sudo test -f "$env_file" 2>/dev/null || return 0
  local minutes
  minutes="$(sudo awk -F= '/^RemoteDebug__MaxSessionDurationMinutes=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)"
  if [[ "$minutes" =~ ^[0-9]+$ ]] && (( minutes > 0 )); then
    REMOTE_SESSION_MAX_DURATION_HOURS=$(( (minutes + 59) / 60 ))
  fi
}

# Pergunta a duracao maxima de uma sessao remota (em horas). Enquanto o
# ping-pong estiver saudavel a sessao se renova; ao atingir o teto ela e
# encerrada. Padrao: 1 hora.
prompt_remote_session_duration() {
  load_existing_remote_session_defaults
  wizard_header "Sessoes remotas" "$(wizard_step_label "9/10" "9/10")"
  echo "Duracao maxima de uma sessao remota sem precisar reconectar."
  echo "Enquanto o navegador estiver saudavel a sessao se renova sozinha;"
  echo "ao atingir este teto ela e encerrada. Padrao: 1 hora."
  echo "----------------------------------------"

  local current="${REMOTE_SESSION_MAX_DURATION_HOURS:-1}"
  local answer=""
  while true; do
    if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
      REMOTE_SESSION_MAX_DURATION_HOURS="$current"
      return 0
    fi
    read -r -p "Duracao maxima da sessao remota (horas, 1-24) [$current]: " answer
    if is_wizard_back_input "$answer"; then
      return "${WIZARD_BACK_RC:-10}"
    fi
    answer="${answer:-$current}"
    if [[ "$answer" =~ ^[0-9]+$ ]] && (( answer >= 1 && answer <= 24 )); then
      REMOTE_SESSION_MAX_DURATION_HOURS="$answer"
      return 0
    fi
    echo "Informe um numero inteiro entre 1 e 24." >&2
  done
}

# ── Update policy wizard ───────────────────────────────────────────────────

prompt_selfupdate_settings() {
  wizard_header "Politica de atualizacao" "$(wizard_step_label "9/10" "9/10")"
  echo "O update do servidor e SEMPRE manual, executado pelo administrador via:"
  echo "  ./scripts/linux/install_discovery_server.sh --mode update"
  echo "  (ou a opcao 3 'Atualizar instalacao existente' do bootstrap)"
  echo "Nao existe atualizacao automatica agendada."
  echo "PADRAO das novas instalacoes: alem da stack, o instalador tambem verifica"
  echo "e aplica os updates do SO (apt-get update + upgrade) nesta instalacao e"
  echo "nos updates manuais seguintes. Para pular os pacotes do SO, use"
  echo "DISCOVERY_APPLY_SYSTEM_UPDATES=0."
  echo "----------------------------------------"

  # Valor efetivo atual: ambiente (--config/export) > discovery.env (preserva a
  # escolha de instalacao existente, inclusive 0) > padrao 1 (nova instalacao).
  # Sem isso, um re-run/reinstalacao reativaria os updates do SO em silencio
  # mesmo com DISCOVERY_APPLY_SYSTEM_UPDATES=0 gravado.
  local current_apply_updates="${DISCOVERY_APPLY_SYSTEM_UPDATES:-}"
  if [[ -z "$current_apply_updates" ]]; then
    current_apply_updates="$(resolve_apply_system_updates_value)"
  fi

  local env_file="${DISCOVERY_ENV_FILE:-/etc/discovery-api/discovery.env}"
  if [[ -z "${DISCOVERY_CLEAN_BUILD:-}" ]] && sudo test -f "$env_file"; then
    DISCOVERY_CLEAN_BUILD="$(sudo awk -F= '/^DISCOVERY_CLEAN_BUILD=/{sub("^[^=]*=",""); print; exit}' "$env_file" 2>/dev/null || true)"
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    DISCOVERY_CLEAN_BUILD="${DISCOVERY_CLEAN_BUILD:-1}"
    case "$DISCOVERY_CLEAN_BUILD" in 0|1) ;; *) DISCOVERY_CLEAN_BUILD="1" ;; esac
    DISCOVERY_APPLY_SYSTEM_UPDATES="${current_apply_updates:-1}"
    case "$DISCOVERY_APPLY_SYSTEM_UPDATES" in 0|1) ;; *) DISCOVERY_APPLY_SYSTEM_UPDATES="1" ;; esac
    return 0
  fi

  # Interativo: Enter mantem o valor atual (S em instalacao nova, N quando ja
  # estava desabilitado). Um valor explicito no --config/ambiente pula a
  # pergunta na primeira passagem, mas permite alterar ao voltar (WIZARD_REPROMPT=1).
  if [[ -z "${DISCOVERY_APPLY_SYSTEM_UPDATES:-}" || "${WIZARD_REPROMPT:-0}" -eq 1 ]]; then
    local apply_updates_default="S"
    [[ "$current_apply_updates" == "0" ]] && apply_updates_default="N"
    echo
    echo "Verificar e instalar tambem os updates do SO (apt-get update + upgrade)?"
    echo "Vale para esta instalacao e para os updates manuais seguintes."
    local apply_updates_choice
    while true; do
      read -r -p "Atualizar o SO automaticamente (S/n) [$apply_updates_default]: " apply_updates_choice
      if is_wizard_back_input "$apply_updates_choice"; then
        return "${WIZARD_BACK_RC:-10}"
      fi
      apply_updates_choice="${apply_updates_choice:-$apply_updates_default}"
      apply_updates_choice="$(printf '%s' "$apply_updates_choice" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
      case "$apply_updates_choice" in
        n|nao|não|no|0) DISCOVERY_APPLY_SYSTEM_UPDATES="0"; break ;;
        s|sim|y|yes|1)  DISCOVERY_APPLY_SYSTEM_UPDATES="1"; break ;;
        *) echo "Opcao invalida: '$apply_updates_choice'. Digite S ou N (ou 'voltar')." >&2 ;;
      esac
    done
  fi

  if [[ -z "${DISCOVERY_CLEAN_BUILD:-}" || "${WIZARD_REPROMPT:-0}" -eq 1 ]]; then
    local clean_default="2"
    [[ "${DISCOVERY_CLEAN_BUILD:-1}" == "0" ]] && clean_default="1"
    echo
    echo "Ativar build incremental para os updates manuais (API, portal e cache Go)?"
    echo "  1) Sim - Incremental (recomendado: mais rapido, reaproveita cache .NET/npm/Go)"
    echo "  2) Nao - Limpo (remove caches antes de cada build)"
    local clean_build_choice
    while true; do
      read -r -p "Escolha [$clean_default]: " clean_build_choice
      if is_wizard_back_input "$clean_build_choice"; then
        return "${WIZARD_BACK_RC:-10}"
      fi
      clean_build_choice="${clean_build_choice:-$clean_default}"
      clean_build_choice="$(printf '%s' "$clean_build_choice" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
      case "$clean_build_choice" in
        1|s|sim|y|yes|incremental) DISCOVERY_CLEAN_BUILD="0"; break ;;
        2|n|nao|não|no|limpo|clean) DISCOVERY_CLEAN_BUILD="1"; break ;;
        *) echo "Opcao invalida: '$clean_build_choice'. Digite 1 (incremental) ou 2 (limpo) (ou 'voltar')." >&2 ;;
      esac
    done
  else
    case "$DISCOVERY_CLEAN_BUILD" in 0|1) ;; *) DISCOVERY_CLEAN_BUILD="1" ;; esac
  fi
}

# ── TLS certificate wizard ─────────────────────────────────────────────────

prompt_tls_certificate_configuration() {
  load_existing_tls_defaults
  wizard_header "Certificado TLS" "$(wizard_step_label "4/10" "4/10")"
  echo "1) self-signed  - certificado local gerado pelo instalador"
  echo "2) zerossl-acme - ZeroSSL via ACME com validacao DNS manual"
  echo "3) letsencrypt-acme - Let's Encrypt via ACME com validacao DNS manual"
  echo "----------------------------------------"

  while true; do
    prompt_if_empty TLS_CERT_PROVIDER "Modo de certificado TLS (1/2/3 ou self-signed/zerossl-acme/letsencrypt-acme)" 0 "self-signed" || return $?
    case "$(printf '%s' "$TLS_CERT_PROVIDER" | tr '[:upper:]' '[:lower:]')" in
      1|self-signed|selfsigned|local) TLS_CERT_PROVIDER="self-signed"; break ;;
      2|zerossl|zerossl-acme|acme)    TLS_CERT_PROVIDER="zerossl-acme"; break ;;
      3|letsencrypt|lets-encrypt|le|letsencrypt-acme) TLS_CERT_PROVIDER="letsencrypt-acme"; break ;;
      *)
        if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
          fail "TLS_CERT_PROVIDER invalido: $TLS_CERT_PROVIDER (use self-signed, zerossl-acme ou letsencrypt-acme)"
        fi
        echo "Opcao invalida: '$TLS_CERT_PROVIDER'. Use 1 (self-signed), 2 (zerossl-acme) ou 3 (letsencrypt-acme)." >&2
        TLS_CERT_PROVIDER=""
        ;;
    esac
  done
  normalize_tls_certificate_provider

  if [[ "$TLS_CERT_PROVIDER" == "zerossl-acme" ]]; then
    prompt_zerossl_acme_configuration || return $?
  elif [[ "$TLS_CERT_PROVIDER" == "letsencrypt-acme" ]]; then
    prompt_letsencrypt_acme_configuration || return $?
  fi
}

prompt_zerossl_acme_configuration() {
  echo
  echo "ZeroSSL ACME usa desafio DNS-01. O instalador exibira o registro TXT,"
  echo "aguardara sua confirmacao e validara o DNS com dig antes de continuar."
  echo
  prompt_if_empty ZEROSSL_CERT_DOMAIN "Dominio do certificado" 0 "$(resolve_fido2_server_domain)" || return $?
  if [[ ( -z "${ZEROSSL_CERT_ALT_DOMAINS:-}" || "${WIZARD_REPROMPT:-0}" -eq 1 ) && "$NON_INTERACTIVE" -eq 0 ]]; then
    local alt_input=""
    read -r -p "SANs adicionais separados por virgula (opcional) [${ZEROSSL_CERT_ALT_DOMAINS:-}]: " alt_input
    if is_wizard_back_input "$alt_input"; then
      return "${WIZARD_BACK_RC:-10}"
    fi
    ZEROSSL_CERT_ALT_DOMAINS="${alt_input:-${ZEROSSL_CERT_ALT_DOMAINS:-}}"
  fi
  ZEROSSL_CERT_ALT_DOMAINS="${ZEROSSL_CERT_ALT_DOMAINS:-}"
  prompt_if_empty ZEROSSL_ACME_EMAIL "Email da conta ZeroSSL/ACME" || return $?
  prompt_if_empty ZEROSSL_ACME_EAB_KID "ZeroSSL EAB KID" || return $?
  prompt_if_empty ZEROSSL_ACME_EAB_HMAC_KEY "ZeroSSL EAB HMAC Key" || return $?
  prompt_if_empty ZEROSSL_DNS_RESOLVERS "Resolvers para validar DNS (separados por virgula)" 0 "1.1.1.1,8.8.8.8" || return $?
  prompt_if_empty ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS "Timeout de propagacao DNS em segundos" 0 "600" || return $?
  prompt_if_empty ZEROSSL_DNS_POLL_INTERVAL_SECONDS "Intervalo de consulta DNS em segundos" 0 "15" || return $?
  ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY="${ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY:-30}"
  echo "Renovacao recomendada: checar diariamente e renovar quando faltar ate ${ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY} dias."
  prompt_if_empty ZEROSSL_AUTO_RENEW_ENABLED "Criar timer automatico de renovacao ZeroSSL? (1/0)" 0 "1" || return $?
  normalize_zerossl_auto_renew_enabled
  normalize_tls_certificate_provider
}

prompt_letsencrypt_acme_configuration() {
  echo
  echo "Let's Encrypt ACME usa desafio DNS-01. O instalador exibira o registro TXT,"
  echo "aguardara sua confirmacao e validara o DNS com dig antes de continuar."
  echo "Nao requer EAB (diferente do ZeroSSL)."
  echo
  prompt_if_empty LETSENCRYPT_CERT_DOMAIN "Dominio do certificado" 0 "$(resolve_fido2_server_domain)" || return $?
  if [[ ( -z "${LETSENCRYPT_CERT_ALT_DOMAINS:-}" || "${WIZARD_REPROMPT:-0}" -eq 1 ) && "$NON_INTERACTIVE" -eq 0 ]]; then
    local alt_input=""
    read -r -p "SANs adicionais separados por virgula (opcional) [${LETSENCRYPT_CERT_ALT_DOMAINS:-}]: " alt_input
    if is_wizard_back_input "$alt_input"; then
      return "${WIZARD_BACK_RC:-10}"
    fi
    LETSENCRYPT_CERT_ALT_DOMAINS="${alt_input:-${LETSENCRYPT_CERT_ALT_DOMAINS:-}}"
  fi
  LETSENCRYPT_CERT_ALT_DOMAINS="${LETSENCRYPT_CERT_ALT_DOMAINS:-}"
  prompt_if_empty LETSENCRYPT_ACME_EMAIL "Email da conta Let's Encrypt/ACME" || return $?
  prompt_if_empty LETSENCRYPT_DNS_RESOLVERS "Resolvers para validar DNS (separados por virgula)" 0 "1.1.1.1,8.8.8.8" || return $?
  prompt_if_empty LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS "Timeout de propagacao DNS em segundos" 0 "600" || return $?
  prompt_if_empty LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS "Intervalo de consulta DNS em segundos" 0 "15" || return $?
  LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY="${LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY:-30}"
  echo "Renovacao recomendada: checar diariamente e renovar quando faltar ate ${LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY} dias."
  prompt_if_empty LETSENCRYPT_AUTO_RENEW_ENABLED "Criar timer automatico de renovacao Let's Encrypt? (1/0)" 0 "1" || return $?
  normalize_letsencrypt_auto_renew_enabled
  normalize_tls_certificate_provider
}

# ── Admin bootstrap wizard ─────────────────────────────────────────────────

prepare_bootstrap_admin_login() {
  local step_label="${1:-$(wizard_step_label "9/10" "8/9")}"
  local existing_login="${DISCOVERY_BOOTSTRAP_ADMIN_LOGIN:-}"
  local login_input=""
  local password_input=""

  DISCOVERY_BOOTSTRAP_ADMIN_LOGIN_AUTO=0

  if [[ -z "$existing_login" ]] && sudo test -f /etc/discovery-api/discovery.env; then
    existing_login="$(sudo awk -F= '/^DISCOVERY_BOOTSTRAP_ADMIN_LOGIN=/{sub("^[^=]*=",""); print; exit}' /etc/discovery-api/discovery.env 2>/dev/null || true)"
  fi

  if [[ "$NON_INTERACTIVE" -eq 0 && -z "${DISCOVERY_BOOTSTRAP_ADMIN_LOGIN:-}" ]]; then
    wizard_header "Primeiro acesso administrativo" "$step_label"
    echo "Informe o usuario administrador do primeiro acesso."
    echo "Se deixar em branco, o instalador gera um login temporario automaticamente."
    echo "A senha tambem pode ficar em branco para ser gerada pelo recover-admin."
    echo "Se digitar, ela precisa atender a politica da API (minimo 12 caracteres,"
    echo "com maiuscula, numero e caractere especial) — senao sera pedida de novo."
    echo "----------------------------------------"

    if [[ -n "$existing_login" ]]; then
      read -r -p "Usuario administrador inicial [$existing_login]: " login_input
      login_input="${login_input:-$existing_login}"
    else
      read -r -p "Usuario administrador inicial [gerar automaticamente]: " login_input
    fi

    # Mesma politica da API (Authentication:PasswordPolicy; defaults: 12 chars,
    # maiuscula, digito e especial). Sem isso, uma senha curta so era recusada
    # pelo --recover-admin no ULTIMO passo da instalacao — depois de tudo mais
    # instalado — e abortava o bootstrap do admin.
    local policy_min policy_upper policy_digit policy_special
    policy_min="$(sudo awk -F= '/^Authentication__PasswordPolicy__MinLength=/{sub("^[^=]*=",""); print; exit}' /etc/discovery-api/discovery.env 2>/dev/null || true)"
    policy_upper="$(sudo awk -F= '/^Authentication__PasswordPolicy__RequireUppercase=/{sub("^[^=]*=",""); print; exit}' /etc/discovery-api/discovery.env 2>/dev/null || true)"
    policy_digit="$(sudo awk -F= '/^Authentication__PasswordPolicy__RequireDigit=/{sub("^[^=]*=",""); print; exit}' /etc/discovery-api/discovery.env 2>/dev/null || true)"
    policy_special="$(sudo awk -F= '/^Authentication__PasswordPolicy__RequireSpecialChar=/{sub("^[^=]*=",""); print; exit}' /etc/discovery-api/discovery.env 2>/dev/null || true)"
    policy_min="${policy_min:-12}"
    [[ "$policy_min" =~ ^[0-9]+$ ]] || policy_min=12

    while true; do
      read -r -s -p "Senha administrativa inicial [gerar automaticamente]: " password_input
      printf '\n'

      # Enter = deixa o --recover-admin gerar uma senha forte.
      [[ -z "$password_input" ]] && break

      local policy_reason=""
      if (( ${#password_input} < policy_min )); then
        policy_reason="no minimo ${policy_min} caracteres"
      elif [[ "${policy_upper,,}" != "false" && ! "$password_input" =~ [A-Z] ]]; then
        policy_reason="pelo menos uma letra maiuscula"
      elif [[ "${policy_digit,,}" != "false" && ! "$password_input" =~ [0-9] ]]; then
        policy_reason="pelo menos um numero"
      elif [[ "${policy_special,,}" != "false" && ! "$password_input" =~ [^A-Za-z0-9] ]]; then
        policy_reason="pelo menos um caractere especial"
      fi
      [[ -z "$policy_reason" ]] && break

      echo "Senha recusada pela politica da API (${policy_reason}). Tente novamente ou pressione Enter para gerar automaticamente." >&2
    done

    if [[ -n "$login_input" ]]; then
      DISCOVERY_BOOTSTRAP_ADMIN_LOGIN="$login_input"
    fi

    if [[ -n "$password_input" ]]; then
      DISCOVERY_BOOTSTRAP_ADMIN_PASSWORD="$password_input"
    fi
  fi

  if [[ -z "${DISCOVERY_BOOTSTRAP_ADMIN_LOGIN:-}" ]]; then
    if [[ -n "$existing_login" ]]; then
      DISCOVERY_BOOTSTRAP_ADMIN_LOGIN="$existing_login"
    else
      DISCOVERY_BOOTSTRAP_ADMIN_LOGIN="$(generate_random_admin_login)"
      DISCOVERY_BOOTSTRAP_ADMIN_LOGIN_AUTO=1
      log "Login do primeiro acesso nao informado; gerando usuario administrador temporario: $DISCOVERY_BOOTSTRAP_ADMIN_LOGIN"
    fi
  fi

  if [[ -n "${DISCOVERY_BOOTSTRAP_ADMIN_PASSWORD:-}" ]]; then
    DISCOVERY_BOOTSTRAP_ADMIN_PASSWORD_AUTO=0
  else
    DISCOVERY_BOOTSTRAP_ADMIN_PASSWORD_AUTO=1
  fi

  ADMIN_RECOVERY_LOGIN="$DISCOVERY_BOOTSTRAP_ADMIN_LOGIN"
}

# ── Confirmation / summary ─────────────────────────────────────────────────

print_prerequisite_plan_summary() {
  local -a base_packages=(
    apt-transport-https ca-certificates curl git gnupg jq lsb-release
    dnsutils nginx openssl postgresql postgresql-contrib redis-server nats-server
  )
  local -a missing_base_packages=()
  local -a planned_actions=()
  local pkg

  for pkg in "${base_packages[@]}"; do
    if ! dpkg -s "$pkg" >/dev/null 2>&1; then
      missing_base_packages+=("$pkg")
    fi
  done

  if (( ${#missing_base_packages[@]} > 0 )); then
    planned_actions+=("Pacotes base via apt: ${missing_base_packages[*]}")
  fi

  if ! command -v dotnet >/dev/null 2>&1; then
    planned_actions+=("dotnet SDK 10.0 (apt Microsoft ou fallback dotnet-install.sh)")
  fi

  local node_needs_install=0
  local current_node_major=""
  if command -v node >/dev/null 2>&1; then
    current_node_major="$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || true)"
    if [[ ! "$current_node_major" =~ ^[0-9]+$ ]] || (( current_node_major < 20 )); then
      node_needs_install=1
    fi
  else
    node_needs_install=1
  fi
  if (( node_needs_install == 1 )); then
    planned_actions+=("Node.js 22 + npm")
  fi

  if ! command -v nk >/dev/null 2>&1; then
    planned_actions+=("nk para gerar chaves NATS")
  fi

  if [[ "$ACCESS_MODE" == "external" || "$ACCESS_MODE" == "hybrid" ]]; then
    if ! command -v cloudflared >/dev/null 2>&1; then
      planned_actions+=("cloudflared para o tunnel externo")
    fi
  fi

  local pg_major="" vector_pkg=""
  if command -v psql >/dev/null 2>&1; then
    pg_major="$(psql --version | sed -n 's/.* \([0-9][0-9]*\)\..*/\1/p' | head -n 1)"
    if [[ -n "$pg_major" ]]; then vector_pkg="postgresql-${pg_major}-pgvector"; fi
  fi
  if [[ -n "$vector_pkg" ]] && ! dpkg -s "$vector_pkg" >/dev/null 2>&1; then
    planned_actions+=("Pacote opcional ${vector_pkg} para embeddings")
  fi

  if (( ${#planned_actions[@]} == 0 )); then
    echo "- Pre-requisitos ausentes: nenhum detectado"
    return
  fi

  echo "- Pre-requisitos ausentes/acoes planejadas:"
  for pkg in "${planned_actions[@]}"; do
    echo "  - $pkg"
  done
}

# Revisao final: apresenta TODOS os campos coletados antes de executar a
# instalacao. Segredos nunca sao exibidos — apenas se estao definidos/gerados.
print_selected_configuration_summary() {
  local internal_portal_host="" internal_portal_url="" primary_portal_host="" primary_portal_url=""
  internal_portal_host="$(resolve_internal_portal_domain)"
  internal_portal_url="$(build_portal_access_url "$internal_portal_host")"
  primary_portal_host="$(resolve_fido2_server_domain)"
  primary_portal_url="$(build_portal_access_url "$primary_portal_host")"

  local install_user_display install_user_password_display=""
  if [[ -n "${DISCOVERY_INSTALL_USER:-}" ]]; then
    install_user_display="$DISCOVERY_INSTALL_USER"
    if [[ -n "${DISCOVERY_INSTALL_USER_PASSWORD:-}" ]]; then
      if [[ "${DISCOVERY_INSTALL_USER_PASSWORD_AUTO:-0}" -eq 1 ]]; then
        install_user_password_display="gerada automaticamente (anote a senha exibida no inicio)"
      else
        install_user_password_display="definida pelo operador"
      fi
    else
      install_user_password_display="nao definida"
    fi
  else
    install_user_display="$(id -un 2>/dev/null || echo desconhecido) (execucao atual, sem troca de usuario)"
  fi

  echo "Resumo das configuracoes selecionadas:"
  echo "- Usuario instalador (sudo): $install_user_display"
  if [[ -n "$install_user_password_display" ]]; then
    echo "- Senha do usuario instalador: $install_user_password_display"
  fi
  echo "- Branch: ${DISCOVERY_GIT_BRANCH:-}"
  echo "- Repo API: ${DISCOVERY_GIT_REPO:-}"
  echo "- Repo Agent: ${DISCOVERY_AGENT_GIT_REPO:-}"
  echo "- Repo Site: ${DISCOVERY_SITE_GIT_REPO:-}"
  echo "- Runtime .NET: ${DISCOVERY_DOTNET_RUNTIME:-<auto>}"
  echo "- Modo de acesso: ${ACCESS_MODE:-}"
  if [[ "${ACCESS_MODE:-}" == "internal" || "${ACCESS_MODE:-}" == "hybrid" ]]; then
    echo "- Host interno: ${INTERNAL_API_HOST:-}"
    if [[ -n "$internal_portal_url" ]]; then echo "- Portal interno: $internal_portal_url"; fi
  fi
  if [[ "${ACCESS_MODE:-}" == "external" || "${ACCESS_MODE:-}" == "hybrid" ]]; then
    echo "- Host externo: ${EXTERNAL_API_HOST:-}"
    echo "- Portal externo: https://${EXTERNAL_API_HOST:-}/"
    if [[ -n "${CLOUDFLARE_TUNNEL_TOKEN:-}" ]]; then
      echo "- Cloudflare Tunnel: token capturado (${#CLOUDFLARE_TUNNEL_TOKEN} caracteres; nao exibido)"
    else
      echo "- Cloudflare Tunnel: token nao informado"
    fi
  fi
  if [[ -n "$primary_portal_url" ]]; then
    echo "- Portal web principal: $primary_portal_url"
  else
    echo "- Portal web: publicado no mesmo host da API via Nginx"
  fi
  echo "- TLS: ${TLS_CERT_PROVIDER:-self-signed}"
  case "${TLS_CERT_PROVIDER:-self-signed}" in
    zerossl-acme)
      echo "-   dominio: ${ZEROSSL_CERT_DOMAIN:-$(resolve_fido2_server_domain)}"
      echo "-   SANs: ${ZEROSSL_CERT_ALT_DOMAINS:-<nenhum>}"
      echo "-   renovacao automatica: $( [[ "${ZEROSSL_AUTO_RENEW_ENABLED:-1}" == "1" ]] && echo habilitada || echo desabilitada )"
      ;;
    letsencrypt-acme)
      echo "-   dominio: ${LETSENCRYPT_CERT_DOMAIN:-$(resolve_fido2_server_domain)}"
      echo "-   SANs: ${LETSENCRYPT_CERT_ALT_DOMAINS:-<nenhum>}"
      echo "-   renovacao automatica: $( [[ "${LETSENCRYPT_AUTO_RENEW_ENABLED:-1}" == "1" ]] && echo habilitada || echo desabilitada )"
      ;;
  esac
  case "${DISCOVERY_OBJECT_STORAGE_MODE:-skip}" in
    rustfs)
      echo "- Storage de arquivos: RustFS local"
      echo "  - bucket: ${DISCOVERY_OBJECT_STORAGE_BUCKET:-discovery-files}"
      echo "  - endpoint publico: ${DISCOVERY_OBJECT_STORAGE_ENDPOINT:-<auto>}"
      echo "  - regiao: ${DISCOVERY_OBJECT_STORAGE_REGION:-us-east-1} | versao RustFS: ${RUSTFS_VERSION:-1.0.1}"
      echo "  - publicar bucket pelo nginx: ${DISCOVERY_OBJECT_STORAGE_NGINX_ENABLED:-y}"
      ;;
    external)
      echo "- Storage de arquivos: provedor externo S3"
      echo "  - bucket: ${DISCOVERY_OBJECT_STORAGE_BUCKET:-}"
      echo "  - endpoint: ${DISCOVERY_OBJECT_STORAGE_ENDPOINT:-}"
      echo "  - regiao: ${DISCOVERY_OBJECT_STORAGE_REGION:-us-east-1}"
      if [[ -n "${DISCOVERY_OBJECT_STORAGE_ACCESS_KEY:-}" ]]; then
        echo "  - credenciais: informadas (nao exibidas)"
      else
        echo "  - credenciais: ausentes"
      fi
      ;;
    *)
      echo "- Storage de arquivos: nao configurado nesta execucao"
      ;;
  esac
  echo "- OpenAPI (contrato /openapi): $( [[ "${OPENAPI_ENABLED:-0}" == "1" ]] && echo habilitado || echo desativado )"
  echo "- Scalar (UI /scalar em producao): $( [[ "${OPENAPI_SCALAR_ENABLED:-${OPENAPI_ENABLED:-0}}" == "1" ]] && echo habilitado || echo desativado )"
  echo "- PostgreSQL: database=${POSTGRES_DB:-} usuario=${POSTGRES_USER:-}"
  if [[ "${POSTGRES_PASSWORD_AUTO:-0}" -eq 1 ]]; then
    echo "- PostgreSQL senha: gerada automaticamente (veja /etc/discovery-api/discovery.env)"
  else
    echo "- PostgreSQL senha: informada pelo operador"
  fi
  if [[ "${NATS_USER_AUTO:-0}" -eq 1 ]]; then
    echo "- NATS usuario: ${NATS_USER:-} (gerado automaticamente)"
  else
    echo "- NATS usuario: ${NATS_USER:-}"
  fi
  if [[ "${NATS_PASSWORD_AUTO:-0}" -eq 1 ]]; then
    echo "- NATS senha: gerada automaticamente (veja /etc/discovery-api/discovery.env)"
  else
    echo "- NATS senha: informada pelo operador"
  fi
  echo "- NATS auth user (bypass da API): ${NATS_AUTH_USER:-}"
  echo "- NATS bind: ${NATS_BIND_HOST:-0.0.0.0} | monitor HTTP: ${NATS_MONITOR_HOST:-127.0.0.1}"
  if [[ "${NATS_AUTH_CALLOUT_ENABLED:-1}" == "1" ]]; then
    echo "- NATS auth callout: habilitado (subject ${NATS_AUTH_CALLOUT_SUBJECT:-\$SYS.REQ.USER.AUTH})"
  else
    echo "- NATS auth callout: desativado"
  fi
  echo "- NATS JetStream: $( [[ "${NATS_JS_ENABLED:-1}" == "1" ]] && echo habilitado || echo desativado )"
  echo "- Sessao remota (maxima): ${REMOTE_SESSION_MAX_DURATION_HOURS:-1} hora(s)"
  echo "- Build para updates manuais: $( [[ "${DISCOVERY_CLEAN_BUILD:-1}" == "1" ]] && echo limpo || echo incremental )"
  echo "- Diretorios: API=${DISCOVERY_API_BASE:-/opt/discovery-api} | Site=${DISCOVERY_SITE_BASE:-/opt/discovery-site} | Agent src=${DISCOVERY_AGENT_SRC:-/opt/discovery-agent-src}"
  if [[ -n "${DISCOVERY_BOOTSTRAP_ADMIN_LOGIN:-}" ]]; then
    if [[ "${DISCOVERY_BOOTSTRAP_ADMIN_LOGIN_AUTO:-0}" -eq 1 ]]; then
      echo "- Admin bootstrap login: $DISCOVERY_BOOTSTRAP_ADMIN_LOGIN (gerado automaticamente)"
    else
      echo "- Admin bootstrap login: $DISCOVERY_BOOTSTRAP_ADMIN_LOGIN"
    fi
  elif [[ "$NON_INTERACTIVE" -eq 0 ]]; then
    echo "- Admin bootstrap login: sera solicitado na etapa final (apos API pronta)"
  fi
  if [[ "${DISCOVERY_BOOTSTRAP_ADMIN_PASSWORD_AUTO:-1}" -eq 1 ]]; then
    echo "- Admin bootstrap senha: gerada automaticamente pelo recover-admin"
  else
    echo "- Admin bootstrap senha: informada pelo operador"
  fi
}

# ── Object storage (anexos/prints) ─────────────────────────────────────────
#
# Pergunta se o storage de objetos sera o RustFS local (instalado por este
# instalador) ou um provedor S3-compativel externo. Quando RustFS: instala o
# servico em loopback, cria o bucket e grava a configuracao no banco. O
# caminho do bucket e publicado pelo nginx (mesmo host do portal) para as URLs
# pre-assinadas funcionarem tambem para agentes fora da maquina.
#
# Default "rustfs": apertar Enter instala o RustFS local. "3" adia (nao instala
# nem altera nada). Entrada invalida NUNCA encerra o instalador: a pergunta e
# repetida ate vir um valor aceito.

is_valid_object_storage_bucket() {
  [[ "${1:-}" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]]
}

is_valid_object_storage_endpoint() {
  [[ "${1:-}" =~ ^(https?://)?[A-Za-z0-9.-]+(:[0-9]{2,5})?$ ]]
}

# prompt_validated_value <VAR> <texto> <default> <validador> <mensagem de erro>
# Repete a pergunta enquanto o valor for vazio ou reprovado pelo validador.
# Para valores vindos de env/--config, falha com mensagem clara (nao ha o que
# perguntar).
prompt_validated_value() {
  local var_name="$1"
  local prompt_text="$2"
  local default_value="${3:-}"
  local validator="$4"
  local error_text="$5"
  local current_value="${!var_name:-}"
  local reprompt="${WIZARD_REPROMPT:-0}"

  if [[ -n "$current_value" && "$reprompt" -eq 0 ]]; then
    "$validator" "$current_value" || fail "$error_text"
    return 0
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    if [[ -n "$current_value" ]]; then
      "$validator" "$current_value" || fail "$error_text"
      return 0
    fi
    if [[ -n "$default_value" ]]; then
      printf -v "$var_name" '%s' "$default_value"
      "$validator" "$default_value" || fail "$error_text"
      return 0
    fi
    fail "Variavel obrigatoria ausente para modo nao interativo: $var_name"
  fi

  local effective_default="${current_value:-$default_value}"
  local input=""
  while true; do
    if [[ -n "$effective_default" ]]; then
      read -r -p "$prompt_text [$effective_default]: " input
    else
      read -r -p "$prompt_text: " input
    fi
    if is_wizard_back_input "$input"; then
      return "${WIZARD_BACK_RC:-10}"
    fi
    input="${input:-$effective_default}"
    input="$(printf '%s' "$input" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    if [[ -n "$input" ]] && "$validator" "$input"; then
      printf -v "$var_name" '%s' "$input"
      return 0
    fi
    echo "$error_text" >&2
  done
}

# Selecao do modo de storage. Default rustfs; valores fora de 1/2/3 (ou dos
# nomes) sao re-perguntados em vez de derrubar o instalador.
prompt_object_storage_mode() {
  if [[ -n "${DISCOVERY_OBJECT_STORAGE_MODE:-}" && "${WIZARD_REPROMPT:-0}" -eq 0 ]]; then
    return 0
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    DISCOVERY_OBJECT_STORAGE_MODE="${DISCOVERY_OBJECT_STORAGE_MODE:-rustfs}"
    return 0
  fi

  local default_mode="${DISCOVERY_OBJECT_STORAGE_MODE:-rustfs}"
  local input=""
  while true; do
    read -r -p "Storage de arquivos (1/2/3 ou rustfs/external/skip) [$default_mode]: " input
    if is_wizard_back_input "$input"; then
      return "${WIZARD_BACK_RC:-10}"
    fi
    input="${input:-$default_mode}"
    input="$(printf '%s' "$input" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    case "$input" in
      1|local|rustfs) DISCOVERY_OBJECT_STORAGE_MODE="rustfs"; return 0 ;;
      2|external|s3)  DISCOVERY_OBJECT_STORAGE_MODE="external"; return 0 ;;
      3|skip|none)    DISCOVERY_OBJECT_STORAGE_MODE="skip"; return 0 ;;
      *) echo "Opcao invalida: '$input'. Use 1 (RustFS local), 2 (provedor externo) ou 3 (configurar depois)." >&2 ;;
    esac
  done
}

prompt_object_storage_configuration() {
  load_existing_object_storage_defaults
  wizard_header "Storage de arquivos (anexos/prints)" "$(wizard_step_label "5/10" "5/10")"
  echo "1) RustFS local      - instala o servidor de objetos nesta maquina (S3 compativel)."
  echo "2) Provedor externo  - S3 compativel ja existente (R2/S3/MinIO/...)."
  echo "3) Configurar depois - nao instala e nao altera nada."
  echo "----------------------------------------"
  prompt_object_storage_mode || return $?

  case "$(printf '%s' "${DISCOVERY_OBJECT_STORAGE_MODE:-rustfs}" | tr '[:upper:]' '[:lower:]')" in
    1|local|rustfs) DISCOVERY_OBJECT_STORAGE_MODE="rustfs" ;;
    2|external|s3)  DISCOVERY_OBJECT_STORAGE_MODE="external" ;;
    3|skip|none)    DISCOVERY_OBJECT_STORAGE_MODE="skip" ;;
  esac

  if [[ "$DISCOVERY_OBJECT_STORAGE_MODE" == "rustfs" ]]; then
    echo
    echo "RustFS: o bucket fica em /opt/rustfs/data e o servico escuta apenas em loopback."
    echo "O nginx publica o caminho do bucket no MESMO host do portal (sem DNS novo)."
    prompt_validated_value DISCOVERY_OBJECT_STORAGE_BUCKET "Nome do bucket" "discovery-files" \
      is_valid_object_storage_bucket \
      "Bucket invalido: use minusculas, digitos, ponto e hifen (3-63, sem comecar/terminar com ponto ou hifen)." || return $?
    prompt_validated_value DISCOVERY_OBJECT_STORAGE_ENDPOINT \
      "Endpoint publico do storage (host ou https://host, SEM path)" \
      "$(resolve_object_storage_default_endpoint)" \
      is_valid_object_storage_endpoint \
      "Endpoint invalido: use host[:porta] ou http(s)://host[:porta], SEM path." || return $?
    prompt_if_empty DISCOVERY_OBJECT_STORAGE_REGION "Regiao S3" 0 "us-east-1" || return $?
    prompt_if_empty RUSTFS_VERSION "Versao do RustFS a instalar (fixa, sem auto-update)" 0 "1.0.1" || return $?
    prompt_yes_no DISCOVERY_OBJECT_STORAGE_NGINX_ENABLED "Publicar o bucket pelo nginx? (y/N)" "y" || return $?
    if [[ "$NON_INTERACTIVE" -eq 0 && ( -z "${RUSTFS_SHA256:-}" || "${WIZARD_REPROMPT:-0}" -eq 1 ) ]]; then
      local sha_input=""
      read -r -p "sha256 do pacote do RustFS (opcional, recomendado; Enter = ${RUSTFS_SHA256:-nao verificar}): " sha_input
      if is_wizard_back_input "$sha_input"; then
        return "${WIZARD_BACK_RC:-10}"
      fi
      RUSTFS_SHA256="${sha_input:-${RUSTFS_SHA256:-}}"
    fi
  elif [[ "$DISCOVERY_OBJECT_STORAGE_MODE" == "external" ]]; then
    echo
    echo "Provedor externo: informe endpoint/bucket/credenciais. Nada sera instalado nesta maquina."
    prompt_validated_value DISCOVERY_OBJECT_STORAGE_ENDPOINT \
      "Endpoint do storage (host ou http(s)://host[:porta], SEM path)" "" \
      is_valid_object_storage_endpoint \
      "Endpoint invalido: use host[:porta] ou http(s)://host[:porta], SEM path." || return $?
    prompt_validated_value DISCOVERY_OBJECT_STORAGE_BUCKET "Nome do bucket" "" \
      is_valid_object_storage_bucket \
      "Bucket invalido: use minusculas, digitos, ponto e hifen (3-63, sem comecar/terminar com ponto ou hifen)." || return $?
    prompt_if_empty DISCOVERY_OBJECT_STORAGE_REGION "Regiao S3" 0 "us-east-1" || return $?
    prompt_if_empty DISCOVERY_OBJECT_STORAGE_ACCESS_KEY "Access key" || return $?
    prompt_if_empty DISCOVERY_OBJECT_STORAGE_SECRET_KEY "Secret key" 1 || return $?
    prompt_yes_no DISCOVERY_OBJECT_STORAGE_USE_PATH_STYLE "Usar path-style? (y/N)" "y" || return $?
    prompt_yes_no DISCOVERY_OBJECT_STORAGE_SSL_VERIFY "Verificar certificado TLS do storage? (y/N)" "y" || return $?
  else
    log "Storage de arquivos: configuracao adiada (nada sera instalado/alterado)."
  fi

  normalize_object_storage_settings

  case "$DISCOVERY_OBJECT_STORAGE_MODE" in
    rustfs)
      log "Storage: RustFS local (endpoint ${DISCOVERY_OBJECT_STORAGE_ENDPOINT}, bucket ${DISCOVERY_OBJECT_STORAGE_BUCKET}, versao ${RUSTFS_VERSION})" ;;
    external)
      log "Storage: provedor externo (endpoint ${DISCOVERY_OBJECT_STORAGE_ENDPOINT}, bucket ${DISCOVERY_OBJECT_STORAGE_BUCKET})" ;;
    *)
      log "Storage: nao configurado nesta execucao" ;;
  esac
}

confirm_installation() {
  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then return 0; fi

  WIZARD_JUMP_STEP=""
  wizard_header "Revisao final" "$(wizard_step_label "10/10" "10/10")"
  echo "Revise TODOS os campos abaixo antes de executar. Nada foi alterado no servidor ainda."
  echo "----------------------------------------"
  print_selected_configuration_summary
  echo
  echo "Pre-requisitos/acoes que serao executados:"
  print_prerequisite_plan_summary
  echo
  echo "Etapas disponiveis para edicao rapida:"
  echo "  1) Repositorios Git          6) OpenAPI / Scalar"
  echo "  2) Canal / Branch            7) Banco de dados (PostgreSQL)"
  echo "  3) Acesso do portal/API      8) Mensageria (NATS)"
  echo "  4) Certificado TLS           9) Sessoes remotas & Atualizacao"
  echo "  5) Storage de arquivos"
  echo

  local confirm="" edit_step=""
  while true; do
    read -r -p "Acao: (s) Confirmar e iniciar | (v) Voltar etapa | (e) Editar etapa (1-9) | (c) Cancelar [s]: " confirm
    if is_wizard_back_input "$confirm"; then
      return "${WIZARD_BACK_RC:-10}"
    fi
    confirm="$(printf '%s' "${confirm:-s}" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    case "$confirm" in
      s|sim|y|yes) return 0 ;;
      v|voltar|back) return "${WIZARD_BACK_RC:-10}" ;;
      e|editar|edit)
        read -r -p "Informe o numero da etapa para editar (1-9): " edit_step
        edit_step="$(printf '%s' "$edit_step" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
        if [[ "$edit_step" =~ ^[1-9]$ ]]; then
          WIZARD_JUMP_STEP="$edit_step"
          return 20
        fi
        echo "Etapa invalida: '$edit_step'. Informe um numero de 1 a 9." >&2
        ;;
      c|cancelar|n|nao|no)
        fail "Instalacao cancelada pelo usuario." ;;
      *)
        echo "Opcao invalida: '$confirm'. Use s (confirmar), v (voltar), e (editar 1-9) ou c (cancelar)." >&2 ;;
    esac
  done
}
