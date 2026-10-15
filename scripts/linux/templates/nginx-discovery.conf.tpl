map $http_upgrade $connection_upgrade {
  default upgrade;
  '' close;
}

# Upstream compartilhado com keepalive: reusa conexoes TCP para a Kestrel
# (menor overhead por requisicao). Localizacoes WebSocket usam
# `Connection $connection_upgrade`; as demais usam `Connection ""` para nao
# matar o keepalive do upstream.
upstream discovery_api_backend {
  server 127.0.0.1:8080;
  keepalive 32;
}

# Storage de objetos (RustFS local): o caminho do bucket e publicado no MESMO
# host do portal, entao as URLs pre-assinadas funcionam de fora sem DNS nem
# certificado novos. O header Host e preservado porque a assinatura SigV4
# cobre o host — reescrever para o upstream invalidaria a assinatura.
upstream discovery_object_storage_backend {
  server 127.0.0.1:9000;
  keepalive 16;
}

# Catch-all: responde 444 a qualquer Host fora da allowlist (scan/scan bots
# apontando DNS arbitrario para o IP do servidor). Reaproveita o certificado
# interno para manter compatibilidade com nginx 1.18 (Ubuntu 20.04), onde
# `ssl_reject_handshake` nao existe.
server {
  listen 443 ssl default_server;
  listen [::]:443 ssl default_server;
  server_name _;
  ssl_certificate /etc/discovery-api/certs/api-internal.crt;
  ssl_certificate_key /etc/discovery-api/certs/api-internal.key;
  return 444;
}

server {
  listen 80;
  listen [::]:80;
  server_name __SERVER_NAME_LIST__;

  # Endpoints http:// do storage (instalacao internal sem TLS): o caminho do
  # bucket e proxiado aqui, ANTES do redirect — PUT de URL pre-assinada nao
  # pode passar por 301.
__OBJECT_STORAGE_LOCATION_PORT80__
  return 301 https://$host$request_uri;
}

server {
  listen 443 ssl http2;
  listen [::]:443 ssl http2;
  server_name __SERVER_NAME_LIST__;

  server_tokens off;

__REDIRECT_RULES__

  ssl_certificate /etc/discovery-api/certs/api-internal.crt;
  ssl_certificate_key /etc/discovery-api/certs/api-internal.key;

  # TLS moderno apenas (defaults antigos do nginx incluem TLS 1.0/1.1).
  ssl_protocols TLSv1.2 TLSv1.3;
  ssl_session_cache shared:SSL:10m;
  ssl_session_timeout 1d;
  ssl_session_tickets off;

  # Uploads. Default do nginx e 1m — sem isso todo upload maior falha com 413.
  # PADRAO 2026-10-15: uploads grandes vao em BLOCOS de 10 MiB (padronizado no
  # console e na API), porque o edge do Cloudflare recusa corpos de centenas de
  # MB/GB com 413 e corta requisicoes longas (~100s): bloco menor atravessa link
  # lento. 64m cobre o bloco + overhead de protocolo.
client_max_body_size 64m;

  gzip on;
  gzip_types text/css application/javascript application/json image/svg+xml;
  gzip_min_length 1024;

  root __DISCOVERY_SITE_CURRENT__;
  index index.html;

  location /api/ {
    proxy_pass http://discovery_api_backend;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
  }

  # Upload multipart DIRETO no object storage (loja custom): o "/complete" só
  # termina depois de o storage montar o objeto E a API calcular o SHA-256 do
  # arquivo (passada de leitura). O default de 60s do proxy_read_timeout devolvia
  # 504 no meio de um instalador de vários GiB — com o storage já concluído.
  #
  # ATENCAO: o location NAO pode terminar com "/". O nginx (auto_redirect, ligado
  # por padrao) responde 301 de /multipart para /multipart/ quando o location tem
  # barra final; o navegador refaz o POST como GET e a API devolve 405. Sem a
  # barra o location casa /multipart e /multipart/{id} e nao ha redirect.
  location ^~ /api/v1/app-store/custom/assets/multipart {
    proxy_pass http://discovery_api_backend;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 900s;
    proxy_send_timeout 900s;
  }

  # AI Chat stream: SSE long-polling exige timeout alto e sem buffering.
  # O LLM pode demorar >60s para corrigir tool calls + regenerar resposta.
  location /api/v1/agent-auth/me/ai-chat/stream {
    proxy_pass http://discovery_api_backend;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 180s;
    proxy_buffering off;
  }

  location /hubs/ {
    proxy_pass http://discovery_api_backend;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
  }

  location = /nats {
    return 308 /nats/;
  }

  location /nats/ {
    proxy_pass http://127.0.0.1:__NATS_WS_PORT__/;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_read_timeout 1h;
    proxy_send_timeout 1h;
    proxy_buffering off;
  }

  location ^~ /health {
    proxy_pass http://discovery_api_backend;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    # Impede service worker de cachear ou interceptar este endpoint.
    add_header Cache-Control "no-store, no-cache, must-revalidate" always;
    add_header Pragma "no-cache" always;
  }

  location ^~ /openapi {
    proxy_pass http://discovery_api_backend;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    # Segue redirects internamente: evita que o browser receba um 301 que o
    # service worker da SPA possa interceptar antes de chegar ao nginx novamente.
    proxy_redirect ~^http://127\.0\.0\.1:8080(/openapi.*) $1;
    # Em caso de erro da API (OpenAPI desabilitado ou servico indisponivel),
    # exibe pagina estatica em vez de deixar cair no SPA.
    proxy_intercept_errors on;
    error_page 404 502 503 504 = @docs_unavailable;
  }

  location ^~ /scalar {
    proxy_pass http://discovery_api_backend;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_redirect ~^http://127\.0\.0\.1:8080(/scalar.*) $1;
    proxy_intercept_errors on;
    error_page 404 502 503 504 = @docs_unavailable;
  }

  # Fallback exibido quando OpenAPI/Scalar esta desabilitado ou a API esta fora.
  # Retorna pagina HTML simples — nunca redireciona para o SPA.
  location @docs_unavailable {
    default_type text/html;
    return 503 "<!DOCTYPE html><html lang='pt-BR'><head><meta charset='UTF-8'><title>Discovery API Docs</title><style>body{font-family:sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;background:#f5f5f5}.box{background:#fff;padding:2rem 3rem;border-radius:8px;box-shadow:0 2px 8px rgba(0,0,0,.1);text-align:center}h1{color:#333;font-size:1.4rem}p{color:#666}a{color:#4a90e2}</style></head><body><div class='box'><h1>Documentacao da API indisponivel</h1><p>A documentacao OpenAPI/Scalar esta desabilitada ou a API esta temporariamente fora do ar.</p><p>Tente acessar diretamente: <a href='/openapi/v1.json'>/openapi/v1.json</a></p></div></body></html>";
  }

  # Caminho do bucket do storage (vazio quando o storage nao e local). Fica
  # antes do fallback da SPA para nao cair no index.html.
__OBJECT_STORAGE_LOCATION__
  location / {
    try_files $uri $uri/ /index.html;
  }
}
