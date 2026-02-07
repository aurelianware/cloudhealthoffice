FROM node:18-alpine

WORKDIR /app

# Copy package files
COPY package*.json ./

# Install dependencies
RUN npm ci --only=production

# Copy parser script
COPY parse-834.js ./

# Make script executable
RUN chmod +x parse-834.js

# Create input/output directories
RUN mkdir -p /input /output

# Set environment variables
ENV INPUT_DIR=/input
ENV OUTPUT_DIR=/output

CMD ["node", "parse-834.js"]
