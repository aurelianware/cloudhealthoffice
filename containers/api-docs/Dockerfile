FROM nginx:1.27-alpine

# Add YAML MIME type (not included in nginx defaults)
RUN sed -i 's|text/plain\s*txt;|text/plain  txt;\n    text/yaml   yaml yml;|' /etc/nginx/mime.types

# Custom nginx configuration
COPY containers/api-docs/nginx.conf /etc/nginx/conf.d/default.conf

# Swagger UI HTML
COPY src/api-docs/index.html /usr/share/nginx/html/index.html

# OpenAPI YAML specs
COPY api/openapi/ /usr/share/nginx/html/specs/

EXPOSE 80
