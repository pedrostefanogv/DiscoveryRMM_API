# Discovery RMM installer – user prompts and interactive input
# Requires: common.sh (log, warn, fail, wizard_header, wizard_step_label, is_valid_repo_url, generate_random_password, generate_random_admin_login, resolve_fido2_server_domain)

prompt_if_empty() {
  local var_name="$1"
  local prompt_text="$2"
  local secret="${3:-0}"
  local default_value="${4:-}"
  local current_value="${!var_name:-}"

  if [[ -n "$current_value" ]]; then
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    if [[ -n "$default_value" ]]; then
      printf -v "$var_name" '%s' "$default_value"
      return
    fi
    fail "Variavel obrigatoria ausente para modo nao interativo: $var_name"
  fi

  local input=""
  if [[ "$secret" -eq 1 ]]; then
    if [[ -n "$default_value" ]]; then
      read -r -s -p "$prompt_text (pressione Enter para usar o valor padrao): " input
      input="${input:-$default_value}"
    else
      read -r -s -p "$prompt_text" input
    fi
    printf '\n'
  else
    if [[ -n "$default_value" ]]; then
      read -r -p "$prompt_text [$default_value]: " input
      input="${input:-$default_value}"
    else
      read -r -p "$prompt_text: " input
    fi
  fi

  [[ -n "$input" ]] || fail "Valor obrigatorio nao informado para $var_name"
  printf -v "$var_name" '%s' "$input"
}

prompt_repo_url() {
  local var_name="$1"
  local prompt_text="$2"
  local default_value="${3:-}"
  local current_value="${!var_name:-}"

  if [[ -n "$current_value" ]]; then
    is_valid_repo_url "$current_value" || fail "URL invalida para $var_name: $current_value"
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    if [[ -n "$default_value" ]]; then
      printf -v "$var_name" '%s' "$default_value"
      return
    fi
    fail "Variavel obrigatoria ausente para modo nao interativo: $var_name"
  fi

  while true; do
    local input=""
    if [[ -n "$default_value" ]]; then
      read -r -p "$prompt_text [$default_value]: " input
      input="${input:-$default_value}"
    else
      read -r -p "$prompt_text: " input
    fi
    [[ -n "$input" ]] || { echo "Valor obrigatorio nao informado para $var_name" >&2; continue; }
    if is_valid_repo_url "$input"; then
      printf -v "$var_name" '%s' "$input"
      return
    fi
    echo "URL invalida. Use https://.../repo.git ou git@host:org/repo.git" >&2
  done
}

prompt_postgres_password() {
  if [[ -z "${POSTGRES_PASSWORD:-}" ]] && sudo test -f /etc/discovery-api/discovery.env; then
    local existing_password
    existing_password="$(sudo sed -n 's/^ConnectionStrings__DefaultConnection=.*Password=\([^;]*\).*/\1/p' /etc/discovery-api/discovery.env | head -n 1)"
    if [[ -n "$existing_password" ]]; then
      POSTGRES_PASSWORD="$existing_password"
      POSTGRES_PASSWORD_AUTO=0
      log "Senha PostgreSQL carregada do discovery.env existente."
      return
    fi
  fi

  if [[ -n "${POSTGRES_PASSWORD:-}" ]]; then
    POSTGRES_PASSWORD_AUTO=0
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    POSTGRES_PASSWORD="$(generate_random_password 24)"
    POSTGRES_PASSWORD_AUTO=1
    log "POSTGRES_PASSWORD nao informado; gerando senha aleatoria."
    return
  fi

  local generated_password
  generated_password="$(generate_random_password 24)"
  echo "Entrada oculta. Pressione Enter para usar a senha gerada automaticamente."
  local input=""
  read -r -s -p "Senha PostgreSQL: " input
  printf '\n'
  if [[ -z "$input" ]]; then
    POSTGRES_PASSWORD="$generated_password"
    POSTGRES_PASSWORD_AUTO=1
    log "Senha PostgreSQL nao informada; gerada automaticamente."
    return
  fi

  POSTGRES_PASSWORD_AUTO=0
  POSTGRES_PASSWORD="$input"
}

