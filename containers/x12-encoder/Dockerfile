# X12 Encoder Container
# Generates X12 277 (Claim Status Notification / RFAI) EDI files
#
# HIPAA Transaction Types Supported:
#   - 005010X212 (277) - Health Care Claim Status Notification
#
# Build: docker build -t cloudhealthoffice/x12-encoder:latest .
# Run: docker run -v /data:/data cloudhealthoffice/x12-encoder --input /data/claim.json --output /data/277.edi

FROM python:3.11-slim

LABEL org.opencontainers.image.title="Cloud Health Office X12 Encoder"
LABEL org.opencontainers.image.description="HIPAA X12 EDI encoder for 277 RFAI responses"
LABEL org.opencontainers.image.vendor="Aurelianware"
LABEL org.opencontainers.image.source="https://github.com/aurelianware/cloudhealthoffice"

# Security: Run as non-root user
RUN groupadd -r x12encoder && useradd -r -g x12encoder x12encoder

# Set working directory
WORKDIR /app

# Copy requirements and install Python dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy application code
COPY generate_277.py .

# Switch to non-root user
USER x12encoder

# Set environment variables
ENV PYTHONUNBUFFERED=1
ENV LOG_LEVEL=INFO

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD python -c "import generate_277; print('healthy')" || exit 1

# Default entrypoint
ENTRYPOINT ["python", "generate_277.py"]
