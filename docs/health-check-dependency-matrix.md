# CHO Microservice Health Check Dependency Matrix

Each service exposes three standard health check endpoints via `CloudHealthOffice.Infrastructure`:

| Endpoint         | Purpose          | Behavior                                      |
|------------------|------------------|-----------------------------------------------|
| `/health`        | All checks       | Runs every registered check                   |
| `/health/live`   | Liveness probe   | Returns `Healthy` if the process is running   |
| `/health/ready`  | Readiness probe  | Checks all downstream dependencies            |

## Dependency Checklist by Service

| Service                    | MongoDB | Redis | HTTP Dependencies              | Notes                                |
|----------------------------|---------|-------|--------------------------------|--------------------------------------|
| claims-service             | X       |       |                                | Uses shared infra (`AddChoInfrastructure`) |
| benefit-plan-service       | X       | X     | claims-service `/health/live`  | Redis for accumulator cache          |
| payment-service            | X       |       | claims-service `/health/live`  | 835 ERA processing                   |
| eligibility-service        | X       |       |                                | 270/271 eligibility                  |
| member-service             | X       |       |                                | Member demographics                  |
| provider-service           | X       |       |                                | Provider directory                   |
| enrollment-import-service  | X       |       |                                | X12 834 import                       |
| authorization-service      | X       |       |                                | 278 prior auth                       |
| coverage-service           | X       |       |                                | Member-Sponsor-Plan linkage          |
| sponsor-service            | X       |       |                                | Employer/group sponsors              |
| encounter-service          | X       |       |                                | 837 encounter submission             |
| risk-adjustment-service    | X       |       |                                | HCC risk scoring                     |
| premium-billing-service    | X       |       |                                | Premium invoicing                    |
| smart-auth-service         | X       |       |                                | SMART on FHIR OIDC server           |
| fhir-service               | X       |       |                                | FHIR R4 API                          |
| attachment-service         | X       |       |                                | 275 clinical attachments             |
| appeals-service            | X       |       |                                | Claim appeals                        |
| rfai-service               | X       |       |                                | Request for Additional Info          |
| reference-data-service     |         |       |                                | PostgreSQL (NpgSql health check)     |
| trading-partner-service    | X       |       |                                | EDI trading partners                 |
| tenant-service             | X       |       |                                | SaaS tenant management               |

## How Health Checks Are Configured

All services use `CloudHealthOffice.Infrastructure.HealthChecks.AddChoHealthChecks()`:

```csharp
// MongoDB-only service (most services)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
});

// Service with Redis + HTTP dependency (benefit-plan-service)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.RedisConnectionString = builder.Configuration["Redis:ConnectionString"];
    options.HttpDependencies["claims-service"] = "http://claims-service:8080/health/live";
});
```

Health check endpoint mapping:
```csharp
app.MapChoHealthChecks(); // Maps /health, /health/live, /health/ready
```

## Docker HEALTHCHECK

All Dockerfiles use the liveness probe:
```dockerfile
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1
```

## Kubernetes Probe Configuration (Recommended)

```yaml
livenessProbe:
  httpGet:
    path: /health/live
    port: 8080
  initialDelaySeconds: 10
  periodSeconds: 30
readinessProbe:
  httpGet:
    path: /health/ready
    port: 8080
  initialDelaySeconds: 5
  periodSeconds: 10
```
