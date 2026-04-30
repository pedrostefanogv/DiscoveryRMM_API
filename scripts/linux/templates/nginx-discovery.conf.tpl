map $http_upgrade $connection_upgrade {
  default upgrade;
  '' close;
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
  listen 8443 ssl http2;
  listen [::]:8443 ssl http2;
  server_name __SERVER_NAME_LIST__;

__REDIRECT_RULES__

  ssl_certificate /etc/discovery-api/certs/api-internal.crt;
  ssl_certificate_key /etc/discovery-api/certs/api-internal.key;

  root __DISCOVERY_SITE_CURRENT__;
  index index.html;

  location /api/ {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
  }

  location /hubs/ {
    proxy_pass http://127.0.0.1:8080;
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
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    # Impede service worker de cachear ou interceptar este endpoint.
    add_header Cache-Control "no-store, no-cache, must-revalidate" always;
    add_header Pragma "no-cache" always;
  }

  location ^~ /openapi {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
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
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
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
