# Two stages: build the static bundle with Node, then serve it with Caddy.
# The result is one small container that also terminates TLS, so a Debian VPS
# needs nothing installed beyond Docker.

# ---- build ----
FROM node:24-alpine AS build
WORKDIR /app

# Copy the manifests alone first so the dependency layer caches until they
# actually change.
COPY package.json package-lock.json* ./
RUN npm ci

COPY . .
RUN npm run build

# ---- serve ----
FROM caddy:2-alpine AS runtime

COPY Caddyfile /etc/caddy/Caddyfile
COPY --from=build /app/dist /srv

EXPOSE 80 443
