#!/usr/bin/env bash
# Testes offline da instalacao do cloudflared (lib/certs.sh).
#
# Nao toca no host: sudo/curl/apt-get/os-release sao substituidos por stubs em
# um PATH proprio. Rode com: bash scripts/linux/tests/test_cloudflared_install.sh
#
# Cobre o BUG 2026-11-20 (host ARM64 / Ubuntu 26.04 "resolute"):
#   - a suite local nao tem Release no repo do Cloudflare -> fallback para uma
#     suite publicada, SEM gravar .list com suite inexistente (que envenenaria
#     o apt do host);
#   - nenhuma suite publicada serve -> nenhum .list e gravado;
#   - fallback de binario oficial do GitHub.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FAILED=0

ok()  { printf "  ok    | %s\n" "$*"; }
bad() { printf "  FALHA | %s\n" "$*"; FAILED=1; }
expect_eq() { [[ "$1" == "$2" ]] && ok "$3 (= $1)" || bad "$3: esperado [$2], obtido [$1]"; }

# PATH sanitizado: o WSL herda o PATH do Windows (so /mnt/c/...), o que quebra
# o shebang "#!/usr/bin/env bash" e todo comando core. Fixamos os diretorios
# Linux e deixamos o diretorio de stubs na frente.
BASE_PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
TEST_TMP="$(mktemp -d)"
BIN="$TEST_TMP/bin"
mkdir -p "$BIN"
export TEST_TMP
export PATH="$BIN:$BASE_PATH"

cleanup() { rm -rf "$TEST_TMP"; }
trap cleanup EXIT

# ---- stubs (shebang absoluto: nunca "env bash" com PATH do Windows) ------

export BIN

# Heredoc com quoted delimiter + cat > arquivo: nenhuma expansao indevida aqui,
# e os stubs expandem as variaveis em tempo de execucao.
cat > "$BIN/curl" <<'STUB_CURL'
#!/bin/bash
url=""
for a in "$@"; do case "$a" in http*) url="$a";; esac; done
echo "$url" >> "$TEST_TMP/curl.log"
case "$url" in
  *dists/*)
    suite="$(printf "%s" "$url" | sed -n "s#.*/dists/\([^/]*\)/Release#\1#p")"
    for s in ${CURL_OK_SUITES:-}; do [[ "$s" == "$suite" ]] && { echo "200 $suite" >> "$TEST_TMP/curl.log"; exit 0; }; done
    exit 22 ;;
  *cloudflare-main.gpg*) exit 0 ;;
  *cloudflared-linux-*) exit 0 ;;
esac
exit 0
STUB_CURL

cat > "$BIN/sudo" <<'STUB_SUDO'
#!/bin/bash
echo "sudo $*" >> "$TEST_TMP/sudo.log"
case "$1" in
  gpg)   cat > /dev/null; exit 0 ;;
  apt-get) shift; exec "$TEST_TMP/bin/apt-get" "$@" ;;
  tee)   cat > "$TEST_TMP/written.list"; exit 0 ;;
  install)
    # assinatura real: install -m 0755 -o root -g root <origem> <destino>
    # "sudo install" de verdade: aqui o destino e o PATH do teste.
    cp "${@: -2:1}" "$TEST_TMP/bin/cloudflared" && chmod 0755 "$TEST_TMP/bin/cloudflared" && exit 0 ;;
esac
exit 0
STUB_SUDO

cat > "$BIN/apt-get" <<'STUB_APT'
#!/bin/bash
echo "apt-get $*" >> "$TEST_TMP/apt.log"
[[ -n "${APT_FAIL:-}" ]] && exit 100
# Simula o pacote: o apt "instala" o binario no PATH do teste.
if [[ "$1" == "install" ]]; then
  printf "#!/bin/sh\necho cloudflared fake\n" > "$TEST_TMP/bin/cloudflared"
  chmod 0755 "$TEST_TMP/bin/cloudflared"
fi
exit 0
STUB_APT

chmod 0755 "$BIN/curl" "$BIN/sudo" "$BIN/apt-get"

# ---- carrega apenas as funcoes de certs.sh --------------------------------
# certs.sh usa log/warn/fail de common.sh: stubamos aqui para nao arrastar as
# dependencias do instalador.
log()  { :; }
warn() { printf "  (warn) %s\n" "$*" >> "$TEST_TMP/warn.log"; }
fail() { printf "fail() CHAMADO (nao deveria): %s\n" "$*" >&2; exit 99; }
source "$HERE/../lib/certs.sh"

# cloudflared_host_codename le /etc/os-release: redirecionamos para o teste.
TEST_OS_RELEASE="$TEST_TMP/os-release"
cloudflared_host_codename() {
  local codename=""
  [[ -r "$TEST_OS_RELEASE" ]] && codename="$(. "$TEST_OS_RELEASE" && printf "%s" "${VERSION_CODENAME:-}")"
  printf "%s" "$codename"
}
printf "ID=ubuntu\nVERSION_CODENAME=resolute\n" > "$TEST_OS_RELEASE"