generate_random_nats_user() {
  local generated_user
  generated_user="discovery_nats_$(generate_random_password 10)"
  generated_user="$(printf '%s' "$generated_user" | tr '[:upper:]' '[:lower:]')"
  printf '%s' "$generated_user"
}

prompt_nats_user() {
  if [[ -n "${NATS_USER:-}" ]]; then
    NATS_USER_AUTO="${NATS_USER_AUTO:-0}"
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    NATS_USER="$(generate_random_nats_user)"
    NATS_USER_AUTO=1
    log "NATS_USER nao informado; gerando usuario aleatorio."
    return
  fi

  local generated_user
  generated_user="$(generate_random_nats_user)"
  echo "Pressione Enter para usar um usuario NATS gerado automaticamente."
  local input=""
  read -r -p "Usuario NATS [$generated_user]: " input
  input="$(printf '%s' "$input" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  if [[ -z "$input" ]]; then
    NATS_USER="$generated_user"
    NATS_USER_AUTO=1
    log "Usuario NATS nao informado; gerado automaticamente."
    return
  fi

  NATS_USER_AUTO=0
  NATS_USER="$input"
}

prompt_nats_password() {
  if [[ -n "${NATS_PASSWORD:-}" ]]; then
    NATS_PASSWORD_AUTO="${NATS_PASSWORD_AUTO:-0}"
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    NATS_PASSWORD="$(generate_random_password 24)"
    NATS_PASSWORD_AUTO=1
    log "NATS_PASSWORD nao informado; gerando senha aleatoria."
    return
  fi

  local generated_password
  generated_password="$(generate_random_password 24)"
  echo "Entrada oculta. Pressione Enter para usar uma senha NATS gerada automaticamente."
  local input=""
  read -r -s -p "Senha NATS: " input
  printf '\n'
  if [[ -z "$input" ]]; then
    NATS_PASSWORD="$generated_password"
    NATS_PASSWORD_AUTO=1
    log "Senha NATS nao informada; gerada automaticamente."
    return
  fi

  NATS_PASSWORD_AUTO=0
  NATS_PASSWORD="$input"
}

# ── Cloudflare Tunnel ──────────────────────────────────────────────────────

