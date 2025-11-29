/**
 * Cloud Health Office - Eligibility Service Entry Point
 * 
 * HTTP server for the eligibility service supporting:
 * - X12 270/271 EDI endpoints
 * - FHIR R4 CoverageEligibilityRequest/Response endpoints
 * - Health check endpoints
 * - Dapr integration
 */

import * as http from 'http';
import { URL } from 'url';
import { CoverageEligibilityRequest } from 'fhir/r4';
import { EligibilityService } from './eligibility-service';
import { X12_270_Request, EligibilityServiceConfig, EligibilityCheckRequest } from './types';
import { X12EligibilityMapper } from './x12-mapper';
import { loadQNXTRulesFromCSV } from './migration';

// Default configuration
const DEFAULT_CONFIG: EligibilityServiceConfig = {
  cosmosDb: {
    endpoint: process.env.COSMOS_ENDPOINT || 'https://localhost:8081',
    databaseName: process.env.COSMOS_DATABASE || 'eligibility-db',
    containerName: process.env.COSMOS_CONTAINER || 'eligibility-cache',
    defaultTtlSeconds: 86400 // 24 hours
  },
  eventGrid: {
    topicEndpoint: process.env.EVENT_GRID_ENDPOINT || ''
  },
  qnxt: process.env.QNXT_BASE_URL ? {
    baseUrl: process.env.QNXT_BASE_URL,
    timeout: 30000
  } : undefined,
  fhirServer: process.env.FHIR_SERVER_URL ? {
    baseUrl: process.env.FHIR_SERVER_URL,
    authType: 'managed_identity'
  } : undefined,
  cache: {
    enabled: process.env.CACHE_ENABLED !== 'false',
    activeMemberTtl: parseInt(process.env.CACHE_ACTIVE_TTL || '86400', 10), // 24 hours
    inactiveMemberTtl: parseInt(process.env.CACHE_INACTIVE_TTL || '3600', 10), // 1 hour
    maxCacheAge: parseInt(process.env.CACHE_MAX_AGE || '43200', 10) // 12 hours
  },
  dapr: {
    enabled: process.env.DAPR_ENABLED === 'true',
    httpPort: parseInt(process.env.DAPR_HTTP_PORT || '3500', 10),
    grpcPort: parseInt(process.env.DAPR_GRPC_PORT || '50001', 10),
    appId: process.env.DAPR_APP_ID || 'eligibility-service',
    stateStoreName: process.env.DAPR_STATE_STORE || 'eligibility-state',
    pubSubName: process.env.DAPR_PUBSUB || 'eligibility-pubsub'
  }
};

// Initialize service
let eligibilityService: EligibilityService;
const x12Mapper = new X12EligibilityMapper();

/**
 * Parse request body as JSON
 */
async function parseBody(req: http.IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    req.on('data', chunk => chunks.push(chunk));
    req.on('end', () => resolve(Buffer.concat(chunks).toString()));
    req.on('error', reject);
  });
}

/**
 * Send JSON response
 */
function sendJson(res: http.ServerResponse, statusCode: number, data: unknown): void {
  res.writeHead(statusCode, { 
    'Content-Type': 'application/json',
    'X-Content-Type-Options': 'nosniff'
  });
  res.end(JSON.stringify(data));
}

/**
 * Send FHIR JSON response
 */
function sendFhirJson(res: http.ServerResponse, statusCode: number, data: unknown): void {
  res.writeHead(statusCode, { 
    'Content-Type': 'application/fhir+json',
    'X-Content-Type-Options': 'nosniff'
  });
  res.end(JSON.stringify(data));
}

/**
 * Handle X12 270 eligibility request
 */
