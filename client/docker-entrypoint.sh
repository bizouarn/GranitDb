#!/bin/sh
set -e
printf '{"ApiBaseUrl":"%s"}' "${API_BASE_URL}" > /usr/share/nginx/html/appsettings.json

# En-têtes de sécurité, dont la Content-Security-Policy : l'adresse de l'API (connect-src) est celle de API_BASE_URL,
# réduite à son origine ; une valeur qui n'a pas la forme http(s)://hôte[:port] n'est pas copiée dans l'en-tête.
api_origin=$(printf '%s' "${API_BASE_URL}" | sed -nE 's#^(https?://[A-Za-z0-9._:-]+)(/.*)?$#\1#p')
csp="default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' data: https://fonts.gstatic.com; img-src 'self' data: blob: https:; connect-src 'self' ${api_origin}; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'"
cat > /etc/nginx/security-headers.conf <<HEADERS
add_header Content-Security-Policy "${csp}" always;
add_header X-Content-Type-Options "nosniff" always;
add_header X-Frame-Options "DENY" always;
add_header Referrer-Policy "no-referrer" always;
add_header Permissions-Policy "camera=(), microphone=(), geolocation=()" always;
HEADERS
