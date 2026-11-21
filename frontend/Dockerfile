# syntax=docker/dockerfile:1.7
# ---------------------------------------------------------------- build
FROM node:22-alpine AS build
WORKDIR /src
COPY package.json package-lock.json ./
# Optional corporate TLS-inspection CA (never stored in a layer):  docker build --secret id=extra_ca,src=corp-ca.crt ...
RUN --mount=type=cache,target=/root/.npm --mount=type=secret,id=extra_ca,required=false \
    if [ -f /run/secrets/extra_ca ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/extra_ca; fi; \
    npm ci --no-audit --no-fund
COPY . .
RUN npx ng build --configuration production

# ---------------------------------------------------------------- runtime (unprivileged nginx, port 8080)
FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY nginx/40-render-config.sh /docker-entrypoint.d/40-render-config.sh
COPY --from=build /src/dist/frontend/browser /usr/share/nginx/html
USER root
RUN chmod +x /docker-entrypoint.d/40-render-config.sh && chown -R nginx /usr/share/nginx/html
USER nginx
ENV API_UPSTREAM=http://api:8080 \
    ARCGIS_API_KEY="" \
    ARCGIS_FONTS_URL="" \
    MAP_DEFAULT_CENTER="78.9,21.5" \
    MAP_DEFAULT_ZOOM=5 \
    APP_LOCALE=en-IN
EXPOSE 8080