# O painel da Cloudflare nao exibe o token "puro": ele entrega um comando ja
# montado (cloudflared tunnel run --token ... / cloudflared service install ...).
# Aceitamos o comando inteiro e extraimos o token; o token puro tambem vale.
prompt_cloudflare_tunnel_token() {
  if [[ -n "${CLOUDFLARE_TUNNEL_TOKEN:-}" ]]; then
    normalize_cloudflare_tunnel_token
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    fail "Variavel obrigatoria ausente para modo nao interativo: CLOUDFLARE_TUNNEL_TOKEN"
  fi

  echo "O painel da Cloudflare mostra o token dentro de um comando, por exemplo:"
  echo "  sudo cloudflared service install eyJhIjoi..."
  echo "  cloudflared tunnel run --token eyJhIjoi..."
  echo "Cole o comando completo (ou so o token); o instalador extrai o token."
  echo "----------------------------------------"

  while true; do
    local input=""
    read -r -s -p "Cloudflare Tunnel token/comando (entrada oculta): " input
    printf '\n'
    # Um paste do painel costuma trazer 2-3 linhas (service install + tunnel run).
    # Consumir o restante evita que elas sejam lidas como resposta do proximo prompt.
    drain_cloudflare_tunnel_paste

    local extracted
    extracted="$(extract_cloudflare_tunnel_token "$input")"
    if is_valid_cloudflare_tunnel_token "$extracted"; then
      printf -v CLOUDFLARE_TUNNEL_TOKEN '%s' "$extracted"
      return
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
  wizard_header "Mensageria (NATS)" "$(wizard_step_label "8/9" "7/8")"
  echo "Configure as credenciais e hosts do NATS local."
  echo "Esses dados sao usados pela API e agentes para mensagens."
  echo "Se usuario ou senha ficarem em branco, o instalador gera valores aleatorios."
  echo "JetStream para replay de comandos fan-out e configurado automaticamente."
  echo "Para ajustar TTL/limites, use as variaveis NATS_JS_* no arquivo de config."
  echo "----------------------------------------"

  prompt_nats_user
  prompt_nats_password
  prompt_if_empty NATS_AUTH_USER "Usuario de bypass da API no NATS (auth_users)" 0 "$NATS_USER"
  prompt_if_empty NATS_AUTH_PASSWORD "Senha de bypass da API no NATS" 1 "$NATS_PASSWORD"
  prompt_if_empty NATS_BIND_HOST "Host de bind do NATS (ex: 0.0.0.0 ou 127.0.0.1)" 0 "0.0.0.0"
  prompt_if_empty NATS_MONITOR_HOST "Host do monitor HTTP do NATS (porta 8222)" 0 "127.0.0.1"
  # Aceita y/n (padrao dos demais prompts); normalize_nats_settings converte para 1/0.
  prompt_if_empty NATS_AUTH_CALLOUT_ENABLED "Habilitar NATS auth callout? (y/n)" 0 "y"
  prompt_if_empty NATS_AUTH_CALLOUT_SUBJECT "Subject do auth callout" 0 "\$SYS.REQ.USER.AUTH"
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
  wizard_header "Sessões remotas" "$(wizard_step_label "10/10" "9/9")"
  echo "Duração máxima de uma sessão remota sem precisar reconectar."
  echo "Enquanto o navegador estiver saudável a sessão se renova sozinha;"
  echo "ao atingir este teto ela é encerrada. Padrão: 1 hora."
  echo "----------------------------------------"

  local current="${REMOTE_SESSION_MAX_DURATION_HOURS:-1}"
  local answer=""
  while true; do
    if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
      REMOTE_SESSION_MAX_DURATION_HOURS="$current"
      return
    fi
    read -r -p "Duração máxima da sessão remota (horas, 1-24) [$current]: " answer
    answer="${answer:-$current}"
    if [[ "$answer" =~ ^[0-9]+$ ]] && (( answer >= 1 && answer <= 24 )); then
      REMOTE_SESSION_MAX_DURATION_HOURS="$answer"
      return
    fi
    echo "Informe um número inteiro entre 1 e 24."
  done
}

# ── Update policy wizard ───────────────────────────────────────────────────

prompt_selfupdate_settings() {
  wizard_header "Politica de atualizacao" "$(wizard_step_label "9/10" "8/9")"
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

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    DISCOVERY_CLEAN_BUILD="${DISCOVERY_CLEAN_BUILD:-1}"
    DISCOVERY_APPLY_SYSTEM_UPDATES="${current_apply_updates:-1}"
    case "$DISCOVERY_APPLY_SYSTEM_UPDATES" in 0|1) ;; *) DISCOVERY_APPLY_SYSTEM_UPDATES="1" ;; esac
    return
  fi

  # Interativo: Enter mantem o valor atual (S em instalacao nova, N quando ja
  # estava desabilitado). Um valor explicito no --config/ambiente pula a
  # pergunta.
  if [[ -z "${DISCOVERY_APPLY_SYSTEM_UPDATES:-}" ]]; then
    local apply_updates_default="S"
    [[ "$current_apply_updates" == "0" ]] && apply_updates_default="N"
    echo
    echo "Verificar e instalar tambem os updates do SO (apt-get update + upgrade)?"
    echo "Vale para esta instalacao e para os updates manuais seguintes."
    local apply_updates_choice
    read -r -p "Atualizar o SO automaticamente (S/n) [$apply_updates_default]: " apply_updates_choice
    apply_updates_choice="${apply_updates_choice:-$apply_updates_default}"
    apply_updates_choice="$(printf '%s' "$apply_updates_choice" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    case "$apply_updates_choice" in
      n|nao|não|no|0) DISCOVERY_APPLY_SYSTEM_UPDATES="0" ;;
      *)          DISCOVERY_APPLY_SYSTEM_UPDATES="1" ;;
    esac
  fi

  if [[ -z "${DISCOVERY_CLEAN_BUILD:-}" ]]; then
    echo
    echo "Modo de build para os updates manuais:"
    echo "  1) Limpo (remove cache antes do build)"
    echo "  2) Incremental (mais rapido, sem limpeza de cache)"
    local clean_build_choice
    read -r -p "Escolha [1]: " clean_build_choice
    clean_build_choice="${clean_build_choice:-1}"
    case "$clean_build_choice" in
      2) DISCOVERY_CLEAN_BUILD="0" ;;
      *) DISCOVERY_CLEAN_BUILD="1" ;;
    esac
  fi
}

