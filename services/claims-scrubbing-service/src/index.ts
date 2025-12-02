/**
 * Cloud Health Office - Claims Scrubbing Service
 * HTTP Server Entry Point
 * 
 * Provides REST API endpoints for claims validation and management.
 * Supports both standalone and Dapr sidecar deployment modes.
 */

import * as http from 'http';
import { URL } from 'url';

import {
  ClaimsScrubberConfig,
  ValidateClaimRequest,
  ValidateClaimResponse,
  BatchValidateRequest,
  X12_837_Claim,
} from './types';
import { ClaimsScrubberService } from './claims-scrubber';

// Default configuration (can be overridden by environment variables)
const config: ClaimsScrubberConfig = {
  kafka: {
    bootstrapServers: process.env.KAFKA_BOOTSTRAP_SERVERS || 'localhost:9092',
    clientId: process.env.KAFKA_CLIENT_ID || 'claims-scrubber',
    inboundTopic: process.env.INBOUND_CLAIMS_TOPIC || 'claims-inbound',
    cleanClaimsTopic: process.env.CLEAN_CLAIMS_TOPIC || 'claims-adjudication',
    flaggedClaimsTopic: process.env.FLAGGED_CLAIMS_TOPIC || 'claims-work-queue',
    rejectedClaimsTopic: process.env.REJECTED_CLAIMS_TOPIC || 'claims-rejected',
    consumerGroupId: process.env.KAFKA_CONSUMER_GROUP || 'claims-scrubber-group',
    sasl: process.env.KAFKA_SASL_USERNAME ? {
      mechanism: (process.env.KAFKA_SASL_MECHANISM || 'scram-sha-512') as 'plain' | 'scram-sha-256' | 'scram-sha-512',
      username: process.env.KAFKA_SASL_USERNAME,
      password: process.env.KAFKA_SASL_PASSWORD || '',
    } : undefined,
    ssl: process.env.KAFKA_SSL === 'true',
  },
  storage: {
    accountName: process.env.STORAGE_ACCOUNT_NAME,
    connectionString: process.env.STORAGE_CONNECTION_STRING,
    containerName: process.env.CLAIMS_CONTAINER || 'claims-archive',
    archivePathPattern: '{claimType}/{status}/{yyyy}/{MM}/{dd}',
  },
  cosmosDb: {
    endpoint: process.env.COSMOS_ENDPOINT || '',
    databaseName: process.env.COSMOS_DATABASE || 'claims-scrubbing',
    rulesContainerName: process.env.COSMOS_RULES_CONTAINER || 'validation-rules',
    auditContainerName: process.env.COSMOS_AUDIT_CONTAINER || 'validation-audit',
  },
  ruleEngine: {
    parallelExecution: process.env.PARALLEL_RULES === 'true',
    maxConcurrency: parseInt(process.env.MAX_RULE_CONCURRENCY || '10', 10),
    ruleTimeoutMs: parseInt(process.env.RULE_TIMEOUT_MS || '5000', 10),
    continueOnError: process.env.CONTINUE_ON_RULE_ERROR === 'true',
    cacheRules: process.env.CACHE_RULES !== 'false',
    ruleCacheTtlSeconds: parseInt(process.env.RULE_CACHE_TTL || '300', 10),
  },
  thresholds: {
    maxErrorsForRejection: parseInt(process.env.MAX_ERRORS_FOR_REJECTION || '5', 10),
    maxWarningsForFlagging: parseInt(process.env.MAX_WARNINGS_FOR_FLAGGING || '3', 10),
    firstPassRateTarget: parseFloat(process.env.FIRST_PASS_RATE_TARGET || '95'),
  },
  features: {
    duplicateDetection: process.env.ENABLE_DUPLICATE_DETECTION === 'true',
    medicalNecessityChecks: process.env.ENABLE_MEDICAL_NECESSITY === 'true',
    ncciEdits: process.env.ENABLE_NCCI_EDITS === 'true',
    autoCorrection: process.env.ENABLE_AUTO_CORRECTION === 'true',
    realtimeNpiValidation: process.env.ENABLE_REALTIME_NPI === 'true',
  },
  dapr: {
    enabled: process.env.DAPR_ENABLED === 'true',
    httpPort: parseInt(process.env.DAPR_HTTP_PORT || '3500', 10),
    grpcPort: parseInt(process.env.DAPR_GRPC_PORT || '50001', 10),
    appId: process.env.DAPR_APP_ID || 'claims-scrubber',
    pubSubName: process.env.DAPR_PUBSUB_NAME || 'pubsub',
    stateStoreName: process.env.DAPR_STATE_STORE || 'statestore',
  },
};

// Create service instance
const service = new ClaimsScrubberService(config);

// HTTP request body parser
function parseBody(req: http.IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    req.on('data', (chunk: Buffer) => chunks.push(chunk));
    req.on('end', () => resolve(Buffer.concat(chunks).toString()));
    req.on('error', reject);
  });
}

// Send JSON response
function sendJson(res: http.ServerResponse, statusCode: number, data: unknown): void {
  res.writeHead(statusCode, {
    'Content-Type': 'application/json',
    'X-Content-Type-Options': 'nosniff',
  });
  res.end(JSON.stringify(data));
}

// CORS handling
const allowedOrigins = (process.env.CORS_ALLOWED_ORIGINS || '*').split(',').map(o => o.trim());