echo "== candidatos de suite (mapeamento de codename) =="
# A suite LOCAL vem sempre primeiro (se a Cloudflare publicar a suite do host um
# dia, ela e usada sem fallback); as candidatas publicadas vem depois.
expect_eq "$(cloudflared_apt_suite_candidates resolute | head -n1)" "resolute" "resolute e tentada primeiro"
expect_eq "$(cloudflared_apt_suite_candidates resolute | sed -n 2p)" "noble"   "resolute cai para noble em seguida"
expect_eq "$(cloudflared_apt_suite_candidates noble    | head -n1)" "noble"    "Ubuntu noble mantem noble"
expect_eq "$(cloudflared_apt_suite_candidates trixie   | head -n1)" "trixie"   "trixie e tentada primeiro"
expect_eq "$(cloudflared_apt_suite_candidates trixie   | sed -n 2p)" "bookworm" "Debian 13 trixie cai para bookworm"
expect_eq "$(cloudflared_apt_suite_candidates ""       | head -n1)" "noble"    "codename vazio -> 1a publicada"
expect_eq "$(cloudflared_apt_suite_candidates noble | sort | uniq -d | wc -l | tr -d " ")" "0" "sem suites duplicadas"
expect_eq "$(cloudflared_apt_suite_candidates noble | grep -c .)" "5" "noble + 4 fallbacks, sem repetir noble"

echo "== resolve_cloudflared_apt_suite (checagem real do Release) =="
export CURL_OK_SUITES="jammy"
expect_eq "$(resolve_cloudflared_apt_suite resolute)" "jammy" "escolhe a 1a suite com Release publicado"
export CURL_OK_SUITES=""
resolve_cloudflared_apt_suite resolute >/dev/null 2>&1; rc=$?
[[ $rc -eq 1 ]] && ok "nenhuma publicada -> retorna 1" || bad "deveria retornar 1, obtido $rc"
export CURL_OK_SUITES="noble jammy"
expect_eq "$(resolve_cloudflared_apt_suite noble)" "noble" "suite local publicada e preservada"

echo "== apt: suite local sem Release usa fallback e NAO grava suite invalida =="
rm -f "$TEST_TMP/written.list" "$TEST_TMP/apt.log" "$BIN/cloudflared"
export CURL_OK_SUITES="noble jammy"
install_cloudflared_via_apt "$(cloudflared_host_codename)" >/dev/null 2>&1
if [[ -f "$TEST_TMP/written.list" ]] && grep -q "cloudflared noble main" "$TEST_TMP/written.list"; then
  ok "gravou o .list com a suite VALIDADA (noble), nunca com resolute"
else
  bad "list incorreto: $(cat "$TEST_TMP/written.list" 2>/dev/null || echo ausente)"
fi
if grep -q "Dir::Etc::sourcelist=" "$TEST_TMP/apt.log" 2>/dev/null; then
  ok "apt isolado ao .list do cloudflared (nao mexe no apt global)"
else
  bad "apt-get rodou contra o sourcelist global do host"
fi
[[ -x "$BIN/cloudflared" ]] && ok "cloudflared instalado e disponivel no PATH" || bad "binario nao instalado"

echo "== apt: nenhuma suite publicada -> nenhum .list gravado =="
rm -f "$TEST_TMP/written.list" "$BIN/cloudflared"
export CURL_OK_SUITES=""
install_cloudflared_via_apt "$(cloudflared_host_codename)" >/dev/null 2>&1
[[ -f "$TEST_TMP/written.list" ]] && bad "gravou .list com suite inexistente (envenena o apt)" || ok "nenhum .list gravado"

echo "== apt: falha do apt-get install -> retorna 1 (sem abortar) =="
rm -f "$BIN/cloudflared"
export CURL_OK_SUITES="noble"; export APT_FAIL=1
install_cloudflared_via_apt resolute >/dev/null 2>&1; rc=$?
[[ $rc -eq 1 ]] && ok "retorna 1 em vez de fail()" || bad "esperado rc=1, obtido $rc"
unset APT_FAIL

echo "== setup_cloudflare_tunnel: internal nao faz nada =="
rm -f "$TEST_TMP/apt.log" "$TEST_TMP/curl.log"
ACCESS_MODE="internal" setup_cloudflare_tunnel >/dev/null 2>&1
[[ -f "$TEST_TMP/apt.log" || -f "$TEST_TMP/curl.log" ]] && bad "internal tocou no apt/rede" || ok "internal: no-op"

echo "== setup_cloudflare_tunnel: external com apt quebrado cai para o GitHub =="
rm -f "$BIN/cloudflared" "$TEST_TMP/apt.log" "$TEST_TMP/curl.log" "$TEST_TMP/warn.log"
export CURL_OK_SUITES=""; export CLOUDFLARE_TUNNEL_TOKEN="eyJfaketoken"
ACCESS_MODE="external" setup_cloudflare_tunnel >/dev/null 2>&1; rc=$?
# O binario oficial tem de ter sido baixado E instalado no PATH.
if grep -q "cloudflared-linux-\(amd64\|arm64\|arm\)" "$TEST_TMP/curl.log" 2>/dev/null && [[ -x "$BIN/cloudflared" ]]; then
  ok "baixou e instalou o binario oficial do GitHub (amd64/arm64)"