# ── TLS certificate wizard ─────────────────────────────────────────────────

prompt_tls_certificate_configuration() {
  load_existing_tls_defaults
  wizard_header "Certificado TLS" "$(wizard_step_label "6/9" "5/8")"
  echo "1) self-signed  - certificado local gerado pelo instalador"
  echo "2) zerossl-acme - ZeroSSL via ACME com validacao DNS manual"
  echo "3) letsencrypt-acme - Let's Encrypt via ACME com validacao DNS manual"
  echo "----------------------------------------"

  prompt_if_empty TLS_CERT_PROVIDER "Modo de certificado TLS (1/2/3 ou self-signed/zerossl-acme/letsencrypt-acme)" 0 "self-signed"
  case "$(printf '%s' "$TLS_CERT_PROVIDER" | tr '[:upper:]' '[:lower:]')" in
    1) TLS_CERT_PROVIDER="self-signed" ;;
    2) TLS_CERT_PROVIDER="zerossl-acme" ;;
    3) TLS_CERT_PROVIDER="letsencrypt-acme" ;;
  esac
  normalize_tls_certificate_provider

  if [[ "$TLS_CERT_PROVIDER" == "zerossl-acme" ]]; then
    prompt_zerossl_acme_configuration
  elif [[ "$TLS_CERT_PROVIDER" == "letsencrypt-acme" ]]; then
    prompt_letsencrypt_acme_configuration
  fi
}

prompt_zerossl_acme_configuration() {
  echo
  echo "ZeroSSL ACME usa desafio DNS-01. O instalador exibira o registro TXT,"
  echo "aguardara sua confirmacao e validara o DNS com dig antes de continuar."
  echo
  prompt_if_empty ZEROSSL_CERT_DOMAIN "Dominio do certificado" 0 "$(resolve_fido2_server_domain)"
  if [[ -z "${ZEROSSL_CERT_ALT_DOMAINS:-}" && "$NON_INTERACTIVE" -eq 0 ]]; then
    read -r -p "SANs adicionais separados por virgula (opcional): " ZEROSSL_CERT_ALT_DOMAINS
  fi
  ZEROSSL_CERT_ALT_DOMAINS="${ZEROSSL_CERT_ALT_DOMAINS:-}"
  prompt_if_empty ZEROSSL_ACME_EMAIL "Email da conta ZeroSSL/ACME"
  prompt_if_empty ZEROSSL_ACME_EAB_KID "ZeroSSL EAB KID"
  prompt_if_empty ZEROSSL_ACME_EAB_HMAC_KEY "ZeroSSL EAB HMAC Key"
  prompt_if_empty ZEROSSL_DNS_RESOLVERS "Resolvers para validar DNS (separados por virgula)" 0 "1.1.1.1,8.8.8.8"
  prompt_if_empty ZEROSSL_DNS_PROPAGATION_TIMEOUT_SECONDS "Timeout de propagacao DNS em segundos" 0 "600"
  prompt_if_empty ZEROSSL_DNS_POLL_INTERVAL_SECONDS "Intervalo de consulta DNS em segundos" 0 "15"
  ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY="${ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY:-30}"
  echo "Renovacao recomendada: checar diariamente e renovar quando faltar ate ${ZEROSSL_RENEW_DAYS_BEFORE_EXPIRY} dias."
  prompt_if_empty ZEROSSL_AUTO_RENEW_ENABLED "Criar timer automatico de renovacao ZeroSSL? (1/0)" 0 "1"
  normalize_zerossl_auto_renew_enabled
  normalize_tls_certificate_provider
}

