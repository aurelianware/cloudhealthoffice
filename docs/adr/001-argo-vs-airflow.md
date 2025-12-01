# ADR 001: Argo Workflows vs Apache Airflow for EDI Orchestration

## Status

**Accepted**

## Context

Cloud Health Office needs to migrate X12 EDI processing from Azure Logic Apps to a cloud-agnostic orchestration platform. The primary candidates considered were:

1. **Argo Workflows** - Kubernetes-native workflow engine
2. **Apache Airflow** - Python-based workflow orchestration
3. **Temporal** - Durable execution platform
4. **Prefect** - Modern Python workflow orchestration

## Decision

We will use **Argo Workflows** for X12 EDI processing orchestration.

## Rationale

### Argo Workflows Advantages

1. **Kubernetes-Native**
   - Runs directly on Kubernetes without additional infrastructure
   - Uses Kubernetes primitives (Pods, Secrets, ConfigMaps)
   - Leverages Kubernetes RBAC and network policies
   - Native scaling with Kubernetes HPA

2. **Container-First Design**
   - Each step runs in an isolated container
   - Easy to build and test containers independently
   - No language runtime dependencies
   - Immutable, version-controlled workflow steps

3. **DAG Support**
   - Native directed acyclic graph execution
   - Parallel step execution
   - Conditional branching
   - Artifact passing between steps

4. **Event-Driven with Argo Events**
   - Integrates with Argo Events for triggers
   - Supports Kafka, SFTP, webhooks, and calendar events
   - Sensor-based workflow triggering

5. **HIPAA Compliance**
   - No external SaaS dependencies
   - Data stays within cluster
   - Full audit logging
   - Secret management via Kubernetes Secrets

### Comparison with Alternatives

| Feature | Argo Workflows | Apache Airflow | Temporal |
|---------|----------------|----------------|----------|
| Kubernetes-Native | ✅ Yes | ⚠️ KubernetesExecutor only | ⚠️ Requires operator |
| Container Isolation | ✅ Per step | ⚠️ Shared worker | ✅ Per activity |
| Event Triggers | ✅ Argo Events | ⚠️ Limited | ⚠️ Limited |
| DAG Visualization | ✅ Built-in | ✅ Built-in | ⚠️ Basic |
| Learning Curve | Medium | Medium | High |
| Python Required | ❌ No | ✅ Yes | ❌ No (but SDKs) |
| CNCF Project | ✅ Graduated | ❌ No (Apache) | ❌ No |

### Why Not Airflow?

- Requires Python runtime and DAG compilation
- Worker-based model less suited for burst workloads
- External database (PostgreSQL/MySQL) required
- More complex deployment for Kubernetes

### Why Not Temporal?

- Higher complexity for simple workflows
- SDK integration required in application code
- Newer project with smaller community
- Overkill for file-based EDI processing

## Consequences

### Positive

- Simplified deployment with single Helm chart
- Native integration with Kubernetes ecosystem
- Container-based testing and CI/CD
- Reduced operational complexity
- Event-driven architecture with Argo Events

### Negative

- Less mature than Airflow for complex scheduling
- Requires Kubernetes expertise
- No built-in backfill capability
- Limited non-Kubernetes deployment options

### Mitigations

- Use CronWorkflows for scheduled processing
- Document Kubernetes operations thoroughly
- Implement manual replay workflow for backfills
- Standardize on managed Kubernetes (AKS/EKS/GKE)

## References

- [Argo Workflows Documentation](https://argoproj.github.io/argo-workflows/)
- [CNCF Argo Project](https://www.cncf.io/projects/argo/)
- [Kubernetes Patterns for Microservices](https://kubernetes.io/docs/concepts/workloads/)