async function handleX12Eligibility(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
  try {
    const body = await parseBody(req);
    const contentType = req.headers['content-type'] || '';
    
    let x12Request: X12_270_Request;
    
    if (contentType.includes('application/json')) {
      // JSON format request
      x12Request = JSON.parse(body);
    } else if (contentType.includes('application/x12') || contentType.includes('text/plain')) {
      // Raw X12 EDI format
      x12Request = x12Mapper.parseX12270(body);
    } else {
      // Assume JSON by default
      x12Request = JSON.parse(body);
    }
    
    const url = new URL(req.url || '', `http://${req.headers.host}`);
    const skipCache = url.searchParams.get('skipCache') === 'true';
    const correlationId = req.headers['x-correlation-id'] as string || undefined;
    
    const response = await eligibilityService.checkX12Eligibility(
      x12Request,
      skipCache,
      correlationId
    );
    
    // Check Accept header for response format
    const accept = req.headers['accept'] || 'application/json';
    
    if (accept.includes('application/x12')) {
      // Return raw X12 271 EDI
      res.writeHead(200, { 
        'Content-Type': 'application/x12',
        'X-Response-Time-Ms': response.responseTimeMs.toString(),
        'X-From-Cache': response.fromCache.toString()
      });
      const x12Response = x12Mapper.generateX12271(response.x12Response!);
      res.end(x12Response);
    } else {
      // Return JSON
      res.writeHead(200, {
        'Content-Type': 'application/json',
        'X-Response-Time-Ms': response.responseTimeMs.toString(),
        'X-From-Cache': response.fromCache.toString()
      });
      res.end(JSON.stringify(response));
    }
  } catch (error) {
    console.error('X12 eligibility check failed:', error);
    sendJson(res, 500, { 
      error: 'Eligibility check failed',
      message: error instanceof Error ? error.message : 'Unknown error'
    });
  }
}

/**
 * Handle FHIR CoverageEligibilityRequest
 */
async function handleFHIREligibility(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
  try {
    const body = await parseBody(req);
    const fhirRequest: CoverageEligibilityRequest = JSON.parse(body);
    
    // Validate resource type
    if (fhirRequest.resourceType !== 'CoverageEligibilityRequest') {
      sendFhirJson(res, 400, {
        resourceType: 'OperationOutcome',
        issue: [{
          severity: 'error',
          code: 'invalid',
          diagnostics: 'Expected resourceType CoverageEligibilityRequest'
        }]
      });
      return;
    }
    
    const url = new URL(req.url || '', `http://${req.headers.host}`);
    const skipCache = url.searchParams.get('skipCache') === 'true';
    const correlationId = req.headers['x-correlation-id'] as string || undefined;
    
    const response = await eligibilityService.checkFHIREligibility(
      fhirRequest,
      skipCache,
      correlationId
    );
    
    res.writeHead(200, {
      'Content-Type': 'application/fhir+json',
      'X-Response-Time-Ms': response.responseTimeMs.toString(),
      'X-From-Cache': response.fromCache.toString()
    });
    res.end(JSON.stringify(response.fhirResponse));
  } catch (error) {
    console.error('FHIR eligibility check failed:', error);
    sendFhirJson(res, 500, {
      resourceType: 'OperationOutcome',
      issue: [{
        severity: 'error',
        code: 'exception',
        diagnostics: error instanceof Error ? error.message : 'Unknown error'
      }]
    });
  }
}

/**
 * Handle unified eligibility check
 */
async function handleUnifiedEligibility(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
  try {
    const body = await parseBody(req);
    const request: EligibilityCheckRequest = JSON.parse(body);
    
    const correlationId = req.headers['x-correlation-id'] as string || request.correlationId;
    request.correlationId = correlationId;
    
    const response = await eligibilityService.checkEligibility(request);
    
    res.writeHead(200, {
      'Content-Type': 'application/json',
      'X-Response-Time-Ms': response.responseTimeMs.toString(),
      'X-From-Cache': response.fromCache.toString()
    });
    res.end(JSON.stringify(response));
  } catch (error) {
    console.error('Unified eligibility check failed:', error);
    sendJson(res, 500, {
      error: 'Eligibility check failed',
      message: error instanceof Error ? error.message : 'Unknown error'
    });
  }
}

/**
 * Handle health check
 */
async function handleHealth(res: http.ServerResponse): Promise<void> {
  try {
    const health = await eligibilityService.getHealth();
    const statusCode = health.status === 'healthy' ? 200 : health.status === 'degraded' ? 200 : 503;
    sendJson(res, statusCode, health);
  } catch (error) {
    sendJson(res, 503, {
      status: 'unhealthy',
      error: error instanceof Error ? error.message : 'Unknown error'
    });
  }
}

