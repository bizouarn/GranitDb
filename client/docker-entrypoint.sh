#!/bin/sh
set -e
printf '{"ApiBaseUrl":"%s"}' "${API_BASE_URL}" > /usr/share/nginx/html/appsettings.json
