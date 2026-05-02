# Discovery RMM installer – operation modes (update-stack, update-nats)
# Requires: common.sh, install.sh, services.sh, deploy.sh, prompts.sh, normalize.sh, certs.sh

apply_stack_update_only() {
  set_log_context "update"
  log "Modo de update da stack: atualizando repositorios e rebuildando API/portal"

  DISCOVERY_GIT_REPO="${DISCOVERY_GIT_REPO:-https://github.com/pedrostefanogv/DiscoveryRMM_API}"
  DISCOVERY_AGENT_GIT_REPO="${DISCOVERY_AGENT_GIT_REPO:-https://github.com/pedrostefanogv/DiscoveryRMM_Agent}"
  DISCOVERY_SITE_GIT_REPO="${DISCOVERY_SITE_GIT_REPO:-https://github.com/pedrostefanogv/DiscoveryRMM_Site}"
  DISCOVERY_GIT_BRANCH="${DISCOVERY_GIT_BRANCH:-${DISCOVERY_RELEASE_CHANNEL:-release}}"
  log "Canal/branch selecionado para update: ${DISCOVERY_GIT_BRANCH}"

  DISCOVERY_API_BASE="${DISCOVERY_API_BASE:-/opt/discovery-api}"
  DISCOVERY_SITE_BASE="${DISCOVERY_SITE_BASE:-/opt/discovery-site}"
  DISCOVERY_AGENT_SRC="${DISCOVERY_AGENT_SRC:-/opt/discovery-agent-src}"
  DISCOVERY_AGENT_ARTIFACTS="${DISCOVERY_AGENT_ARTIFACTS:-/opt/discovery-agent-artifacts}"
  DISCOVERY_OPS_DIR="${DISCOVERY_OPS_DIR:-/opt/discovery-ops}"

  local detected_arch; local detected_dotnet_runtime
  detected_arch="$(detect_system_architecture)"
  if detected_dotnet_runtime="$(map_arch_to_dotnet_runtime "$detected_arch")"; then :; else
    detected_dotnet_runtime="linux-x64"
    warn "Arquitetura nao mapeada (${detected_arch:-desconhecida}); usando runtime padrao linux-x64"
  fi

  if [[ -z "${DISCOVERY_DOTNET_RUNTIME:-}" ]]; then DISCOVERY_DOTNET_RUNTIME="$detected_dotnet_runtime"; fi
  validate_dotnet_runtime "$DISCOVERY_DOTNET_RUNTIME"

  DISCOVERY_SITE_API_URL="${DISCOVERY_SITE_API_URL:-}"
  if [[ -z "${DISCOVERY_CLEAN_BUILD:-}" ]] && sudo test -f /etc/discovery-api/discovery.env; then
    DISCOVERY_CLEAN_BUILD="$(sudo awk -F= '/^DISCOVERY_CLEAN_BUILD=/{sub("^[^=]*=",""); print; exit}' /etc/discovery-api/discovery.env 2>/dev/null || true)"
  fi
  case "${DISCOVERY_CLEAN_BUILD:-1}" in 0|1) ;; *) DISCOVERY_CLEAN_BUILD="1" ;; esac
  load_existing_site_realtime_defaults
  normalize_site_realtime_settings
  update_site_realtime_environment_file

  DISCOVERY_API_RELEASES="${DISCOVERY_API_BASE}/releases"
  DISCOVERY_API_SHARED="${DISCOVERY_API_BASE}/shared"
  DISCOVERY_API_SOURCE="${DISCOVERY_API_BASE}/source"
  DISCOVERY_API_CURRENT="${DISCOVERY_API_BASE}/current"
  DISCOVERY_SITE_RELEASES="${DISCOVERY_SITE_BASE}/releases"
  DISCOVERY_SITE_SOURCE="${DISCOVERY_SITE_BASE}/source"
  DISCOVERY_SITE_CURRENT="${DISCOVERY_SITE_BASE}/current"

  ensure_dotnet_sdk
  ensure_nodejs
  ensure_service_user
  create_directories

  trap cleanup_on_exit EXIT
  setup_git_askpass
  clone_or_update_repo "$DISCOVERY_GIT_REPO" "$DISCOVERY_API_SOURCE"
  clone_or_update_repo "$DISCOVERY_AGENT_GIT_REPO" "$DISCOVERY_AGENT_SRC"
  clone_or_update_repo "$DISCOVERY_SITE_GIT_REPO" "$DISCOVERY_SITE_SOURCE"

  publish_api
  publish_site
  install_selfupdate_script
  write_site_proxy_config

  if sudo systemctl list-unit-files discovery-api.service >/dev/null 2>&1; then
    sudo systemctl restart discovery-api || warn "Falha ao reiniciar discovery-api"
  else warn "Servico discovery-api nao encontrado; pulando restart"; fi
  if sudo systemctl list-unit-files nginx.service >/dev/null 2>&1; then
    sudo systemctl restart nginx || warn "Falha ao reiniciar nginx"
  fi

  log "Update da stack concluido"
}

apply_nats_reconfiguration_only() {
  set_log_context "update-nats"
  log "Modo de atualizacao NATS: reconfigurando NATS e variaveis da API"

  prompt_nats_configuration
  validate_security_inputs
  normalize_nats_settings
  normalize_site_realtime_settings
  load_existing_nats_defaults
  generate_nats_account_keys

  setup_nats
  update_nats_environment_file
  update_site_realtime_environment_file

  if sudo systemctl is-active --quiet discovery-api; then
    log "Reiniciando discovery-api para aplicar novas configuracoes NATS"
    sudo systemctl restart discovery-api
  fi

  log "Atualizacao de configuracao NATS concluida"
}
