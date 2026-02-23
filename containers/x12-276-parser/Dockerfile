# X12 276 Parser Container
# Parses X12 276 (Claim Status Inquiry) EDI files to JSON
#
# HIPAA Transaction: 005010X212 - Health Care Claim Status Request
#
# Build: docker build -t ghcr.io/aurelianware/cloudhealthoffice-x12-276-parser:latest .
# Run: docker run -v /data:/data ghcr.io/aurelianware/cloudhealthoffice-x12-276-parser /data/input.edi

FROM python:3.11-slim

LABEL org.opencontainers.image.title="Cloud Health Office X12 276 Parser"
LABEL org.opencontainers.image.description="HIPAA X12 276 Claim Status Inquiry parser"
LABEL org.opencontainers.image.vendor="Aurelianware"
LABEL org.opencontainers.image.source="https://github.com/aurelianware/cloudhealthoffice"

# Security: Run as non-root user
RUN groupadd -r x12parser && useradd -r -g x12parser x12parser

# Set working directory
WORKDIR /app

# Copy application code
COPY parse-276.py .

# Create data directories
RUN mkdir -p /data/input /data/output && \
    chown -R x12parser:x12parser /data

# Switch to non-root user
USER x12parser

# Set environment variables
ENV PYTHONUNBUFFERED=1
ENV LOG_LEVEL=INFO

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD python -c "import sys; sys.exit(0)" || exit 1

# Default entrypoint
ENTRYPOINT ["python", "parse-276.py"]
CMD ["--help"]
