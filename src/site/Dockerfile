# Stage 1 — assemble web content, removing build artefacts not meant for the web root
FROM alpine:3.21 AS content
WORKDIR /site
COPY . .
# Remove files that belong in /etc/nginx or are deployment-only artefacts,
# not web-accessible content.
RUN rm -f nginx.conf Dockerfile .dockerignore && rm -rf k8s .k8s

# Stage 2 — serve with nginx-unprivileged (non-root, port 8080)
FROM nginxinc/nginx-unprivileged:alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=content /site /usr/share/nginx/html
# nginx-unprivileged already runs as non-root (UID 101) on port 8080
EXPOSE 8080
