# ADR 005: Kubernetes-First Runtime

## Status

Accepted

## Context

CloudHealthOffice needs to run multiple payer-domain services, workflow jobs,
benchmark jobs, and operator surfaces in a repeatable way across local
development and cloud environments.

## Decision

CloudHealthOffice is Kubernetes-first. Services, workflows, benchmark jobs, and
operational evidence should be runnable in containers and deployable to
Kubernetes.

## Consequences

Positive:

- Local Docker Desktop Kubernetes can exercise production-shaped workflows.
- Argo Workflows and Kubernetes Jobs can run batch and benchmark workloads.
- Service dependencies can be made explicit through manifests, health checks,
  and deployment docs.
- The platform avoids being locked to one cloud workflow runtime.

Tradeoffs:

- Contributors need basic Kubernetes familiarity.
- Local resource limits matter and must be documented.
- Deployment docs must distinguish local evidence from production capacity.

## References

- [Deployment guide](../deployment/DEPLOYMENT.md)
- [Architecture index](../architecture/README.md)
- [Million Claim Challenge benchmarks](../benchmarks/README.md)
