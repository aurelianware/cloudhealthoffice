# Kafka Publisher Container
# Publishes events to Apache Kafka topics for X12 EDI processing pipeline
#
# Features:
#   - SASL/SSL authentication
#   - Configurable topic routing
#   - Message serialization (JSON)
#   - Delivery confirmation and error handling
#
# Build: docker build -t cloudhealthoffice/kafka-publisher:latest .
# Run: docker run cloudhealthoffice/kafka-publisher --bootstrap-servers kafka:9092 --topic attachments-in

FROM python:3.11-slim

LABEL org.opencontainers.image.title="Cloud Health Office Kafka Publisher"
LABEL org.opencontainers.image.description="Publishes X12 EDI events to Apache Kafka"
LABEL org.opencontainers.image.vendor="Aurelianware"
LABEL org.opencontainers.image.source="https://github.com/aurelianware/cloudhealthoffice"

# Security: Run as non-root user
RUN groupadd -r kafkapub && useradd -r -g kafkapub kafkapub

# Set working directory
WORKDIR /app

# Copy requirements and install Python dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy application code
COPY publish.py .

# Switch to non-root user
USER kafkapub

# Set environment variables
ENV PYTHONUNBUFFERED=1
ENV LOG_LEVEL=INFO
ENV KAFKA_BOOTSTRAP_SERVERS=""
ENV KAFKA_SECURITY_PROTOCOL=PLAINTEXT
ENV KAFKA_SASL_MECHANISM=""
ENV KAFKA_SASL_USERNAME=""
ENV KAFKA_SASL_PASSWORD=""

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD python -c "import publish; print('healthy')" || exit 1

# Default entrypoint
ENTRYPOINT ["python", "publish.py"]
