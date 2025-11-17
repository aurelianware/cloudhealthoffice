# Contributing to HIPAA Attachments

Welcome to the HIPAA Attachments repository! This guide will help you get started with development and contributions.

## Table of Contents
- [License and Contribution Agreement](#license-and-contribution-agreement)
- [Getting Started](#getting-started)
- [Prerequisites](#prerequisites)
- [Development Setup](#development-setup)
- [Development Workflow](#development-workflow)
- [Validation and Testing](#validation-and-testing)
- [Code Review Standards](#code-review-standards)
- [Troubleshooting](#troubleshooting)
- [Best Practices](#best-practices)

## License and Contribution Agreement

This project is licensed under the **Apache License 2.0**. By contributing to this project, you agree that your contributions will be licensed under the same license.

### Key Points About Contributing

- **License Grant**: All contributions are subject to the Apache License 2.0
- **Patent Grant**: Contributors grant a patent license for their contributions as defined in the Apache License 2.0
- **Copyright**: Contributors retain copyright to their contributions while granting the project rights under Apache 2.0
- **HIPAA Compliance**: Contributors should be aware this project handles PHI; review [SECURITY.md](SECURITY.md) for compliance guidelines

For the full license text, see [LICENSE](LICENSE).

### Why Apache 2.0?

We chose Apache 2.0 because it:
- Is widely accepted in healthcare and enterprise environments
- Provides patent protection for contributors and users
- Supports commercial use in HIPAA-regulated environments
- Has clear, well-understood terms for contributions

## Getting Started

This repository implements an Azure Logic Apps solution for processing HIPAA-compliant medical attachments with secure X12 EDI processing, Service Bus messaging, and Data Lake storage.

### Quick Links
- 📖 **[Architecture Documentation](ARCHITECTURE.md)** - System design and data flows
- 🚀 **[Deployment Guide](DEPLOYMENT.md)** - Step-by-step deployment procedures
- 🔧 **[Troubleshooting Guide](TROUBLESHOOTING.md)** - Common issues and solutions
- 🔒 **[Security Guide](SECURITY.md)** - HIPAA compliance and secure development

## Prerequisites

Before you begin, ensure you have the following tools installed:

### Required Tools

| Tool | Minimum Version | Purpose | Installation |
|------|----------------|---------|--------------|
| **Azure CLI** | 2.77.0+ | Azure resource management | [Install Guide](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli) |
| **Bicep CLI** | 0.37.0+ | Infrastructure as Code | `az bicep install` |
| **PowerShell** | 7.4+ | Deployment scripts | [Install Guide](https://docs.microsoft.com/en-us/powershell/scripting/install/installing-powershell) |
| **jq** | 1.7+ | JSON validation and processing | [Install Guide](https://stedolan.github.io/jq/download/) |
| **Git** | 2.x+ | Version control | [Install Guide](https://git-scm.com/downloads) |

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
```

### Azure Access Requirements

- Azure subscription with appropriate permissions
- Access to target resource groups (DEV/UAT/PROD)
- Azure Active Directory authentication configured
- OIDC federated credentials for GitHub Actions (for CI/CD)

## Development Setup

### 1. Clone the Repository

```bash
git clone https://github.com/aurelianware/hipaa-attachments.git
cd hipaa-attachments
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

We follow a structured branching strategy:

```
main                    # Production-ready code
├── release/*          # UAT deployments (auto-deploys)
└── feature/*          # Feature development
    └── bugfix/*       # Bug fixes
```

**Branch Naming Conventions:**
- Features: `feature/description-of-feature`
- Bug fixes: `bugfix/issue-description`
- Releases: `release/v1.0.0`

### Making Changes

#### 1. Create a Feature Branch

```bash
# Create and checkout a new feature branch
git checkout -b feature/your-feature-name

# For bug fixes
git checkout -b bugfix/issue-description
```

#### 2. Make Your Changes

Follow these guidelines based on what you're changing:

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
  echo "✓ Valid: $f"
done

# Validate Bicep templates
az bicep build --file infra/main.bicep --outfile /tmp/arm.json
echo "✓ Bicep validation passed"

# Validate PowerShell scripts
for script in *.ps1; do
  pwsh -Command "Get-Content './$script' | Out-Null" && echo "✓ Valid: $script"
done
```

#### 4. Test Your Changes

**Local Testing (when applicable):**

```bash
# Test workflow packaging
cd logicapps && zip -r ../workflows.zip workflows/
unzip -l ../workflows.zip  # Verify structure

# Test PowerShell scripts with -WhatIf
pwsh -c "./deploy-workflows.ps1 -WhatIf"

# Run workflow tests (requires Azure access)
pwsh -c "./test-workflows.ps1 -TestInbound275"
```

#### 5. Commit Your Changes

Use clear, descriptive commit messages:

```bash
git add .
git commit -m "feat: Add replay endpoint for 278 transactions

- Implemented HTTP trigger for deterministic replay
- Added blobUrl validation
- Integrated with edi-278 Service Bus topic
- Updated documentation"
```

**Commit Message Format:**
- `feat:` - New features
- `fix:` - Bug fixes
- `docs:` - Documentation changes
- `refactor:` - Code refactoring
- `test:` - Test additions or modifications
- `chore:` - Maintenance tasks

#### 6. Push and Create Pull Request

```bash
# Push your branch
git push origin feature/your-feature-name

# Create a pull request on GitHub
# Fill in the PR template with:
# - Description of changes
# - Testing performed
# - Related issues
```

### Pull Request Process

1. **Create PR**: Open a pull request against the `main` branch
2. **Automated Checks**: CI/CD will run:
   - JSON workflow validation
   - Bicep compilation
   - YAML linting (actionlint, yamllint)
   - PowerShell syntax validation
3. **Code Review**: At least one approval required
4. **Address Feedback**: Make requested changes and push updates
5. **Merge**: Once approved and checks pass, PR will be merged

## Validation and Testing

### Automated CI/CD Checks

All pull requests automatically run these validations:

#### 1. JSON Workflow Validation
Validates all `workflow.json` files for:
- Valid JSON syntax
- Required keys: `definition`, `kind`, `parameters`
- Stateful/Stateless kind verification

#### 2. Bicep Validation
Compiles all `.bicep` files to verify:
- Syntax correctness
- Parameter validation
- Resource configuration

**Expected Warnings:**
- "use-parent-property" for Service Bus topics - **SAFE TO IGNORE**

#### 3. YAML Linting
Validates GitHub Actions workflows:
- Syntax correctness (actionlint)
- YAML formatting (yamllint)
- Best practices

#### 4. PowerShell Validation
Checks all `.ps1` files for syntax errors

### Local Validation Commands

Run these before pushing to catch issues early:

```bash
# Complete validation suite
cd /home/runner/work/hipaa-attachments/hipaa-attachments

# 1. JSON validation
echo "=== Validating JSON workflows ==="
find logicapps/workflows -name "workflow.json" -exec jq -e 'has("definition") and has("kind") and has("parameters")' {} \;

# 2. Bicep validation
echo "=== Validating Bicep templates ==="
az bicep build --file infra/main.bicep --outfile /tmp/arm.json

# 3. PowerShell validation
echo "=== Validating PowerShell scripts ==="
pwsh -Command "Get-Content './test-workflows.ps1' | Out-Null"
pwsh -Command "Get-Content './fix_repo_structure.ps1' | Out-Null"

echo "✅ All validations passed"
```

### Testing Workflows

Use the provided test script to validate Logic Apps workflows:

```powershell
# Test 275 attachment ingestion
pwsh -c "./test-workflows.ps1 -TestInbound275"

# Test 277 RFAI generation
pwsh -c "./test-workflows.ps1 -TestOutbound277"

# Test 278 processing
pwsh -c "./test-workflows.ps1 -TestInbound278"

# Full end-to-end test
pwsh -c "./test-workflows.ps1 -TestFullWorkflow"
```

**Test Files Available:**
- `test-x12-275-availity-to-pchp.edi` - Sample 275 EDI file
- `test-qnxt-response-payload.json` - Sample QNXT API response
- `test-plan-trading-partners.md` - Detailed test plan

### Pre-Commit Checklist

Before committing, ensure:

- [ ] All JSON files are valid (run `jq` validation)
- [ ] Bicep templates compile without errors
- [ ] PowerShell scripts have no syntax errors
- [ ] Changes follow repository conventions
- [ ] Documentation updated if behavior changed
- [ ] Tests pass locally (if applicable)
- [ ] Commit message is clear and descriptive
- [ ] No secrets or credentials in code

## Code Review Standards

### What Reviewers Look For

1. **Code Quality**
   - Follows established patterns and conventions
   - Minimal changes to achieve goal
   - No unnecessary refactoring

2. **Security**
   - No hardcoded credentials or secrets
   - Proper use of managed identities
   - HIPAA compliance maintained
   - Encryption requirements met

3. **Functionality**
   - Changes accomplish stated goal
   - Edge cases handled
   - Error handling appropriate
   - Retry logic for transient failures

4. **Testing**
   - Validation commands pass
   - Appropriate testing performed
   - Test results documented

5. **Documentation**
   - Code is self-documenting or has comments
   - README/docs updated if needed
   - Breaking changes clearly noted

### Review Checklist for Reviewers

**Logic Apps Changes:**
- [ ] Workflow JSON is valid
- [ ] All workflows are `"kind": "Stateful"`
- [ ] Required keys present (`definition`, `kind`, `parameters`)
- [ ] Actions have meaningful names
- [ ] Error handling included
- [ ] Retry policies appropriate

**Infrastructure Changes:**
- [ ] Bicep compiles without errors
- [ ] Parameters have descriptions
- [ ] Resource naming follows conventions
- [ ] HIPAA compliance maintained
- [ ] No breaking changes to existing resources

**PowerShell Script Changes:**
- [ ] Syntax is valid
- [ ] Uses approved verbs
- [ ] Includes comment-based help
- [ ] Error handling implemented
- [ ] Parameters properly defined

**GitHub Actions Changes:**
- [ ] YAML syntax valid
- [ ] Boolean values use `true`/`false`
- [ ] Timeouts appropriate for tasks
- [ ] OIDC authentication preserved
- [ ] Environment secrets properly referenced

### Providing Feedback

**Constructive Feedback:**
- Be specific about what needs to change
- Explain why the change is needed
- Suggest alternative approaches when helpful
- Acknowledge good solutions

**Examples:**
- ✅ "Consider adding retry logic here for transient failures (QNXT API can timeout)"
- ✅ "This workflow should be Stateful instead of Stateless for Logic Apps Standard"
- ❌ "This is wrong" (not specific enough)
- ❌ "Rewrite everything" (not constructive)

## Troubleshooting

### Common Development Issues

#### JSON Validation Errors

**Issue:** `jq: parse error: Expected separator between values`

**Solution:**
```bash
# Find the syntax error
jq . logicapps/workflows/ingest275/workflow.json

# Common causes:
# - Missing comma between elements
# - Trailing comma before closing }
# - Unescaped quotes in strings
# - Mismatched brackets
```

#### Bicep Compilation Errors

**Issue:** Parameter validation errors

**Solution:**
```bash
# Check parameter definitions
az bicep build --file infra/main.bicep --outfile /tmp/arm.json

# Review output for specific parameter issues
# Common causes:
# - Missing required parameters
# - Invalid parameter types
# - Constraint violations
```

**Expected Warnings:**
```
Warning use-parent-property: Use a reference to the parent resource instead of repeating name/type
```
These warnings about Service Bus topics are **SAFE TO IGNORE**.

#### PowerShell Execution Errors

**Issue:** Script won't execute

**Solution:**
```powershell
# Check execution policy
Get-ExecutionPolicy

# Set for current session
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

# Verify syntax
pwsh -Command "Get-Content './script.ps1' | Out-Null"
```

#### Workflow Package Issues

**Issue:** ZIP structure incorrect

**Solution:**
```bash
# Correct structure: workflows/ as top-level directory
cd logicapps
zip -r ../workflows.zip workflows/

# Verify structure
unzip -l ../workflows.zip

# Should show:
# workflows/ingest275/workflow.json
# workflows/ingest278/workflow.json
# workflows/replay278/workflow.json
# workflows/rfai277/workflow.json
```

#### Git Issues

**Issue:** Merge conflicts

**Solution:**
```bash
# Update your branch with latest main
git fetch origin
git merge origin/main

# Resolve conflicts manually
# Then mark as resolved
git add .
git commit -m "Merge main and resolve conflicts"
```

For more troubleshooting guidance, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

## Best Practices

### General Guidelines

1. **Minimal Changes**: Make the smallest change necessary to achieve your goal
2. **Test Early**: Validate and test as soon as possible after making changes
3. **Document Clearly**: Update docs when changing behavior
4. **Security First**: Never commit secrets or credentials
5. **HIPAA Aware**: Maintain compliance with all changes

### Workflow Development

- Use meaningful action names that describe their purpose
- Include error handling for all external calls
- Add retry policies for transient failures (QNXT API, SFTP)
- Use Service Bus for async/decoupled processing
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
- Store secrets in Azure Key Vault
- Accept secrets as `[SecureString]` parameters
- Enable audit logging for all operations
- Follow principle of least privilege
- Review [SECURITY.md](SECURITY.md) for detailed guidelines

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
- [Troubleshooting](TROUBLESHOOTING.md) - Common issues and solutions
- [Security Guide](SECURITY.md) - HIPAA compliance and security

### External Resources
- [Azure Logic Apps Documentation](https://docs.microsoft.com/en-us/azure/logic-apps/)
- [Bicep Documentation](https://docs.microsoft.com/en-us/azure/azure-resource-manager/bicep/)
- [X12 EDI Standards](https://x12.org/)
- [HIPAA Compliance Guide](https://www.hhs.gov/hipaa/index.html)

### GitHub Copilot Instructions
This repository includes comprehensive instructions for GitHub Copilot:
- [Repository-wide instructions](.github/copilot-instructions.md)
- [Workflow-specific guidance](.github/instructions/)

## Getting Help

If you encounter issues:

1. Check [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for common issues
2. Review existing [GitHub Issues](https://github.com/aurelianware/hipaa-attachments/issues)
3. Check Application Insights for runtime errors
4. Create a new issue with detailed information:
   - Steps to reproduce
   - Expected vs actual behavior
   - Error messages and logs
   - Environment details

## Questions?

For questions or clarifications:
- Open a [GitHub Discussion](https://github.com/aurelianware/hipaa-attachments/discussions)
- Review existing documentation
- Check GitHub Copilot instructions for guidance

---

## Legal and Licensing

### Contributor License Agreement

By submitting a pull request or contribution to this project, you certify that:

1. **Original Work**: Your contribution is your original work or you have rights to submit it
2. **License Agreement**: You agree to license your contribution under the Apache License 2.0
3. **Patent Grant**: You grant a patent license as specified in the Apache License 2.0
4. **No Conflicts**: Your contribution does not violate any third-party rights or licenses
5. **Compliance Awareness**: You understand this project handles PHI and have reviewed security guidelines

### Third-Party Code

If your contribution includes third-party code or dependencies:
- Ensure compatibility with Apache 2.0 license
- Document the source, license, and any required attribution
- Update dependency documentation as needed
- For healthcare dependencies, verify HIPAA compliance considerations

### Security and Compliance

Contributors working on HIPAA-related features must:
- Review [SECURITY.md](SECURITY.md) for compliance requirements
- Never commit PHI or test data containing real patient information
- Follow secure coding practices and encryption requirements
- Report security vulnerabilities responsibly (see SECURITY.md)

---

Thank you for contributing to the HIPAA Attachments project! Your efforts help maintain a secure, compliant, and efficient medical attachment processing system.
