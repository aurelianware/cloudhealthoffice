FROM nginx:alpine

WORKDIR /usr/share/nginx/html

COPY . .

EXPOSE 8080

# Run nginx as non-root using the built-in nginx user
# Update nginx config to listen on non-privileged port 8080
RUN sed -i 's/listen\s*80;/listen 8080;/g' /etc/nginx/conf.d/default.conf \
    && chown -R nginx:nginx /usr/share/nginx/html \
    && chown -R nginx:nginx /var/cache/nginx \
    && chown -R nginx:nginx /var/log/nginx \
    && touch /var/run/nginx.pid \
    && chown nginx:nginx /var/run/nginx.pid
USER nginx
