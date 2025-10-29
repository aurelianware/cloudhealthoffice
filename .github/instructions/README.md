# Copilot Path-Specific Instructions

This directory contains path-specific custom instructions for GitHub Copilot. These instructions provide context-aware guidance when working on different parts of the codebase.

## How It Works

GitHub Copilot automatically uses these instructions when you're working on files that match the specified paths. If multiple instruction files match, Copilot combines them with the repository-wide instructions from `.github/copilot-instructions.md`.

## Instruction Files

### [workflows.instructions.md](workflows.instructions.md)
**Applies to**: `.github/workflows/*.yml`

Guidance for GitHub Actions workflows including:
- OIDC authentication best practices
- Deployment workflow structure
- YAML conventions and validation
- Common deployment scenarios

### [infrastructure.instructions.md](infrastructure.instructions.md)
**Applies to**: `infra/*.bicep`, `attachments_logicapps_package/*.bicep`

Guidance for Azure Bicep infrastructure templates including:
- Resource configuration patterns
- Validation and deployment processes
- Expected warnings and troubleshooting
- HIPAA compliance requirements

### [logicapps.instructions.md](logicapps.instructions.md)
**Applies to**: `logicapps/workflows/*/workflow.json`

Guidance for Logic Apps workflow definitions including:
- Workflow structure requirements (all must be Stateful)
- X12 EDI transaction processing (275, 277, 278)
- Integration Account setup
- Testing and troubleshooting

### [scripts.instructions.md](scripts.instructions.md)
**Applies to**: `*.ps1` files in repository root and `scripts/` directory

Guidance for PowerShell scripts including:
- PowerShell conventions and best practices
- Script structure and error handling
- Azure CLI integration
- Security and secrets management

## Benefits

Path-specific instructions help Copilot:
- Understand the context of what you're working on
- Provide more accurate suggestions
- Follow project-specific conventions
- Avoid common pitfalls in each area

## Creating New Instructions

To add instructions for a new area:

1. Create a file named `NAME.instructions.md` in this directory
2. Start with `**Applies to**: path/pattern` to specify which files it applies to
3. Provide context, conventions, examples, and common scenarios
4. Keep it focused on that specific area
5. Use Markdown formatting for readability

## Learn More

- [GitHub Copilot Custom Instructions Documentation](https://docs.github.com/en/copilot/customizing-copilot/adding-custom-instructions-for-github-copilot)
- [Repository-wide instructions](../copilot-instructions.md)
- [Best practices for Copilot coding agent](https://gh.io/copilot-coding-agent-tips)
