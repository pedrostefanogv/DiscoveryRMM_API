#!/usr/bin/env bash
# Testes offline das melhorias, suporte ARM64, otimizacoes e navegacao
# interativa (voltar/re-prompt) dos scripts em scripts/linux/.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_DIR="$(cd "$HERE/.." && pwd)"
FAILED=0

ok()  { printf "  ok    | %s\n" "$*"; }
bad() { printf "  FALHA | %s\n" "$*"; FAILED=1; }
expect_eq() { [[ "$1" == "$2" ]] && ok "$3 (= $1)" || bad "$3: esperado [$2], obtido [$1]"; }

TEST_TMP="$(mktemp -d)"
cleanup() { rm -rf "$TEST_TMP"; }
trap cleanup EXIT

echo "== 1. Validacao de sintaxe (bash -n) em todos os .sh =="
while IFS= read -r sh_file; do
  if bash -n "$sh_file" 2>"$TEST_TMP/syntax.err"; then
    ok "sintaxe OK: ${sh_file#"$LINUX_DIR"/}"
  else
    bad "erro de sintaxe em ${sh_file#"$LINUX_DIR"/}: $(cat "$TEST_TMP/syntax.err")"
  fi
done < <(find "$LINUX_DIR" -type f -name '*.sh' | sort)

# Isola PATH para testes offline com stubs
BIN="$TEST_TMP/bin"
SHIM="$TEST_TMP/shim"
mkdir -p "$BIN" "$SHIM"
for tool in bash sh env grep sed awk cut tr head tail sort uniq wc cat printf \
            mktemp rm mv cp chmod chown install touch find ls dirname basename \
            uname dpkg apt-get apt-cache tee true false sleep seq date readlink; do
  for dir in /usr/bin /bin /usr/sbin /sbin; do
    if [[ -x "$dir/$tool" ]]; then ln -sf "$dir/$tool" "$SHIM/$tool"; break; fi
  done
done
export TEST_TMP BIN
export PATH="$BIN:$SHIM"

# Carrega common.sh
source "$LINUX_DIR/lib/common.sh"

echo "== 2. Mapeamento de arquitetura ARM64/x64 =="
expect_eq "$(map_arch_to_dotnet_runtime amd64)"   "linux-x64"   "amd64 -> linux-x64"
expect_eq "$(map_arch_to_dotnet_runtime x86_64)"  "linux-x64"   "x86_64 -> linux-x64"
expect_eq "$(map_arch_to_dotnet_runtime arm64)"   "linux-arm64" "arm64 -> linux-arm64"
expect_eq "$(map_arch_to_dotnet_runtime aarch64)" "linux-arm64" "aarch64 -> linux-arm64"

expect_eq "$(map_arch_to_dotnet_install_arch amd64)"   "x64"   "dotnet-install amd64 -> x64"
expect_eq "$(map_arch_to_dotnet_install_arch x86_64)"  "x64"   "dotnet-install x86_64 -> x64"
expect_eq "$(map_arch_to_dotnet_install_arch arm64)"   "arm64" "dotnet-install arm64 -> arm64"
expect_eq "$(map_arch_to_dotnet_install_arch aarch64)" "arm64" "dotnet-install aarch64 -> arm64"
expect_eq "$(map_arch_to_dotnet_install_arch armv7l)"  "arm"   "dotnet-install armv7l -> arm"

echo "== 3. load_env_keys_if_unset (leitura em lote e preservacao) =="
cat > "$TEST_TMP/test.env" <<'ENVEOF'
# comentario
KEY_A=valor_a
KEY_B=valor_b_com=igual
KEY_CR=valor_cr
ENVEOF
sed -i 's/valor_cr/valor_cr\r/' "$TEST_TMP/test.env"

sudo() { "$@"; }
KEY_A="ja_definida"
KEY_B=""
KEY_CR=""
load_env_keys_if_unset "$TEST_TMP/test.env" KEY_A KEY_B KEY_CR KEY_MISSING
expect_eq "$KEY_A" "ja_definida" "preserva variavel ja definida"
expect_eq "$KEY_B" "valor_b_com=igual" "preserva '=' no valor"
expect_eq "$KEY_CR" "valor_cr" "remove CR do final da linha"
expect_eq "${KEY_MISSING:-}" "" "chave ausente permanece vazia"