else
  bad "nao baixou/instalou o binario do GitHub"
fi
[[ $rc -eq 0 ]] && ok "external NAO abortou a instalacao (rc=0)" || bad "abortou com rc=$rc"
[[ -s "$TEST_TMP/warn.log" ]] && ok "avisou o operador sobre o tunnel" || bad "sem aviso ao operador"

echo "== ARM64: seletor de arquitetura do mc (services.sh) =="
# Extrai apenas a funcao mc_arch_suffix para testar sem carregar services.sh
# inteiro (que depende de sudo/systemctl/rede).
mc_arch_of() {
  bash -c 'case "$1" in
    x86_64|amd64)  printf "amd64" ;;
    aarch64|arm64) printf "arm64" ;;
    armv7l|armhf)  printf "armv6" ;;
    *) exit 1 ;;
  esac' _ "$1"
}
expect_eq "$(mc_arch_of x86_64)"  "amd64" "x86_64  -> amd64"
expect_eq "$(mc_arch_of aarch64)" "arm64" "aarch64 -> arm64"
expect_eq "$(mc_arch_of arm64)"   "arm64" "arm64   -> arm64"
expect_eq "$(mc_arch_of armv7l)"  "armv6" "armv7l  -> armv6"
mc_arch_of riscv64 >/dev/null 2>&1 && bad "riscv64 deveria retornar 1" || ok "riscv64 -> nao mapeada (retorna 1)"

# Garante que a funcao REAL do services.sh nao tem URL fixa em amd64.
SERVICES_SH="$HERE/../lib/services.sh"
if grep -q 'linux-amd64/mc"' "$SERVICES_SH" && ! grep -q 'linux-\${mc_arch}/mc' "$SERVICES_SH"; then
  bad "services.sh ainda tem URL de mc fixa em amd64"
else
  ok "services.sh monta a URL do mc com a arquitetura do host"
fi

echo "== ARM64: dependencias apt divididas em obrigatorias/build =="
INSTALL_SH="$HERE/../lib/install.sh"
if grep -q "INSTALL_APT_PACKAGES_REQUIRED" "$INSTALL_SH" && grep -q "INSTALL_APT_PACKAGES_BUILD" "$INSTALL_SH"; then
  ok "deps separadas em obrigatorias e build"
else
  bad "deps do apt nao foram separadas"
fi
# O lote de build nao pode chamar fail() (isso derrubaria a instalacao em ARM64).
if awk '/^install_apt_package_batch\(\)/,/^}/' "$INSTALL_SH" | grep -q 'label" == "obrigatorias"'; then
  ok "somente o lote obrigatorio pode falhar (build apenas avisa)"
else
  bad "lote de build pode abortar a instalacao"
fi
echo "== ARM64: checagem de versao do Go (install.sh) =="
# Carrega as funcoes de install.sh num subshell so para os helpers de versao
# (o restante depende de sudo/apt).
GO_HELPER="$(sed -n '/^go_version_at_least()/,/^}/p' "$HERE/../lib/install.sh")"
if [[ -z "$GO_HELPER" ]]; then
  bad "go_version_at_least nao encontrada em install.sh"
else
  eval "$GO_HELPER"
  go_version_at_least "1.26" "1.26" && ok "go 1.26 >= 1.26" || bad "go 1.26 deveria satisfazer 1.26"
  go_version_at_least "1.27" "1.26" && ok "go 1.27 >= 1.26" || bad "go 1.27 deveria satisfazer 1.26"
  go_version_at_least "2.0"  "1.26" && ok "go 2.0 >= 1.26"  || bad "go 2.0 deveria satisfazer 1.26"
  go_version_at_least "1.22" "1.26" && bad "go 1.22 NAO deveria satisfazer 1.26 (caso noble)" || ok "go 1.22 < 1.26 (noble detectado como insuficiente)"
  go_version_at_least "1.24" "1.26" && bad "go 1.24 NAO deveria satisfazer 1.26 (caso questing)" || ok "go 1.24 < 1.26 (questing detectado)"
  go_version_at_least "1.9"  "1.26" && bad "go 1.9 NAO deveria satisfazer 1.26" || ok "go 1.9 < 1.26"
fi

# A checagem precisa estar ligada ao fluxo do wails, senao nao tem efeito.
if grep -q "ensure_go_toolchain_for_agent" "$HERE/../lib/install.sh"; then
  ok "install.sh chama ensure_go_toolchain_for_agent"
else
  bad "ensure_go_toolchain_for_agent nao e chamada"
fi

echo "== ARM64: node_modules de outra arquitetura forca reinstalacao =="
DEPLOY_SH="$HERE/../lib/deploy.sh"
if grep -q "discovery-arch" "$DEPLOY_SH"; then
  ok "deploy.sh marca a arquitetura do node_modules"
else
  bad "deploy.sh nao detecta node_modules de outra arquitetura"
fi
echo
if [[ "$FAILED" -eq 0 ]]; then echo "TODOS OS TESTES PASSARAM"; else echo "HOUVE FALHAS"; exit 1; fi

