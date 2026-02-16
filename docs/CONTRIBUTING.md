# Contributing to Cloud Health Office

Welcome to the Cloud Health Office platform repository! We’re building a modern integration layer for health plans running legacy systems, helping them achieve CMS-0057-F compliance and reduce dependency on expensive vendor implementations.

## Table of Contents

- [About the Project](#about-the-project)
- [Ways to Contribute](#ways-to-contribute)
- [License and Contribution Agreement](#license-and-contribution-agreement)
- [Getting Started](#getting-started)
- [Development Setup](#development-setup)
- [Development Workflow](#development-workflow)
- [Validation and Testing](#validation-and-testing)
- [Code Review Standards](#code-review-standards)
- [Best Practices](#best-practices)

## About the Project

Cloud Health Office is an open-source healthcare integration platform built by former healthcare payer architects and implementation specialists. We’re solving real problems faced by health plans:

- **CMS-0057-F Compliance**: January 2027 deadline approaching, $2M+ vendor upgrades required
- **Vendor Lock-in**: Expensive BPaaS dependencies with limited operational control
- **Integration Complexity**: 6-18 month implementation timelines for basic EDI connections
- **Multi-Clearinghouse**: Business continuity risk from single-vendor dependency

### Target Audience

This platform serves:

- **Medicaid Managed Care Organizations** (MCOs)
- **Medicare Advantage Plans**
- **Commercial Health Plans** running aging Core Admin platforms
- **Third-Party Administrators** (TPAs) needing modern EDI infrastructure

### Core Capabilities

- FHIR R4 APIs (Patient Access, Provider Access, Prior Authorization, Payer-to-Payer)
- X12 EDI (270/271, 275, 276/277, 278, 835, 837)
- Multi-clearinghouse support (Availity, Change Healthcare, Optum, Inovalon)
- Azure Logic Apps OR Kubernetes multi-cloud deployment
- Core system integration without core system replacement

## Ways to Contribute

### For Healthcare IT Professionals

Even if you’re not a developer, you can contribute valuable domain expertise:

- **Document Use Cases**: Share your health plan’s integration challenges
- **Provide Feedback**: Test features and report what works/doesn’t work
- **Compliance Review**: Review CMS-0057-F implementation for accuracy
- **Integration Testing**: Help validate connectors with your legacy systems

**How to start**: Open a [GitHub Discussion](https://github.com/aurelianware/cloudhealthoffice/discussions) to share your experience.

### For Developers

We welcome code contributions in several areas:

- **Core System Connectors**: Epic Tapestry, HealthEdge, additional CAPS platforms
- **FHIR APIs**: Enhancements to Patient/Provider Access APIs
- **EDI Processing**: Additional X12 transaction types, clearinghouse integrations
- **Security**: HIPAA compliance improvements, audit logging, encryption
- **DevOps**: CI/CD improvements, deployment automation, monitoring

**How to start**: See [Development Setup](#development-setup) below.

### For Technical Writers

Help us improve documentation:

- **User Guides**: Deployment guides for different health plan sizes
- **Integration Guides**: Step-by-step backend system connection procedures
- **Compliance Docs**: CMS-0057-F checklists and validation procedures
- **Troubleshooting**: Common issues and solutions

**How to start**: Fork the repo and submit PRs to improve any `.md` files.

### For Security Researchers

Help us maintain HIPAA compliance:

- **Security Audits**: Review code for PHI handling, encryption, access controls
- **Penetration Testing**: Identify vulnerabilities (please report privately)
- **Compliance Verification**: Validate against HIPAA Security Rule requirements
- **Threat Modeling**: Identify potential attack vectors in healthcare workflows

**How to start**: Review <SECURITY.md> and report findings via our security process.

## License and Contribution Agreement

This project is licensed under the **Apache License 2.0**. By contributing to this project, you agree that your contributions will be licensed under the same license.

### Key Points About Contributing

- **License Grant**: All contributions are subject to the Apache License 2.0
- **Patent Grant**: Contributors grant a patent license for their contributions as defined in the Apache License 2.0
- **Copyright**: Contributors retain copyright to their contributions while granting the project rights under Apache 2.0
- **HIPAA Compliance**: Contributors should be aware this project handles PHI; review <SECURITY.md> for compliance guidelines

For the full license text, see <LICENSE>.

### Why Apache 2.0?

We chose Apache 2.0 because it:

- Is widely accepted in healthcare and enterprise environments
- Provides patent protection for contributors and users
- Supports commercial use in HIPAA-regulated environments (including by health plans)
- Has clear, well-understood terms for contributions
- Allows health plans to deploy internally without licensing concerns

## Getting Started

This repository implements both Azure Logic Apps and Kubernetes deployment options for processing HIPAA-compliant EDI transactions with secure X12 processing, Service Bus/Kafka messaging, and Data Lake storage.

### Quick Links

- 📖 **[Architecture Documentation](ARCHITECTURE.md)** - System design and data flows
- 🚀 **[Deployment Guide](DEPLOYMENT.md)** - Step-by-step deployment procedures
- 🌿 **[Branching Strategy](BRANCHING-STRATEGY.md)** - Branch conventions and merge requirements
- 🔐 **[Secrets Setup Guide](DEPLOYMENT-SECRETS-SETUP.md)** - GitHub Secrets and environment configuration
- 🔧 **[Troubleshooting Guide](TROUBLESHOOTING.md)** - Common issues and solutions
- 🔒 **[Security Guide](SECURITY.md)** - HIPAA compliance and secure development

### Prerequisites

Before you begin, ensure you have the following tools installed:

#### Required Tools

|Tool          |Minimum Version|Purpose                       |Installation                                                                                        |
|--------------|---------------|------------------------------|----------------------------------------------------------------------------------------------------|
|**Azure CLI** |2.77.0+        |Azure resource management     |[Install Guide](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli)                       |
|**Bicep CLI** |0.37.0+        |Infrastructure as Code        |`az bicep install`                                                                                  |
|**PowerShell**|7.4+           |Deployment scripts            |[Install Guide](https://docs.microsoft.com/en-us/powershell/scripting/install/installing-powershell)|
|**jq**        |1.7+           |JSON validation and processing|[Install Guide](https://stedolan.github.io/jq/download/)                                            |
|**Git**       |2.x+           |Version control               |[Install Guide](https://git-scm.com/downloads)                                                      |

#### For Kubernetes Deployment (Optional)

|Tool       |Minimum Version|Purpose                   |
|-----------|---------------|--------------------------|
|**kubectl**|1.28+          |Kubernetes CLI            |
|**helm**   |3.12+          |Kubernetes package manager|
|**Docker** |24.0+          |Container runtime         |

### Verify Prerequisites

```bash
# Check Azure CLI version
az --version

# Check Bicep CLI version
az bicep version

# Check PowerShell version
pwsh --version

# Check jq version
jq --version

# Check Git version
git --version

# Optional: Check Kubernetes tools
kubectl version --client
helm version
docker --version
```

### Azure Access Requirements

- Azure subscription with appropriate permissions
- Access to target resource groups (DEV/UAT/PROD)
- Azure Active Directory authentication configured
- OIDC federated credentials for GitHub Actions (for CI/CD)

## Development Setup

### 1. Clone the Repository

```bash
git clone https://github.com/aurelianware/cloudhealthoffice.git
cd cloudhealthoffice
```

### 2. Verify Repository Structure

Run the repository structure fix script to ensure proper layout:

```bash
pwsh -c "./fix_repo_structure.ps1 -RepoRoot ."
```

**Expected Output:**

```
Repository structure verified and normalized
✓ All workflows in correct locations
```

### 3. Validate Current State

Before making changes, validate the existing codebase:

```bash
# Validate JSON workflows (takes <1 second)
find logicapps/workflows -name "workflow.json" -exec jq . {} \;

# Validate Bicep templates (takes ~4 seconds)
az bicep build --file infra/main.bicep --outfile /tmp/arm.json

# Validate PowerShell scripts
pwsh -Command "Get-Content './test-workflows.ps1' | Out-Null"
```

All validation commands should complete without errors.

## Development Workflow

### Branching Strategy

We follow a structured branching strategy aligned with semantic versioning and automated deployments.

**For complete branching guidelines, see <BRANCHING-STRATEGY.md>**

**Quick Reference:**

```
main (protected)                    # Production-ready code, deploys to PROD
├── release/*          # UAT deployments (auto-deploys)
├── feature/*          # Feature development
├── bugfix/*           # Bug fixes
└── hotfix/*           # Critical production fixes
```

**Branch Naming Conventions:**

- Features: `feature/{issue-number}-{description}` or `feature/{description}`
- Bug fixes: `bugfix/{issue-number}-{description}` or `bugfix/{description}`
- Releases: `release/v{MAJOR}.{MINOR}.{PATCH}`
- Hotfixes: `hotfix/v{MAJOR}.{MINOR}.{PATCH+1}-{description}`

**Key Points:**

- All merges to `main` require PR approval and passing checks
- `release/*` branches auto-deploy to UAT
- Use conventional commits for semantic versioning
- See <BRANCHING-STRATEGY.md> for detailed workflows

### Making Changes

#### 1. Create a Feature Branch

```bash
# Create and checkout a new feature branch
git checkout -b feature/your-feature-name

# For bug fixes
git checkout -b bugfix/issue-description
```

#### 2. Make Your Changes

Follow these guidelines based on what you’re changing:

**Logic Apps Workflows** (`logicapps/workflows/*/workflow.json`):

- All workflows MUST be `"kind": "Stateful"`
- Required keys: `definition`, `kind`, `parameters`
- Validate JSON syntax after changes
- Test workflow structure validation

**Infrastructure** (`infra/*.bicep`):

- Follow Azure naming conventions
- Use descriptive parameter names with `@description` decorators
- Accept warnings about Service Bus topic parent properties (known issue)
- Test Bicep compilation after changes

**Kubernetes Manifests** (`k8s/*.yaml`, `argo-workflows/*.yaml`):

- Follow Kubernetes API conventions
- Include resource limits and requests
- Use namespaces appropriately
- Validate YAML syntax

**PowerShell Scripts** (`*.ps1`):

- Use approved PowerShell verbs (Get, Set, New, Remove, etc.)
- Follow PascalCase for functions and parameters
- Use kebab-case for file names
- Include comment-based help

**GitHub Actions** (`.github/workflows/*.yml`):

- Use only `true`/`false` for boolean values (not `on`/`off`/`yes`/`no`)
- Note: `on` is reserved for workflow trigger events
- Include descriptive `name` fields for steps
- Set appropriate timeouts (30+ minutes for deployments)

#### 3. Validate Your Changes

**ALWAYS validate before committing:**

```bash
# Run complete validation suite
./validate-changes.sh  # If available, or use commands below

# Validate JSON workflows
WF_PATH="logicapps/workflows"
find "$WF_PATH" -type f -name "workflow.json" -print0 | \
while IFS= read -r -d '' f; do
  echo "Checking $f"
  if ! jq . "$f" >/dev/null 2>&1; then
    echo "ERROR: Invalid JSON in $f"
    exit 1
  fi
  if ! jq -e 'has("definition") and has("kind") and has("parameters")' "$f" >/dev/null; then
    echo "ERROR: Missing required keys in $f"
    exit 1
  fi
done

# Validate Bicep templates
az bicep build --file infra/main.bicep --outfile /tmp/arm.json

# Validate Kubernetes manifests (if applicable)
kubectl apply --dry-run=client -f k8s/

# Validate PowerShell syntax
pwsh -Command "Get-ChildItem -Filter '*.ps1' -Recurse | ForEach-Object { $null = [System.Management.Automation.PSParser]::Tokenize((Get-Content $_.FullName -Raw), [ref]$null) }"
```

## Validation and Testing

### Automated Validation

GitHub Actions automatically validates all pull requests:

1. **Bicep Validation** (~4 seconds)
- Compiles all Bicep templates
- Checks for syntax errors
- Validates Azure resource configurations
1. **JSON Linting** (<1 second)
- Validates all workflow.json files
- Checks required keys (definition, kind, parameters)
- Verifies stateful workflow configuration
1. **PowerShell Testing**
- PSScriptAnalyzer for code quality
- Syntax validation
- Best practices compliance
1. **Branch Protection Checks**
- Conventional commit format
- PR title requirements
- Required reviews

### Manual Testing

Before submitting a PR, test locally:

**For Logic Apps Changes:**

```bash
# Test workflow deployment to DEV environment
./deploy-workflows.ps1 -Environment dev -WorkflowName your-workflow
```

**For Infrastructure Changes:**

```bash
# Deploy to DEV environment with what-if analysis
az deployment group what-if \
  --resource-group rg-dev \
  --template-file infra/main.bicep \
  --parameters @infra/dev.parameters.json
```

**For Kubernetes Changes:**

```bash
# Test Kubernetes manifests
kubectl apply --dry-run=client -f k8s/

# Test Argo Workflows
argo lint argo-workflows/
```

## Code Review Standards

### Pull Request Requirements

1. **Descriptive Title**: Use conventional commits format
- `feat(component): Add new feature`
- `fix(component): Resolve bug`
- `docs(component): Update documentation`
1. **Clear Description**: Explain:
- What problem does this solve?
- How does it solve it?
- What testing was performed?
- Any breaking changes?
1. **Linked Issues**: Reference related issues with `Fixes #123`
1. **Passing Checks**: All automated validation must pass
1. **Signed Commits**: DCO sign-off required (see below)

### Review Process

- All PRs require at least one approving review
- Maintainers will provide feedback within 48-72 hours
- Address review comments and re-request review
- Merges to `main` are performed by maintainers after approval

## Best Practices

### General Guidelines

1. **Follow Existing Patterns**: Review existing code for conventions
1. **Write Clear Comments**: Explain “why”, not just “what”
1. **Keep Changes Focused**: One feature/fix per PR
1. **Test Thoroughly**: Include both positive and negative test cases
1. **HIPAA Aware**: Maintain compliance with all changes

### Workflow Development

- Use meaningful action names that describe their purpose
- Include error handling for all external calls
- Add retry policies for transient failures (claims backend API, SFTP)
- Use Service Bus/Kafka for async/decoupled processing
- Archive all files to Data Lake with date partitioning

### Infrastructure Development

- Use descriptive parameter names with validation
- Follow Azure naming conventions: `{baseName}-{resource-type}`
- Enable hierarchical namespace for Data Lake Gen2
- Configure managed identity for all resource access
- Include Application Insights for monitoring

### Script Development

- Use `[CmdletBinding()]` for advanced PowerShell functions
- Implement try/catch error handling
- Clear sensitive variables after use
- Use `Join-Path` for cross-platform compatibility
- Set `$ErrorActionPreference = "Stop"` for consistent behavior

### Security Practices

- Use managed identities instead of connection strings
- Store secrets in Azure Key Vault or HashiCorp Vault
- Accept secrets as `[SecureString]` parameters
- Enable audit logging for all operations
- Follow principle of least privilege
- Review <SECURITY.md> for detailed guidelines

### Performance Tips

- Cache Azure CLI results instead of repeated calls
- Use `--query` to filter data early
- Use parallel execution for independent operations (PS 7+)
- Set appropriate timeouts for long-running operations
- Monitor Application Insights for bottlenecks

## Additional Resources

### Documentation

- [Architecture Overview](ARCHITECTURE.md) - System design and data flows
- [Deployment Guide](DEPLOYMENT.md) - Deployment procedures
- [Multi-Cloud Deployment](docs/MULTI-CLOUD-DEPLOYMENT.md) - Kubernetes deployment guide
- [CMS-0057-F Compliance](docs/CMS-0057-F-COMPLIANCE.md) - FHIR API implementation
- [Troubleshooting](TROUBLESHOOTING.md) - Common issues and solutions
- [Security Guide](SECURITY.md) - HIPAA compliance and security

### External Resources

- [Azure Logic Apps Documentation](https://docs.microsoft.com/en-us/azure/logic-apps/)
- [Kubernetes Documentation](https://kubernetes.io/docs/)
- [Argo Workflows Documentation](https://argoproj.github.io/argo-workflows/)
- [Bicep Documentation](https://docs.microsoft.com/en-us/azure/azure-resource-manager/bicep/)
- [X12 EDI Standards](https://x12.org/)
- [HIPAA Compliance Guide](https://www.hhs.gov/hipaa/index.html)
- [CMS Interoperability Rule](https://www.cms.gov/regulations-and-guidance/guidance/interoperability)

### GitHub Copilot Instructions

This repository includes comprehensive instructions for GitHub Copilot:

- [Repository-wide instructions](.github/copilot-instructions.md)
- [Workflow-specific guidance](.github/instructions/)

**Professional Tone for AI Contributions**: Ensure all AI-generated contributions maintain a professional, collaborative tone suitable for enterprise partnerships. Avoid hyperbolic marketing language in documentation and user-facing content.

## Getting Help

If you encounter issues:

1. Check <TROUBLESHOOTING.md> for common issues
1. Review existing [GitHub Issues](https://github.com/aurelianware/cloudhealthoffice/issues)
1. Check Application Insights for runtime errors
1. Create a new issue with detailed information:
- Steps to reproduce
- Expected vs actual behavior
- Error messages and logs
- Environment details (Azure/Kubernetes, versions, etc.)

## Questions?

For questions or clarifications:

- Open a [GitHub Discussion](https://github.com/aurelianware/cloudhealthoffice/discussions)
- Review existing documentation
- Check GitHub Copilot instructions for guidance

-----

## Legal and Licensing

### Developer Certificate of Origin (DCO)

All contributions to this project must be signed off under the [Developer Certificate of Origin (DCO)](https://developercertificate.org/). The DCO is a lightweight way to certify that you have the right to submit your contribution.

**What is the DCO?**

The DCO is a statement that you, as a contributor, have the legal right to make your contribution and are willing to have it distributed under the project’s license.

**Full text of the DCO (v1.1):**

```
Developer Certificate of Origin
Version 1.1

Copyright (C) 2004, 2006 The Linux Foundation and its contributors.

Everyone is permitted to copy and distribute verbatim copies of this
license document, but changing it is not allowed.


Developer's Certificate of Origin 1.1

By making a contribution to this project, I certify that:

(a) The contribution was created in whole or in part by me and I
    have the right to submit it under the open source license
    indicated in the file; or

(b) The contribution is based upon previous work that, to the best
    of my knowledge, is covered under an appropriate open source
    license and I have the right under that license to submit that
    work with modifications, whether created in whole or in part
    by me, under the same open source license (unless I am
    permitted to submit under a different license), as indicated
    in the file; or

(c) The contribution was provided directly to me by some other
    person who certified (a), (b) or (c) and I have not modified
    it.

(d) I understand and agree that this project and the contribution
    are public and that a record of the contribution (including all
    personal information I submit with it, including my sign-off) is
    maintained indefinitely and may be redistributed consistent with
    this project or the open source license(s) involved.
```

**How to Sign Off Your Commits**

Add a `Signed-off-by` line to your commit messages. The easiest way is to use the `-s` flag when committing:

```bash
git commit -s -m "feat(workflows): Add new 278 processing workflow"
```

This will automatically add a line like:

```
Signed-off-by: Your Name <your.email@example.com>
```

**Retroactively Signing Off Commits**

If you forgot to sign off, you can amend your commit:

```bash
# Amend the most recent commit
git commit --amend -s --no-edit

# For multiple commits, use interactive rebase
git rebase -i HEAD~N  # where N is the number of commits
# Then mark commits as 'edit' and run: git commit --amend -s --no-edit && git rebase --continue
```

**Configuring Git for Sign-off**

Ensure your Git identity is configured correctly:

```bash
git config --global user.name "Your Full Name"
git config --global user.email "your.email@example.com"
```

### Contributor License Agreement (CLA)

By submitting a pull request or contribution to this project, you agree to the following terms:

**Summary of CLA Terms:**

1. **Original Work**: Your contribution is your original work, or you have obtained all necessary rights and permissions to submit it
1. **License Grant**: You agree to license your contribution under the Apache License 2.0, the same license as the project
1. **Patent Grant**: You grant a perpetual, worldwide, royalty-free patent license as specified in the Apache License 2.0 for any patents you own that are necessarily infringed by your contribution
1. **No Conflicts**: Your contribution does not violate any third-party intellectual property rights, contractual obligations, or other legal restrictions
1. **Compliance Awareness**: You understand this project handles Protected Health Information (PHI) and have reviewed the <SECURITY.md> guidelines
1. **Right to Grant**: You have the legal authority to enter into this agreement and grant these licenses

**Corporate Contributions:**

If you are contributing on behalf of your employer or another entity:

- You must have authorization to submit contributions on their behalf
- You represent that your employer has waived any rights to the work you contribute
- Please contact the maintainers if your organization requires a separate corporate CLA

**What the CLA Covers:**

- All code, documentation, and other materials you contribute
- All modifications and additions to existing project files
- All new files you create as part of your contributions

### Third-Party Code

If your contribution includes third-party code or dependencies:

- Ensure compatibility with Apache 2.0 license
- Document the source, license, and any required attribution
- Update dependency documentation as needed
- For healthcare dependencies, verify HIPAA compliance considerations

### Security and Compliance

Contributors working on HIPAA-related features must:

- Review <SECURITY.md> for compliance requirements
- Never commit PHI or test data containing real patient information
- Follow secure coding practices and encryption requirements
- Report security vulnerabilities responsibly (see SECURITY.md)

-----

Thank you for contributing to the Cloud Health Office platform! Your efforts help health plans achieve regulatory compliance, reduce vendor dependencies, and modernize their integration infrastructure.
