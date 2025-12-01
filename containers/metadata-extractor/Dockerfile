# Metadata Extractor Container
# Extracts business metadata from parsed X12 JSON for downstream processing
#
# Extracts:
#   - Claim number
#   - Member ID
#   - Provider NPI
#   - Service dates
#   - Diagnosis codes
#   - Attachment metadata
#
# Build: docker build -t cloudhealthoffice/metadata-extractor:latest .
# Run: docker run -v /data:/data cloudhealthoffice/metadata-extractor --input /data/parsed.json

FROM python:3.11-slim

LABEL org.opencontainers.image.title="Cloud Health Office Metadata Extractor"
LABEL org.opencontainers.image.description="Extracts business metadata from parsed X12 EDI JSON"
LABEL org.opencontainers.image.vendor="Aurelianware"
LABEL org.opencontainers.image.source="https://github.com/aurelianware/cloudhealthoffice"

# Security: Run as non-root user
RUN groupadd -r extractor && useradd -r -g extractor extractor

# Set working directory
WORKDIR /app

# Copy requirements and install Python dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy application code
COPY extract_metadata.py .

# Switch to non-root user
USER extractor

# Set environment variables
ENV PYTHONUNBUFFERED=1
ENV LOG_LEVEL=INFO

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD python -c "import extract_metadata; print('healthy')" || exit 1

# Default entrypoint
ENTRYPOINT ["python", "extract_metadata.py"]