prompt_letsencrypt_acme_configuration() {
  echo
  echo "Let's Encrypt ACME usa desafio DNS-01. O instalador exibira o registro TXT,"
  echo "aguardara sua confirmacao e validara o DNS com dig antes de continuar."
  echo "Nao requer EAB (diferente do ZeroSSL)."
  echo
  prompt_if_empty LETSENCRYPT_CERT_DOMAIN "Dominio do certificado" 0 "$(resolve_fido2_server_domain)"
  if [[ -z "${LETSENCRYPT_CERT_ALT_DOMAINS:-}" && "$NON_INTERACTIVE" -eq 0 ]]; then
    read -r -p "SANs adicionais separados por virgula (opcional): " LETSENCRYPT_CERT_ALT_DOMAINS
  fi
  LETSENCRYPT_CERT_ALT_DOMAINS="${LETSENCRYPT_CERT_ALT_DOMAINS:-}"
  prompt_if_empty LETSENCRYPT_ACME_EMAIL "Email da conta Let's Encrypt/ACME"
  prompt_if_empty LETSENCRYPT_DNS_RESOLVERS "Resolvers para validar DNS (separados por virgula)" 0 "1.1.1.1,8.8.8.8"
  prompt_if_empty LETSENCRYPT_DNS_PROPAGATION_TIMEOUT_SECONDS "Timeout de propagacao DNS em segundos" 0 "600"
  prompt_if_empty LETSENCRYPT_DNS_POLL_INTERVAL_SECONDS "Intervalo de consulta DNS em segundos" 0 "15"
  LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY="${LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY:-30}"
  echo "Renovacao recomendada: checar diariamente e renovar quando faltar ate ${LETSENCRYPT_RENEW_DAYS_BEFORE_EXPIRY} dias."
  prompt_if_empty LETSENCRYPT_AUTO_RENEW_ENABLED "Criar timer automatico de renovacao Let's Encrypt? (1/0)" 0 "1"
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

  if [[ -n "$current_value" ]]; then
    "$validator" "$current_value" || fail "$error_text"
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    if [[ -n "$default_value" ]]; then
      printf -v "$var_name" '%s' "$default_value"
      "$validator" "$default_value" || fail "$error_text"
      return
    fi
    fail "Variavel obrigatoria ausente para modo nao interativo: $var_name"
  fi

  local input=""
  while true; do
    if [[ -n "$default_value" ]]; then
      read -r -p "$prompt_text [$default_value]: " input
      input="${input:-$default_value}"
    else
      read -r -p "$prompt_text: " input
    fi
    input="$(printf '%s' "$input" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    if [[ -n "$input" ]] && "$validator" "$input"; then
      printf -v "$var_name" '%s' "$input"
      return
    fi
    echo "$error_text" >&2
  done
}

# Selecao do modo de storage. Default rustfs; valores fora de 1/2/3 (ou dos
# nomes) sao re-perguntados em vez de derrubar o instalador.
prompt_object_storage_mode() {
  if [[ -n "${DISCOVERY_OBJECT_STORAGE_MODE:-}" ]]; then
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    DISCOVERY_OBJECT_STORAGE_MODE="rustfs"
    return
  fi

  local input=""
  while true; do
    read -r -p "Storage de arquivos (1/2/3 ou rustfs/external/skip) [rustfs]: " input
    input="${input:-rustfs}"
    input="$(printf '%s' "$input" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    case "$input" in
      1|local|rustfs) DISCOVERY_OBJECT_STORAGE_MODE="rustfs"; return ;;
      2|external|s3)  DISCOVERY_OBJECT_STORAGE_MODE="external"; return ;;
      3|skip|none)    DISCOVERY_OBJECT_STORAGE_MODE="skip"; return ;;
      *) echo "Opcao invalida: '$input'. Use 1 (RustFS local), 2 (provedor externo) ou 3 (configurar depois)." >&2 ;;
    esac
  done
}

