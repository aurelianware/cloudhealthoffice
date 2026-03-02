FROM alpine:3.21

WORKDIR /app

# Install dependencies
RUN apk add --no-cache \
    bash \
    curl \
    jq \
    kafkacat

# Copy publisher script
COPY publish-claims.sh /app/
RUN chmod +x /app/publish-claims.sh

# Create non-root user
RUN addgroup -S appgroup && adduser -S appuser -G appgroup \
    && chown -R appuser:appgroup /app
USER appuser

# Set entrypoint
ENTRYPOINT ["/app/publish-claims.sh"]
