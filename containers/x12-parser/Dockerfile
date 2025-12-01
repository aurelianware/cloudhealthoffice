# X12 Parser Container
# Parses X12 275/277/278 EDI files to JSON format using pyx12 library
# 
# HIPAA Transaction Types Supported:
#   - 005010X210 (275) - Additional Information to Support a Health Care Claim or Encounter
#   - 005010X212 (277) - Health Care Claim Status Notification
#   - 005010X217 (278) - Health Care Services Review Information
#
# Build: docker build -t cloudhealthoffice/x12-parser:latest .
# Run: docker run -v /data:/data cloudhealthoffice/x12-parser /data/input.edi

FROM python:3.11-slim

LABEL org.opencontainers.image.title="Cloud Health Office X12 Parser"
LABEL org.opencontainers.image.description="HIPAA X12 EDI parser for 275/277/278 transactions"
LABEL org.opencontainers.image.vendor="Aurelianware"
LABEL org.opencontainers.image.source="https://github.com/aurelianware/cloudhealthoffice"

# Security: Run as non-root user
RUN groupadd -r x12parser && useradd -r -g x12parser x12parser

# Install system dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    && rm -rf /var/lib/apt/lists/*

# Set working directory
WORKDIR /app

# Copy requirements and install Python dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy application code
COPY parse_x12.py .
COPY x12_schemas/ x12_schemas/

# Switch to non-root user
USER x12parser

# Set environment variables
ENV PYTHONUNBUFFERED=1
ENV LOG_LEVEL=INFO

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD python -c "import parse_x12; print('healthy')" || exit 1

# Default entrypoint
ENTRYPOINT ["python", "parse_x12.py"]