prompt_object_storage_configuration() {
  load_existing_object_storage_defaults
  wizard_header "Storage de arquivos (anexos/prints)" "$(wizard_step_label "8/10" "6/9")"
  echo "1) RustFS local      - instala o servidor de objetos nesta maquina (S3 compativel)."
  echo "2) Provedor externo  - S3 compativel ja existente (R2/S3/MinIO/...)."
  echo "3) Configurar depois - nao instala e nao altera nada."
  echo "----------------------------------------"
  prompt_object_storage_mode

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
      "Bucket invalido: use minusculas, digitos, ponto e hifen (3-63, sem comecar/terminar com ponto ou hifen)."
    prompt_validated_value DISCOVERY_OBJECT_STORAGE_ENDPOINT \
      "Endpoint publico do storage (host ou https://host, SEM path)" \
      "$(resolve_object_storage_default_endpoint)" \
      is_valid_object_storage_endpoint \
      "Endpoint invalido: use host[:porta] ou http(s)://host[:porta], SEM path."
    prompt_if_empty DISCOVERY_OBJECT_STORAGE_REGION "Regiao S3" 0 "us-east-1"
    prompt_if_empty RUSTFS_VERSION "Versao do RustFS a instalar (fixa, sem auto-update)" 0 "1.0.1"
    prompt_if_empty DISCOVERY_OBJECT_STORAGE_NGINX_ENABLED "Publicar o bucket pelo nginx? (y/N)" 0 "y"
    if [[ "$NON_INTERACTIVE" -eq 0 && -z "${RUSTFS_SHA256:-}" ]]; then
      read -r -p "sha256 do pacote do RustFS (opcional, recomendado; Enter = nao verificar): " RUSTFS_SHA256
    fi
  elif [[ "$DISCOVERY_OBJECT_STORAGE_MODE" == "external" ]]; then
    echo
    echo "Provedor externo: informe endpoint/bucket/credenciais. Nada sera instalado nesta maquina."
    prompt_validated_value DISCOVERY_OBJECT_STORAGE_ENDPOINT \
      "Endpoint do storage (host ou http(s)://host[:porta], SEM path)" "" \
      is_valid_object_storage_endpoint \
      "Endpoint invalido: use host[:porta] ou http(s)://host[:porta], SEM path."
    prompt_validated_value DISCOVERY_OBJECT_STORAGE_BUCKET "Nome do bucket" "" \
      is_valid_object_storage_bucket \
      "Bucket invalido: use minusculas, digitos, ponto e hifen (3-63, sem comecar/terminar com ponto ou hifen)."
    prompt_if_empty DISCOVERY_OBJECT_STORAGE_REGION "Regiao S3" 0 "us-east-1"
    prompt_if_empty DISCOVERY_OBJECT_STORAGE_ACCESS_KEY "Access key"
    prompt_if_empty DISCOVERY_OBJECT_STORAGE_SECRET_KEY "Secret key" 1
    prompt_if_empty DISCOVERY_OBJECT_STORAGE_USE_PATH_STYLE "Usar path-style? (y/N)" 0 "y"
    prompt_if_empty DISCOVERY_OBJECT_STORAGE_SSL_VERIFY "Verificar certificado TLS do storage? (y/N)" 0 "y"
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
  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then return; fi

  wizard_header "Revisao final" "$(wizard_step_label "10/10" "9/9")"
  echo "Revise TODOS os campos abaixo antes de executar. Nada foi alterado no servidor ainda."
  echo "----------------------------------------"
  print_selected_configuration_summary
  echo
  echo "Pre-requisitos/acoes que serao executados:"
  print_prerequisite_plan_summary
  echo

  local confirm
  read -r -p "Confirma os dados e inicia a instalacao? (s/N): " confirm
  confirm="$(printf '%s' "$confirm" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  case "$confirm" in
    s|sim|y|yes) ;;
    *) fail "Instalacao cancelada pelo usuario." ;;
  esac
}
