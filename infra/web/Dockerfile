# syntax=docker/dockerfile:1
# VYBE web image: the Angular production build behind Caddy, which also proxies /api to the API (ADR 0008).
# Build from the repository root (the build needs the workspace packages and the asset manifests):
#   docker build -f infra/web/Dockerfile -t vybe-web .
# Run: see infra/oracle/compose.yml (production VM) and infra/docker-compose.yml (service "web", local).

# Base images pinned by digest. To update: docker buildx imagetools inspect <image:tag>, copy the manifest-list digest.
FROM node:22.23.4-alpine3.24@sha256:0e1754758c2df4cfa3e68d59e2e1b7374bb7fd9ea6328eaaacf15cc934af8202 AS build
WORKDIR /src
# The pnpm version of package.json "packageManager".
RUN npm install --global --no-fund --no-audit pnpm@12.11.2
# Install first, so a source change does not invalidate the dependency layer.
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY apps/web/package.json apps/web/
COPY packages/contracts/package.json packages/contracts/
COPY packages/game-rules/package.json packages/game-rules/
COPY tests/e2e/package.json tests/e2e/
RUN pnpm install --frozen-lockfile --filter @vybe/web...
COPY packages packages
COPY assets/manifests assets/manifests
# The avatar and catalog art (assets/source/build.sh); apps/web/art links to it and the build serves it at /assets.
COPY assets/published assets/published
COPY apps/web apps/web
RUN pnpm --filter @vybe/web build

FROM caddy:2.11.9-alpine@sha256:0bbb055bda6793c999bde78b1b5ce3e89f829fb031af2cf50056ea1246559fee AS runtime
# Non-root. Caddy listens on 8080 (no privileged port) and writes only to /tmp (runtime config, Caddy state).
RUN addgroup -S -g 10001 web && adduser -S -D -H -u 10001 -G web web
COPY infra/web/Caddyfile /etc/caddy/Caddyfile
COPY --chmod=0555 infra/web/entrypoint.sh /usr/local/bin/vybe-web
COPY --from=build /src/apps/web/dist/web/browser /srv
ENV XDG_CONFIG_HOME=/tmp/caddy/config \
    XDG_DATA_HOME=/tmp/caddy/data \
    VYBE_API_PORT=8080
USER 10001:10001
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/vybe-web"]
