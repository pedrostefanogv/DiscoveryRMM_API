# Discovery RMM installer – bootstrap do usuario instalador (com sudo)
#
# Quando o instalador roda como root, a instalacao inteira (arquivos, chaves e
# servicos) fica presa ao root. Para evitar isso, criamos ou reutilizamos um
# usuario comum com sudo e reexecutamos o instalador como esse usuario, usando
# SUDO_ASKPASS para que as chamadas sudo nao fiquem esperando senha no terminal.
#
# Requer: common.sh (log, warn, fail, wizard_header, wizard_step_label,
# generate_random_password, require_cmd) e as variaveis SCRIPT_PATH e
# ORIGINAL_ARGS definidas pelo entrypoint (install_discovery_server.sh).

is_valid_installer_user_name() {
  local user_name="${1:-}"
  [[ "$user_name" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || return 1
  [[ "$user_name" != "root" ]] || return 1
  return 0
}

validate_installer_user_name() {
  local user_name="${1:-}"
  if ! is_valid_installer_user_name "$user_name"; then
    fail "Nome de usuario invalido: '${user_name}'. Use minusculas, digitos, '_' e '-' (max 32, iniciando por letra ou '_') e nao use 'root'."
  fi
}

validate_installer_user_password() {
  local user_password="${1:-}"
  (( ${#user_password} >= 8 )) || fail "Senha do usuario instalador precisa ter pelo menos 8 caracteres."
  (( ${#user_password} <= 128 )) || fail "Senha do usuario instalador muito longa (max 128 caracteres)."
  [[ "$user_password" != *:* ]] || fail "Senha do usuario instalador nao pode conter ':' (quebra o chpasswd)."
  [[ ! "$user_password" =~ [[:cntrl:]] ]] || fail "Senha do usuario instalador nao pode conter caracteres de controle."
}

prompt_installer_user_name() {
  DISCOVERY_INSTALL_USER="${DISCOVERY_INSTALL_USER:-discovery}"

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    validate_installer_user_name "$DISCOVERY_INSTALL_USER"
    return
  fi

  local input=""
  while true; do
    read -r -p "Nome do usuario instalador (com sudo) [$DISCOVERY_INSTALL_USER]: " input
    input="$(printf '%s' "${input:-$DISCOVERY_INSTALL_USER}" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    if is_valid_installer_user_name "$input"; then
      DISCOVERY_INSTALL_USER="$input"
      return
    fi
    echo "Nome invalido: use minusculas, digitos, '_' e '-' (max 32, iniciando por letra) e nao use 'root'. Tente novamente." >&2
  done
}

# Senha do usuario instalador — obrigatoria, porque e a senha do sudo dele.
#   - Enter em branco: gera uma senha forte e mostra uma unica vez (nunca aborta).
#   - Senha informada: pede confirmacao e repete se estiver curta ou divergente.
prompt_installer_user_password() {
  if [[ -n "${DISCOVERY_INSTALL_USER_PASSWORD:-}" ]]; then
    validate_installer_user_password "$DISCOVERY_INSTALL_USER_PASSWORD"
    DISCOVERY_INSTALL_USER_PASSWORD_AUTO="${DISCOVERY_INSTALL_USER_PASSWORD_AUTO:-0}"
    return
  fi

  if [[ "$NON_INTERACTIVE" -eq 1 ]]; then
    fail "Variavel obrigatoria ausente para modo nao interativo: DISCOVERY_INSTALL_USER_PASSWORD"
  fi

  local input="" confirm=""
  while true; do
    read -r -s -p "Senha do usuario instalador (obrigatoria; Enter = gerar senha forte): " input
    printf '\n'

    if [[ -z "$input" ]]; then
      local generated
      generated="$(generate_random_password 24)"
      DISCOVERY_INSTALL_USER_PASSWORD="$generated"
      DISCOVERY_INSTALL_USER_PASSWORD_AUTO=1
      echo
      echo "Senha gerada para o usuario '${DISCOVERY_INSTALL_USER}':"
      echo "  ${generated}"
      echo "Anote agora: ela e a senha do sudo desse usuario e nao sera mostrada novamente."
      echo
      return
    fi

    if (( ${#input} < 8 )); then
      echo "Senha muito curta (minimo 8 caracteres). Tente novamente." >&2
      continue
    fi
    if (( ${#input} > 128 )); then
      echo "Senha muito longa (maximo 128 caracteres). Tente novamente." >&2
      continue
    fi
    if [[ "$input" == *:* || "$input" =~ [[:cntrl:]] ]]; then
      echo "Senha invalida: nao use ':' nem caracteres de controle. Tente novamente." >&2
      continue
    fi

    read -r -s -p "Confirme a senha do usuario instalador: " confirm
    printf '\n'
    if [[ "$input" != "$confirm" ]]; then
      echo "As senhas nao conferem. Tente novamente." >&2
      continue
    fi

    DISCOVERY_INSTALL_USER_PASSWORD="$input"
    DISCOVERY_INSTALL_USER_PASSWORD_AUTO=0
    return
  done
}

ensure_installer_user_from_root() {
  local user_name="$1"
  local user_password="$2"

  require_cmd useradd
  require_cmd usermod
  require_cmd chpasswd

  if ! getent group sudo >/dev/null 2>&1; then
    fail "Grupo sudo nao encontrado. O instalador espera Debian/Ubuntu com sudo."
  fi

  if id -u "$user_name" >/dev/null 2>&1; then
    log "Usuario instalador '$user_name' ja existe; atualizando senha e garantindo o grupo sudo."
  else
    log "Criando usuario instalador '$user_name'."
    useradd --create-home --shell /bin/bash "$user_name"
  fi

  printf '%s:%s\n' "$user_name" "$user_password" | chpasswd
  usermod -aG sudo "$user_name"
}

# O sudo reseta o ambiente ao trocar de usuario. Encaminha as variaveis
# DISCOVERY_* do processo root (branch, runtime e o que veio do bootstrap)
# para o instalador reexecutado como usuario instalador.
collect_root_bootstrap_env() {
  local var
  while IFS= read -r var; do
    case "$var" in
      # Ja passadas explicitamente para o filho; evita duplicar no env.
      DISCOVERY_ROOT_BOOTSTRAPPED|DISCOVERY_INSTALL_USER|DISCOVERY_INSTALL_USER_PASSWORD|DISCOVERY_INSTALL_USER_PASSWORD_AUTO|DISCOVERY_SUDO_ASKPASS_PASSWORD) continue ;;
      DISCOVERY_*) printf '%s=%s\n' "$var" "${!var}" ;;
    esac
  done < <(compgen -v)
}

bootstrap_root_execution() {
  wizard_header "Usuario instalador (com sudo)" "$(wizard_step_label "2/8" "1/7")"
  echo "Este usuario executa o instalador e as operacoes via sudo."
  echo "Se ja existir, sera reutilizado."
  echo
  echo "[install][aviso] O instalador foi executado como root."
  echo "[install][aviso] Para evitar uma instalacao presa ao root, sera criado ou usado um usuario comum com sudo para continuar."
  echo "[install][aviso] Se o usuario ja existir, a senha dele sera ATUALIZADA para a senha informada aqui."
  echo

  # Em automacao (--non-interactive) o usuario instalador e opt-in: sem a senha
  # definida, seguimos como root para nao quebrar scripts existentes.
  if [[ "$NON_INTERACTIVE" -eq 1 && -z "${DISCOVERY_INSTALL_USER_PASSWORD:-}" ]]; then
    warn "Modo --non-interactive sem DISCOVERY_INSTALL_USER_PASSWORD: seguindo como root (instalacao presa ao root). Defina a variavel para usar um usuario instalador."
    return 0
  fi

  prompt_installer_user_name

  # O --config e lido de novo pelo processo reexecutado como usuario
  # instalador; se nao for legivel, seguimos como root para nao perder a
  # configuracao (ex.: arquivo 0600 pertencente ao root). Precisa vir depois de
  # prompt_installer_user_name, que e quem define DISCOVERY_INSTALL_USER.
  if [[ -n "${CONFIG_FILE:-}" ]]; then
    if ! id -u "$DISCOVERY_INSTALL_USER" >/dev/null 2>&1 \
       || ! sudo -u "$DISCOVERY_INSTALL_USER" test -r "$CONFIG_FILE"; then
      warn "Arquivo --config '$CONFIG_FILE' nao sera legivel pelo usuario '$DISCOVERY_INSTALL_USER'; seguindo como root para preservar a configuracao."
      return 0
    fi
  fi

  prompt_installer_user_password

  ensure_installer_user_from_root "$DISCOVERY_INSTALL_USER" "$DISCOVERY_INSTALL_USER_PASSWORD"

  # O instalador (e os libs que ele carrega) precisa ser legivel pelo usuario.
  # O checkout temporario do bootstrap e liberado por ele; um checkout manual
  # pode estar restrito (ex.: /root/...).
  if ! sudo -u "$DISCOVERY_INSTALL_USER" test -r "$SCRIPT_PATH"; then
    fail "O usuario '$DISCOVERY_INSTALL_USER' nao consegue ler '$SCRIPT_PATH'. Execute pelo bootstrap (ele libera o checkout temporario) ou torne o checkout legivel (chmod -R a+rX)."
  fi

  local askpass_file
  askpass_file="$(mktemp /tmp/discovery-install-sudo-askpass.XXXXXX)"
  cat > "$askpass_file" <<'EOF'
#!/usr/bin/env sh
printf '%s\n' "$DISCOVERY_SUDO_ASKPASS_PASSWORD"
EOF
  chown "$DISCOVERY_INSTALL_USER:$DISCOVERY_INSTALL_USER" "$askpass_file"
  chmod 700 "$askpass_file"

  local -a forward_env=()
  local env_line=""
  while IFS= read -r env_line; do
    [[ -n "$env_line" ]] && forward_env+=("$env_line")
  done < <(collect_root_bootstrap_env)

  local -a child_cmd=(
    env
    DISCOVERY_ROOT_BOOTSTRAPPED=1
    DISCOVERY_INSTALL_USER="$DISCOVERY_INSTALL_USER"
    DISCOVERY_INSTALL_USER_PASSWORD="$DISCOVERY_INSTALL_USER_PASSWORD"
    DISCOVERY_INSTALL_USER_PASSWORD_AUTO="${DISCOVERY_INSTALL_USER_PASSWORD_AUTO:-0}"
    DISCOVERY_SUDO_ASKPASS_PASSWORD="$DISCOVERY_INSTALL_USER_PASSWORD"
    SUDO_ASKPASS="$askpass_file"
  )
  if (( ${#forward_env[@]} > 0 )); then
    child_cmd+=("${forward_env[@]}")
  fi
  child_cmd+=(bash "$SCRIPT_PATH" "${ORIGINAL_ARGS[@]}")

  log "Reexecutando instalador como '$DISCOVERY_INSTALL_USER'."
  # O cwd do root (ex.: /root, 0700) nao e acessivel ao usuario instalador;
  # executa a partir de /tmp para nao herdar um diretorio inacessivel.
  cd /tmp 2>/dev/null || true
  set +e
  sudo -u "$DISCOVERY_INSTALL_USER" -H "${child_cmd[@]}"
  local exit_code=$?
  set -e

  rm -f "$askpass_file"
  exit "$exit_code"
}