echo "== 4. ensure_dotnet_sdk em ARM64 (fallback dotnet-install.sh com --architecture arm64) =="
cat > "$BIN/uname" <<'STUB'
#!/bin/bash
echo "aarch64"
STUB
cat > "$BIN/dpkg" <<'STUB'
#!/bin/bash
if [[ "$1" == "--print-architecture" ]]; then echo "arm64"; exit 0; fi
exit 0
STUB
cat > "$BIN/apt-cache" <<'STUB'
#!/bin/bash
exit 1
STUB
cat > "$BIN/curl" <<'STUB'
#!/bin/bash
out=""
prev=""
for a in "$@"; do
  if [[ "$prev" == "-o" ]]; then out="$a"; fi
  prev="$a"
done
case "$*" in
  *packages-microsoft-prod.deb*) exit 22 ;;
  *dotnet-install.sh*)
    cat > "$out" <<'INSTALLER'
#!/bin/bash
echo "dotnet-install $*" >> "$TEST_TMP/dotnet_install.log"
exit 0
INSTALLER
    exit 0 ;;
esac
exit 0
STUB
cat > "$BIN/sudo" <<'STUB'
#!/bin/bash
case "$1" in
  env) shift; "$@" ;;
  mkdir|ln|tee|rm|test)
    if [[ "$1" == "mkdir" || "$1" == "ln" || "$1" == "rm" || "$1" == "tee" ]]; then
      exit 0
    fi
    "$@" ;;
  *dotnet-install.sh)
    "$@" ;;
  *) "$@" ;;
esac
STUB
chmod +x "$BIN/uname" "$BIN/dpkg" "$BIN/apt-cache" "$BIN/curl" "$BIN/sudo"

# Simula ausencia de dotnet antes, e presenca apos o fallback
cat > "$BIN/command_check" <<'STUB'
#!/bin/bash
exit 0
STUB
source "$LINUX_DIR/lib/install.sh"
# Redefine sudo apenas para interceptar chamadas ao sistema durante o teste de ensure_dotnet_sdk
sudo() {
  case "$1" in
    mkdir|ln|rm) return 0 ;;
    tee) cat >/dev/null; return 0 ;;
    test) return 1 ;;
    env) shift; "$@" ;;
    *) "$@" ;;
  esac
}
# Cria stub dotnet apos dotnet-install.sh rodar
cat > "$TEST_TMP/make_dotnet_stub.sh" <<'STUB'
#!/bin/bash
echo "dotnet-install $*" >> "$TEST_TMP/dotnet_install.log"
cat > "$TEST_TMP/bin/dotnet" <<'DNET'
#!/bin/bash
echo "10.0.100 [/usr/share/dotnet/sdk]"
DNET
chmod +x "$TEST_TMP/bin/dotnet"
STUB
cat > "$BIN/curl" <<'STUB'
#!/bin/bash
out=""
prev=""
for a in "$@"; do
  if [[ "$prev" == "-o" ]]; then out="$a"; fi
  prev="$a"
done
case "$*" in
  *packages-microsoft-prod.deb*) exit 22 ;;
  *dotnet-install.sh*)
    cp "$TEST_TMP/make_dotnet_stub.sh" "$out"
    exit 0 ;;
esac
exit 0
STUB
chmod +x "$BIN/curl"

rm -f "$BIN/dotnet" "$TEST_TMP/dotnet_install.log"
ensure_dotnet_sdk >/dev/null 2>&1
if [[ -f "$TEST_TMP/dotnet_install.log" ]] && grep -q -- "--architecture arm64" "$TEST_TMP/dotnet_install.log"; then
  ok "ensure_dotnet_sdk passou --architecture arm64 no fallback dotnet-install.sh"
else
  bad "ensure_dotnet_sdk nao passou --architecture arm64: $(cat "$TEST_TMP/dotnet_install.log" 2>/dev/null || echo vazio)"
fi

echo "== 5. Navegacao voltar/re-prompt no prompts.sh e normalize.sh =="
NON_INTERACTIVE=0
WIZARD_SHOW_BACK_HINT=1
source "$LINUX_DIR/lib/normalize.sh"
source "$LINUX_DIR/lib/prompts.sh"

