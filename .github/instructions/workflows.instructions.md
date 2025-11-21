# GitHub Actions Workflows Instructions

**Applies to**: `.github/workflows/*.yml`

## Overview

This directory contains GitHub Actions workflow files for CI/CD automation of the Cloud Health Office Logic Apps solution.

## Workflow Files

### Deployment Workflows
- **deploy.yml**: Production deployment (manual trigger only)
- **deploy-dev.yml**: DEV environment auto-deployment
- **deploy-uat.yml**: UAT environment auto-deployment (triggers on `release/*` branches)

### Validation Workflows
- **pr-lint.yml**: Pull request validation (JSON, YAML, Bicep, PowerShell)
- **sanity.yml**: Basic health checks
- **debug-oidc.yml**: OIDC authentication debugging

## Best Practices

### Authentication
- **Always use OIDC** (OpenID Connect) for Azure authentication
- Never store Azure credentials as secrets
- Use environment-specific secrets: `AZURE_CLIENT_ID_{ENV}`, `AZURE_TENANT_ID_{ENV}`, `AZURE_SUBSCRIPTION_ID_{ENV}`

### Workflow Structure
- Use matrix strategy for multi-environment deployments
- Set appropriate timeouts (30+ minutes for deployments)
- Include validation steps before deployment
- Add health checks after deployment

### YAML Conventions
- Line length restrictions are disabled (long parameter lines are acceptable)
- For boolean fields, use only `true` and `false` (do not use `on`, `off`, `yes`, or `no`).  
  - Note: `on` and `off` are reserved for workflow trigger events and should not be used as boolean values.
- Always include descriptive `name` fields for steps
- Use `reviewdog/action-actionlint` for workflow validation

### Deployment Process
Each deployment workflow should:
1. Validate inputs and checkout code
2. Authenticate with Azure using OIDC
3. Run Bicep validation and ARM What-If analysis
4. Deploy infrastructure via Bicep
5. Package and deploy Logic App workflows (ZIP)
6. Restart Logic App
7. Run health checks and report status

### Common Pitfalls
- **Don't cancel deployments**: Set adequate timeouts (30+ minutes)
- **Bicep warnings**: "use-parent-property" warnings for Service Bus topics are expected and safe
- **ZIP structure**: Ensure workflows.zip contains `workflows/` as top-level directory
- **Parallel deployments**: Avoid deploying to same environment concurrently

### Lint Command
```bash
# Run actionlint locally
actionlint -color .github/workflows/*.yml

# Run yamllint
yamllint .github/workflows/
```

## Modifying Workflows

### Adding a New Environment
1. Create environment-specific secrets in GitHub
2. Add matrix entry or new workflow file
3. Update resource naming pattern
4. Test in non-production first

### Changing Deployment Steps
1. Always preserve validation steps
2. Test Bicep changes locally first
3. Keep health checks at the end
4. Update timeout if adding long-running steps

### Testing Workflow Changes
- Use workflow_dispatch for manual testing
- Test in DEV environment first
- Review ARM What-If output before production
- Monitor Application Insights during deployment