/**
 * Handle liveness check (for Kubernetes)
 */
function handleLiveness(res: http.ServerResponse): void {
  sendJson(res, 200, { status: 'alive' });
}

/**
 * Handle readiness check (for Kubernetes)
 */
async function handleReadiness(res: http.ServerResponse): Promise<void> {
  try {
    const health = await eligibilityService.getHealth();
    if (health.status === 'unhealthy') {
      sendJson(res, 503, { status: 'not ready', checks: health.checks });
    } else {
      sendJson(res, 200, { status: 'ready' });
    }
  } catch (error) {
    sendJson(res, 503, { status: 'not ready' });
  }
}

/**
 * Handle Dapr subscription endpoint
 */
function handleDaprSubscribe(res: http.ServerResponse): void {
  sendJson(res, 200, [{
    pubsubname: DEFAULT_CONFIG.dapr.pubSubName,
    topic: 'eligibility-requests',
    route: '/api/dapr/eligibility'
  }]);
}

/**
 * Main request handler
 */
async function handleRequest(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
  const url = new URL(req.url || '', `http://${req.headers.host}`);
  const path = url.pathname;
  const method = req.method || 'GET';
  
  // CORS headers
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-Correlation-Id');
  
  if (method === 'OPTIONS') {
    res.writeHead(204);
    res.end();
    return;
  }
  
  try {
    // Route handling
    if (path === '/api/eligibility/x12' && method === 'POST') {
      await handleX12Eligibility(req, res);
    } else if (path === '/api/eligibility/fhir' && method === 'POST') {
      await handleFHIREligibility(req, res);
    } else if ((path === '/fhir/CoverageEligibilityRequest' || path === '/CoverageEligibilityRequest/$submit') && method === 'POST') {
      await handleFHIREligibility(req, res);
    } else if (path === '/api/eligibility' && method === 'POST') {
      await handleUnifiedEligibility(req, res);
    } else if (path === '/health' || path === '/api/health') {
      await handleHealth(res);
    } else if (path === '/healthz' || path === '/livez') {
      handleLiveness(res);
    } else if (path === '/readyz') {
      await handleReadiness(res);
    } else if (path === '/dapr/subscribe' && method === 'GET') {
      handleDaprSubscribe(res);
    } else if (path === '/api/dapr/eligibility' && method === 'POST') {
      await handleUnifiedEligibility(req, res);
    } else {
      sendJson(res, 404, { error: 'Not found', path });
    }
  } catch (error) {
    console.error('Request handler error:', error);
    sendJson(res, 500, { 
      error: 'Internal server error',
      message: error instanceof Error ? error.message : 'Unknown error'
    });
  }
}

/**
 * Start the server
 */
async function startServer(): Promise<void> {
  // Initialize the eligibility service
  eligibilityService = new EligibilityService(DEFAULT_CONFIG);
  
  // Load QNXT rules if CSV file is provided
  const rulesFile = process.env.QNXT_RULES_FILE;
  if (rulesFile) {
    try {
      const rules = await loadQNXTRulesFromCSV(rulesFile);
      eligibilityService.loadEligibilityRules(rules);
      console.log(`Loaded ${rules.length} eligibility rules from ${rulesFile}`);
    } catch (error) {
      console.warn(`Failed to load QNXT rules: ${error}`);
    }
  }
  
  const port = parseInt(process.env.PORT || '3000', 10);
  const server = http.createServer(handleRequest);
  
  server.listen(port, () => {
    console.log(`Eligibility service listening on port ${port}`);
    console.log(`X12 270/271 endpoint: POST /api/eligibility/x12`);
    console.log(`FHIR endpoint: POST /api/eligibility/fhir`);
    console.log(`FHIR endpoint: POST /fhir/CoverageEligibilityRequest`);
    console.log(`Unified endpoint: POST /api/eligibility`);
    console.log(`Health endpoint: GET /health`);
    if (DEFAULT_CONFIG.dapr.enabled) {
      console.log(`Dapr enabled with app ID: ${DEFAULT_CONFIG.dapr.appId}`);
    }
  });
}

// Start the server
startServer().catch(error => {
  console.error('Failed to start server:', error);
  process.exit(1);
});

export { startServer, handleRequest };
