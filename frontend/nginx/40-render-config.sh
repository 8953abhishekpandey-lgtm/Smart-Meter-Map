#!/bin/sh
# Renders the SPA runtime configuration from environment variables at container start.
set -eu
cat > /usr/share/nginx/html/config.json <<EOF
{
  "apiBaseUrl": "${API_BASE_URL:-}",
  "arcgisApiKey": "${ARCGIS_API_KEY:-}",
  "arcgisAssetsPath": "/assets/arcgis",
  "arcgisFontsUrl": "${ARCGIS_FONTS_URL:-}",
  "defaultCenter": [${MAP_DEFAULT_CENTER:-78.9,21.5}],
  "defaultZoom": ${MAP_DEFAULT_ZOOM:-5},
  "locale": "${APP_LOCALE:-en-IN}"
}
EOF
