# Load Testing — Cloud Health Office

## Overview

Load tests validate that critical services (claims processing, payment/ERA generation) meet SLA targets under sustained production-level throughput. Tests use [NBomber](https://nbomber.com/) for HTTP load generation with built-in reporting.

## Running Locally

```bash
# Start required services
docker compose up -d claims-service payment-service mongodb redis

# Run all load tests
dotnet test tests/CloudHealthOffice.LoadTests

# Run a specific scenario
dotnet test tests/CloudHealthOffice.LoadTests --filter "ClaimSubmission"
```

## Configuration

All parameters are configurable via environment variables (see `LoadTestConfig.cs`):

| Variable | Default | Description |
|----------|---------|-------------|
| `LOAD_TEST_CLAIMS_URL` | `http://localhost:5001` | Claims service endpoint |
| `LOAD_TEST_PAYMENT_URL` | `http://localhost:5003` | Payment service endpoint |
| `LOAD_TEST_TARGET_RPS` | `50` | Requests per second at peak |
| `LOAD_TEST_SUSTAIN_SECS` | `30` | Duration of sustained load phase |
| `LOAD_TEST_MAX_P99_MS` | `2000` | Maximum acceptable p99 latency (ms) |
| `LOAD_TEST_MAX_ERROR_RATE` | `0.01` | Maximum acceptable error rate (1%) |

## CI/CD Integration

Load tests run as part of the `quality-gate.yml` pipeline:
- **Nightly**: Automatically via cron schedule (4 AM UTC)
- **On-demand**: Via `workflow_dispatch` with `run_load: true`

Results are published as artifacts (`load-test-results`) with HTML and CSV reports.

## Adding Load Tests for New Modules

1. Create a new `*LoadTests.cs` file in this project
2. Use `LoadTestConfig` for endpoints, durations, and SLA thresholds
3. Define NBomber scenarios with inject/ramp-up profiles
4. Assert on p99 latency and error rate from NBomber stats
5. The quality-gate pipeline automatically discovers new test files

## SLA Targets

| Metric | Target | Rationale |
|--------|--------|-----------|
| p99 Latency | < 2000ms | Healthcare EDI processing requires near-real-time responses |
| Error Rate | < 1% | Payment processing must be highly reliable |
| Health p50 | < 100ms | Infrastructure baseline |
