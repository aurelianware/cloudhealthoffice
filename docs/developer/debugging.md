# Debugging

## First Checks

- Confirm the service is running.
- Check health endpoints.
- Verify environment variables and connection strings.
- Confirm Docker/Kubernetes context.
- Check whether the tenant is fresh or long-lived.

## Useful Commands

```bash
docker compose ps
docker compose logs --tail=200
kubectl get pods
kubectl logs <pod-name> --tail=200
dotnet test <project>.csproj --logger "console;verbosity=normal"
```

## Portal Issues

- Check the browser route and the API route separately.
- Confirm the portal service client points to the running backend.
- Use claim IDs and run IDs from the Mass Adjudication console when debugging
  benchmark evidence.
- Reproduce with synthetic claims only.

## Benchmark Issues

- Save the command, commit SHA, parallelism, seed, claim count, and output.
- Separate preparation failures from timed adjudication failures.
- Check unsupported scenarios before treating a run as mismatched.
- Check payment-comparable scope before using payment deltas.

## What Not To Share

Do not post real PHI, production logs, real claim IDs, credentials, bearer
tokens, or private tenant details in public issues or discussions.
