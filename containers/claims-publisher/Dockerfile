FROM alpine:3.18

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

# Set entrypoint
ENTRYPOINT ["/app/publish-claims.sh"]
