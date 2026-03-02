FROM node:20-alpine

WORKDIR /app

# Copy package files
COPY package*.json ./

# Install dependencies
RUN npm install --omit=dev

# Copy parser script
COPY parse-834.js ./

# Make script executable
RUN chmod +x parse-834.js

# Create input/output directories and set permissions
RUN mkdir -p /input /output \
    && chown -R node:node /app /input /output

# Set environment variables
ENV INPUT_DIR=/input
ENV OUTPUT_DIR=/output

# Run as non-root user
USER node

CMD ["node", "parse-834.js"]
