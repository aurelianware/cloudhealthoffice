FROM nginxinc/nginx-unprivileged:alpine

WORKDIR /usr/share/nginx/html

COPY . .

# nginx-unprivileged already runs as non-root (UID 101) on port 8080
EXPOSE 8080
