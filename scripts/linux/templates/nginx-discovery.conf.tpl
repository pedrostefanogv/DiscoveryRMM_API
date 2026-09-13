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

  # Uploads (ex.: artefatos de build do agent com RequestSizeLimit de 500MB
  # na API). Default do nginx e 1m — sem isso, todo upload maior falha com 413.
  client_max_body_size 100m;

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

  location / {
    try_files $uri $uri/ /index.html;
  }
}