# 5a. prompt_if_empty retorna WIZARD_BACK_RC (10) ao digitar voltar ou <
rc=0
TEST_VAR=""
WIZARD_REPROMPT=0
prompt_if_empty TEST_VAR "Campo teste" 0 "padrao" <<< "voltar" >/dev/null 2>&1 || rc=$?
expect_eq "$rc" "$WIZARD_BACK_RC" "prompt_if_empty 'voltar' retorna WIZARD_BACK_RC"

rc=0
prompt_if_empty TEST_VAR "Campo teste" 0 "padrao" <<< "<" >/dev/null 2>&1 || rc=$?
expect_eq "$rc" "$WIZARD_BACK_RC" "prompt_if_empty '<' retorna WIZARD_BACK_RC"

# 5b. WIZARD_REPROMPT=1 permite alterar valor ja preenchido e Enter mantem o atual
TEST_VAR="valor_antigo"
WIZARD_REPROMPT=1
prompt_if_empty TEST_VAR "Campo teste" 0 "padrao" <<< "valor_novo" >/dev/null 2>&1
expect_eq "$TEST_VAR" "valor_novo" "WIZARD_REPROMPT=1 atualiza valor existente"

WIZARD_REPROMPT=1
prompt_if_empty TEST_VAR "Campo teste" 0 "padrao" <<< "" >/dev/null 2>&1
expect_eq "$TEST_VAR" "valor_novo" "WIZARD_REPROMPT=1 com Enter mantem valor atual"
WIZARD_REPROMPT=0

# 5c. Entrada invalida nao aborta (faz loop ate receber valor valido)
TEST_REPO=""
prompt_repo_url TEST_REPO "Repo" "" <<< $'url-invalida\nhttps://github.com/org/repo.git' >/dev/null 2>&1
expect_eq "$TEST_REPO" "https://github.com/org/repo.git" "prompt_repo_url repete apos URL invalida sem abortar"

# 5d. select_install_branch suporta voltar e branch custom invalida sem abortar
DISCOVERY_GIT_BRANCH=""
rc=0
select_install_branch <<< "voltar" >/dev/null 2>&1 || rc=$?
expect_eq "$rc" "$WIZARD_BACK_RC" "select_install_branch 'voltar' retorna WIZARD_BACK_RC"

DISCOVERY_GIT_BRANCH=""
select_install_branch <<< $'5\n-invalida\nfeature/arm64' >/dev/null 2>&1
expect_eq "$DISCOVERY_GIT_BRANCH" "feature/arm64" "select_install_branch repete apos branch custom invalida"

# 5e. confirm_installation suporta (v) voltar e (e) editar etapa especifica
print_selected_configuration_summary() { echo "resumo mock"; }
print_prerequisite_plan_summary() { echo "prereqs mock"; }

rc=0
confirm_installation <<< "v" >/dev/null 2>&1 || rc=$?
expect_eq "$rc" "$WIZARD_BACK_RC" "confirm_installation 'v' retorna WIZARD_BACK_RC"

rc=0
confirm_installation <<< $'e\n4' >/dev/null 2>&1 || rc=$?
expect_eq "$rc" "20" "confirm_installation 'e' + '4' retorna 20"
expect_eq "$WIZARD_JUMP_STEP" "4" "confirm_installation define WIZARD_JUMP_STEP=4"

rc=0
confirm_installation <<< "s" >/dev/null 2>&1 || rc=$?
expect_eq "$rc" "0" "confirm_installation 's' retorna 0"

echo "== 6. Bootstrap: choose_branch_interactive retorna 10 ao digitar voltar =="
BOOTSTRAP_HELPERS="$TEST_TMP/bootstrap_funcs.sh"
sed -n '/^BOOTSTRAP_BACK_RC=/,/^choose_operation_mode_interactive()/p' "$LINUX_DIR/bootstrap_install_discovery.sh" \
  | sed '$d' > "$BOOTSTRAP_HELPERS"
# Stubs minimos para carregar helpers do bootstrap
bootstrap_header() { :; }
fail() { echo "FAIL: $*" >&2; return 1; }
source "$BOOTSTRAP_HELPERS"

rc=0
out_branch="$(choose_branch_interactive "full" <<< "voltar" 2>/dev/null)" || rc=$?
expect_eq "$rc" "10" "bootstrap choose_branch_interactive 'voltar' retorna 10"

echo
if [[ "$FAILED" -eq 0 ]]; then
  echo "TODOS OS TESTES DE MELHORIAS PASSARAM"
else
  echo "HOUVE FALHAS NOS TESTES"
  exit 1
fi
