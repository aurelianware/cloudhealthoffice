# SFTP Fetcher Container
# Downloads files from Clearinghouse SFTP server for X12 EDI processing
#
# Features:
#   - SSH key-based authentication
#   - Connection pooling and retry logic
#   - Batch file download support
#   - Kubernetes Secret integration
#
# Build: docker build -t cloudhealthoffice/sftp-fetcher:latest .
# Run: docker run -v /data:/data cloudhealthoffice/sftp-fetcher --host sftp.clearinghouse.example.com --folder /inbound

FROM python:3.11-slim

LABEL org.opencontainers.image.title="Cloud Health Office SFTP Fetcher"
LABEL org.opencontainers.image.description="SFTP client for downloading X12 EDI files from clearinghouse"
LABEL org.opencontainers.image.vendor="Aurelianware"
LABEL org.opencontainers.image.source="https://github.com/aurelianware/cloudhealthoffice"

# Security: Run as non-root user
RUN groupadd -r sftpfetcher && useradd -r -g sftpfetcher sftpfetcher

# Install system dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    openssh-client \
    && rm -rf /var/lib/apt/lists/*

# Set working directory
WORKDIR /app

# Copy requirements and install Python dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy application code
COPY fetch_sftp.py .

# Create directories for credentials and data
RUN mkdir -p /data/input /data/output /secrets && \
    chown -R sftpfetcher:sftpfetcher /data /secrets

# Switch to non-root user
USER sftpfetcher

# Set environment variables
ENV PYTHONUNBUFFERED=1
ENV LOG_LEVEL=INFO
ENV SFTP_HOST=""
ENV SFTP_PORT=22
ENV SFTP_USERNAME=""
ENV SSH_KEY_PATH=/secrets/ssh-key

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD python -c "import fetch_sftp; print('healthy')" || exit 1

# Default entrypoint
ENTRYPOINT ["python", "fetch_sftp.py"]