function handleCors(req: http.IncomingMessage, res: http.ServerResponse): boolean {
  const origin = req.headers.origin || '';
  
  if (allowedOrigins.includes('*') || allowedOrigins.includes(origin)) {
    res.setHeader('Access-Control-Allow-Origin', origin || '*');
    res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-Correlation-Id');
    res.setHeader('Access-Control-Max-Age', '86400');
  }

  // Handle preflight
  if (req.method === 'OPTIONS') {
    res.writeHead(204);
    res.end();
    return true;
  }

  return false;
}

// Request handler
async function handleRequest(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
  // Handle CORS
  if (handleCors(req, res)) return;

  const url = new URL(req.url || '/', `http://${req.headers.host}`);
  const pathname = url.pathname;
  const method = req.method || 'GET';

  try {
    // Health endpoints
    if (pathname === '/health' || pathname === '/api/health') {
      const health = await service.getHealth();
      sendJson(res, health.status === 'healthy' ? 200 : 503, health);
      return;
    }

    if (pathname === '/healthz' || pathname === '/livez') {
      sendJson(res, 200, { status: 'alive' });
      return;
    }

    if (pathname === '/readyz') {
      const health = await service.getHealth();
      if (health.status === 'healthy') {
        sendJson(res, 200, { status: 'ready' });
      } else {
        sendJson(res, 503, { status: 'not ready', reason: health.status });
      }
      return;
    }

    // Validate single claim
    if (pathname === '/api/claims/validate' && method === 'POST') {
      const body = await parseBody(req);
      const request: ValidateClaimRequest = JSON.parse(body);
      request.correlationId = request.correlationId || req.headers['x-correlation-id'] as string;
      
      const response = await service.validateClaim(request);
      sendJson(res, 200, response);
      return;
    }

    // Validate batch of claims
    if (pathname === '/api/claims/validate/batch' && method === 'POST') {
      const body = await parseBody(req);
      const request: BatchValidateRequest = JSON.parse(body);
      request.correlationId = request.correlationId || req.headers['x-correlation-id'] as string;
      
      const response = await service.validateBatch(request);
      sendJson(res, 200, response);
      return;
    }

    // Get all rules
    if (pathname === '/api/rules' && method === 'GET') {
      const rules = service.getRules();
      sendJson(res, 200, { rules, count: rules.length });
      return;
    }

    // Get rules by category
    if (pathname.startsWith('/api/rules/category/') && method === 'GET') {
      const category = pathname.replace('/api/rules/category/', '');
      const rules = service.getRulesByCategory(category);
      sendJson(res, 200, { rules, count: rules.length, category });
      return;
    }

    // Metrics endpoint
    if (pathname === '/metrics' || pathname === '/api/metrics') {
      const health = await service.getHealth();
      sendJson(res, 200, health.metrics);
      return;
    }

    // Dapr subscription endpoint
    if (pathname === '/dapr/subscribe' && method === 'GET') {
      sendJson(res, 200, [
        {
          pubsubname: config.dapr?.pubSubName || 'pubsub',
          topic: config.kafka.inboundTopic,
          route: '/api/dapr/claims',
        },
      ]);
      return;
    }

    // Dapr claims endpoint
    if (pathname === '/api/dapr/claims' && method === 'POST') {
      const body = await parseBody(req);
      const cloudEvent = JSON.parse(body);
      const claim = cloudEvent.data as X12_837_Claim;
      
      const response = await service.validateClaim({
        claim,
        correlationId: cloudEvent.id,
      });
      
      sendJson(res, 200, { success: true, validationResult: response.result });
      return;
    }

    // 404 Not Found
    sendJson(res, 404, { error: 'Not found', path: pathname });
  } catch (error) {
    console.error('Request error:', error);
    sendJson(res, 500, {
      error: 'Internal server error',
      message: error instanceof Error ? error.message : 'Unknown error',
    });
  }
}

// Create and start server
const PORT = parseInt(process.env.PORT || '3000', 10);

const server = http.createServer(handleRequest);

// Graceful shutdown
async function shutdown(): Promise<void> {
  console.log('Shutting down...');
  await service.close();
  server.close(() => {
    console.log('Server closed');
    process.exit(0);
  });
}

process.on('SIGTERM', shutdown);
process.on('SIGINT', shutdown);

// Start server
async function start(): Promise<void> {
  try {
    // Initialize service (connect to Azure resources)
    // Skip initialization in development mode without Azure resources
    if (config.cosmosDb.endpoint) {
      await service.initialize();
      console.log('Claims Scrubbing Service initialized');
    } else {
      console.log('Running in development mode (no Azure resources)');
    }

    server.listen(PORT, () => {
      console.log(`Claims Scrubbing Service listening on port ${PORT}`);
      console.log(`Endpoints: /livez, /readyz, /api/claims/validate, /api/rules`);
    });
  } catch (error) {
    console.error('Failed to start service:', error);
    process.exit(1);
  }
}

start();

// Export for testing only - not exposed in production builds
// These exports allow unit tests to access the service and config
// In production, the service runs as an HTTP server and doesn't export these
const isTest = process.env.NODE_ENV === 'test';
export const testService = isTest ? service : undefined;
export const testConfig = isTest ? config : undefined;
