# Branching Strategy

This document defines the branching strategy, naming conventions, merge requirements, and commit policies for the Cloud Health Office repository. Our branching model is designed to support continuous integration, automated deployments, and semantic versioning while maintaining code quality and HIPAA compliance.

## Table of Contents
- [Overview](#overview)
- [Branch Structure](#branch-structure)
- [Branch Naming Conventions](#branch-naming-conventions)
- [Branch Protection Rules](#branch-protection-rules)
- [Merge Requirements](#merge-requirements)
- [Commit Message Conventions](#commit-message-conventions)
- [Workflows](#workflows)
  - [Feature Development](#feature-development)
  - [Release Cycles](#release-cycles)
  - [Hotfix Procedures](#hotfix-procedures)
- [Semantic Versioning](#semantic-versioning)
- [CI/CD Integration](#cicd-integration)
- [Best Practices](#best-practices)

## Overview

The Cloud Health Office repository follows a **trunk-based development** approach with feature branches and release branches. This strategy enables:

- **Continuous Integration**: Frequent merging to main branch with automated validation
- **Environment-Specific Deployments**: Automated deployments to DEV, UAT, and PROD
- **Code Quality**: Mandatory code reviews and automated checks before merging
- **Compliance**: Audit trail for all changes to HIPAA-compliant code
- **Semantic Versioning**: Clear version progression aligned with branch workflow

## Branch Structure

```
main (protected)                    # Production-ready code, deploys to PROD manually
├── release/* (auto-deploys UAT)    # Release candidates, auto-deploys to UAT
├── feature/*                       # New features and enhancements
├── bugfix/*                        # Bug fixes for non-production issues
└── hotfix/*                        # Critical production fixes
```

### Branch Purposes

| Branch Type | Purpose | Deployment | Lifetime |
|-------------|---------|------------|----------|
| `main` | Production-ready code, stable | Manual to PROD | Permanent |
| `release/*` | Release candidates for UAT testing | Auto to UAT | Until merged to main |
| `feature/*` | New features and enhancements | Manual to DEV (optional) | Until merged to main |
| `bugfix/*` | Bug fixes for non-production issues | Manual to DEV (optional) | Until merged to main |
| `hotfix/*` | Critical production fixes | Manual to PROD | Until merged to main |

## Branch Naming Conventions

### Main Branch
```
main
```
- **Protected**: Yes
- **Purpose**: Production-ready code only
- **Deployment**: Manual workflow dispatch to PROD
- **Merges From**: `release/*`, `hotfix/*`

### Release Branches
```
release/v{MAJOR}.{MINOR}.{PATCH}
release/v{MAJOR}.{MINOR}.{PATCH}-{PRERELEASE}
```

**Examples:**
- `release/v1.0.0` - Major release
- `release/v1.1.0` - Minor release with new features
- `release/v1.1.1` - Patch release with bug fixes
- `release/v2.0.0-beta.1` - Pre-release version

**Naming Rules:**
- Must start with `release/v`
- Follow semantic versioning: `MAJOR.MINOR.PATCH`
- Optional pre-release suffix: `-alpha.N`, `-beta.N`, `-rc.N`
- All lowercase
- Auto-deploys to UAT on push

### Feature Branches
```
feature/{issue-number}-{short-description}
feature/{short-description}
```

**Examples:**
- `feature/42-add-278-replay-endpoint`
- `feature/authorization-workflow`
- `feature/appeals-processing`

**Naming Rules:**
- Must start with `feature/`
- Use kebab-case (lowercase with hyphens)
- Include issue/ticket number when applicable
- Keep description concise but meaningful

### Bugfix Branches
```
bugfix/{issue-number}-{short-description}
bugfix/{short-description}
```

**Examples:**
- `bugfix/123-fix-x12-decoding-error`
- `bugfix/service-bus-retry-logic`

**Naming Rules:**
- Must start with `bugfix/`
- Use kebab-case
- Include issue/ticket number when applicable
- Describe the bug being fixed

### Hotfix Branches
```
hotfix/v{MAJOR}.{MINOR}.{PATCH+1}-{short-description}
```

**Examples:**
- `hotfix/v1.2.1-qnxt-timeout`
- `hotfix/v2.0.1-sftp-connection-failure`

**Naming Rules:**
- Must start with `hotfix/v`
- Include target version number (incremented patch)
- Use kebab-case for description
- Reserved for critical production issues only

## Branch Protection Rules

### Main Branch Protection

The `main` branch has the following protection rules enforced:

#### Required Pull Request Reviews
- **Minimum approvals**: 1
- **Dismiss stale reviews**: Yes - when new commits are pushed
- **Require review from Code Owners**: No (but recommended if CODEOWNERS file exists)
- **Restrict who can dismiss reviews**: Administrators only

#### Required Status Checks
All checks must pass before merging:
- ✅ **JSON Workflow Validation**: Validates all `workflow.json` files
- ✅ **Bicep Compilation**: Ensures infrastructure templates compile
- ✅ **YAML Linting** (actionlint): Validates GitHub Actions workflows
- ✅ **YAML Linting** (yamllint): Validates YAML formatting
- ✅ **PowerShell Validation**: Checks script syntax

#### Additional Protections
- **Require branches to be up to date**: Yes - must merge latest main first
- **Require conversation resolution**: Yes - all review comments must be resolved
- **Enforce for administrators**: Yes - no exceptions
- **Restrict force pushes**: Yes - no force pushes allowed
- **Allow deletions**: No - branch cannot be deleted

### Release Branch Protection

`release/*` branches have moderate protection:

- **Required Pull Request Reviews**: Optional (recommended: 1 approval)
- **Required Status Checks**: Same as main branch
- **Restrict force pushes**: Yes
- **Auto-deploy**: Yes to UAT environment

### Feature/Bugfix Branch Protection

Feature and bugfix branches have minimal restrictions:

- **Pull Request Reviews**: Recommended but not required
- **Status Checks**: Run but not required for pushing
- **Force Pushes**: Allowed (for branch author only)

## Merge Requirements

### Merging to Main

**Requirements:**
1. ✅ Pull request from `release/*` or `hotfix/*` branch
2. ✅ At least 1 approval from team member
3. ✅ All status checks passing
4. ✅ All conversations resolved
5. ✅ Branch is up to date with main
6. ✅ Code review completed with focus on:
   - Security (no credentials, proper encryption)
   - HIPAA compliance
   - Logic Apps workflow validity
   - Infrastructure changes reviewed
   - Documentation updated

**Merge Strategy**: **Squash and Merge** (preferred) or **Create a Merge Commit**
- Squash: For feature branches with many commits
- Merge commit: For release branches to preserve history

**After Merge:**
- Delete source branch (for feature/bugfix)
- Tag main with version number (for releases)
- Manual deployment to PROD (via workflow dispatch)

### Merging to Release

**Requirements:**
1. ✅ Pull request from `feature/*` or `bugfix/*` branch
2. ✅ At least 1 approval recommended
3. ✅ All status checks passing
4. ✅ Testing completed in DEV (if applicable)

**Merge Strategy**: **Create a Merge Commit** or **Squash and Merge**

**After Merge:**
- Auto-deploys to UAT
- Monitor UAT deployment and testing
- Verify workflows in UAT environment

### Merging Features to Main (Alternative)

Features can merge directly to main if:
- No active release branch exists
- Change is small and low-risk
- All standard merge requirements met

## Commit Message Conventions

We follow **Conventional Commits** aligned with semantic versioning.

### Format

```
<type>(<scope>): <subject>

[optional body]

[optional footer]
```

### Types

| Type | Description | Semantic Version Impact | Examples |
|------|-------------|------------------------|----------|
| `feat` | New feature | MINOR bump | `feat(workflows): Add 278 replay endpoint` |
| `fix` | Bug fix | PATCH bump | `fix(auth): Correct QNXT API timeout handling` |
| `docs` | Documentation only | None | `docs: Update deployment guide` |
| `refactor` | Code refactoring (no behavior change) | None | `refactor(bicep): Simplify parameter definitions` |
| `test` | Add or modify tests | None | `test(workflows): Add 275 ingestion test` |
| `chore` | Maintenance tasks | None | `chore: Update dependencies` |
| `perf` | Performance improvement | PATCH bump | `perf(appeals): Optimize QNXT API calls` |
| `style` | Code style changes (formatting) | None | `style(powershell): Apply consistent formatting` |
| `ci` | CI/CD changes | None | `ci: Update GitHub Actions workflows` |
| `build` | Build system changes | None | `build: Update Bicep CLI version` |
| `revert` | Revert previous commit | Depends | `revert: feat(workflows): Add 278 replay` |

### Breaking Changes

For breaking changes, add `BREAKING CHANGE:` in the footer or append `!` after type:

```
feat(api)!: Change QNXT API request format

BREAKING CHANGE: QNXT API now requires authentication token in header instead of query parameter.
Update all QNXT API calls accordingly.
```

**Impact**: MAJOR version bump

### Scope

Use scope to specify the component affected:

- `workflows` - Logic Apps workflows
- `infra` - Bicep infrastructure
- `ci` - GitHub Actions
- `scripts` - PowerShell scripts
- `auth` - Authorization processing
- `appeals` - Appeals processing
- `x12` - X12 EDI processing
- `sftp` - SFTP operations
- `storage` - Data Lake operations
- `servicebus` - Service Bus operations

### Examples

**Feature:**
```
feat(workflows): Add replay endpoint for 278 transactions

- Implemented HTTP trigger for deterministic replay
- Added blobUrl validation
- Integrated with edi-278 Service Bus topic
- Updated documentation
```

**Bug Fix:**
```
fix(auth): Handle QNXT API timeouts with retry logic

Added 4 retries with 15-second intervals for QNXT authorization
API calls to handle transient network failures.

Fixes #127
```

**Breaking Change:**
```
feat(infra)!: Migrate to hierarchical namespace for Data Lake

BREAKING CHANGE: Storage account now requires hierarchical namespace
enabled. Existing deployments must migrate data before updating.

Migration guide: docs/storage-migration.md
```

**Documentation:**
```
docs: Document branching strategy and protection rules

- Created BRANCHING-STRATEGY.md
- Added branch naming conventions
- Documented merge requirements
- Updated CONTRIBUTING.md references
```

## Workflows

### Feature Development

Standard workflow for developing new features:

```bash
# 1. Create feature branch from main
git checkout main
git pull origin main
git checkout -b feature/42-new-workflow

# 2. Make changes and commit frequently
git add .
git commit -m "feat(workflows): Add initial replay endpoint structure"

# 3. Push to remote
git push origin feature/42-new-workflow

# 4. Continue development with regular commits
git commit -m "feat(workflows): Implement blobUrl validation"
git commit -m "test(workflows): Add replay endpoint tests"
git push origin feature/42-new-workflow

# 5. Update branch with latest main
git fetch origin
git merge origin/main
# Resolve conflicts if any

# 6. Create pull request
# - Open PR on GitHub targeting main
# - Fill in PR template
# - Request reviews

# 7. Address review feedback
git commit -m "fix(workflows): Address code review feedback"
git push origin feature/42-new-workflow

# 8. After approval and passing checks, merge via GitHub UI
# - Squash and merge (preferred for features)
# - Delete branch after merge
```

### Release Cycles

Process for creating and deploying releases:

#### 1. Create Release Branch

```bash
# Determine version based on changes since last release
# - MAJOR: Breaking changes
# - MINOR: New features (backward compatible)
# - PATCH: Bug fixes only

# Create release branch
git checkout main
git pull origin main
git checkout -b release/v1.2.0

# Update version references if needed
# - Update documentation
# - Update CHANGELOG.md

git commit -m "chore: Prepare v1.2.0 release"
git push origin release/v1.2.0
```

#### 2. UAT Deployment (Automatic)

```
Push to release/v1.2.0 branch triggers automatic UAT deployment:
- Validates JSON, Bicep, YAML, PowerShell
- Deploys infrastructure via Bicep
- Deploys Logic App workflows
- Restarts Logic App
```

#### 3. UAT Testing

```bash
# Monitor UAT deployment
# - Check GitHub Actions for deployment status
# - Verify infrastructure in Azure Portal
# - Test workflows in UAT environment

# If issues found, fix in release branch
git checkout release/v1.2.0
# Make fixes
git commit -m "fix(workflows): Correct UAT deployment issue"
git push origin release/v1.2.0
# Re-deploys to UAT automatically

# If major issues, merge fixes back to main
git checkout main
git merge release/v1.2.0
git push origin main
```

#### 4. Merge to Main

```bash
# After successful UAT testing:
# 1. Create PR from release/v1.2.0 to main
# 2. Get approval from team member
# 3. Ensure all checks pass
# 4. Merge using "Create a merge commit" (preserves release history)

# After merge, tag the release
git checkout main
git pull origin main
git tag -a v1.2.0 -m "Release v1.2.0"
git push origin v1.2.0
```

#### 5. Production Deployment

```bash
# Manual deployment to PROD via GitHub Actions:
# 1. Go to Actions > Deploy PROD
# 2. Click "Run workflow"
# 3. Confirm deployment parameters
# 4. Monitor deployment
# 5. Verify production environment
```

#### 6. Post-Release

```bash
# Delete release branch
git branch -d release/v1.2.0
git push origin --delete release/v1.2.0

# Create GitHub Release
# - Go to Releases > Draft a new release
# - Tag: v1.2.0
# - Release title: "v1.2.0"
# - Description: List of changes from CHANGELOG.md
# - Publish release
```

### Hotfix Procedures

Process for critical production fixes:

#### 1. Create Hotfix Branch

```bash
# Identify current production version (e.g., v1.2.0)
# New hotfix version will be v1.2.1

git checkout main
git pull origin main
git checkout -b hotfix/v1.2.1-qnxt-timeout

# Make minimal fix
# - Focus on critical issue only
# - Avoid additional changes
# - Add tests if possible

git commit -m "fix(auth): Handle QNXT API timeout with retry logic

Critical fix for production QNXT API timeouts affecting
authorization processing. Added 4 retries with 15-second
intervals.

Fixes #234"
```

#### 2. Test Hotfix

```bash
# Option A: Deploy to UAT for verification
git push origin hotfix/v1.2.1-qnxt-timeout
# Create PR to temporary test branch if needed

# Option B: Test in isolated environment
# - Deploy to separate test resource group
# - Verify fix resolves issue
# - Ensure no regression
```

#### 3. Merge to Main

```bash
# Create PR from hotfix branch to main
# - Requires 1 approval (can be expedited)
# - All checks must pass
# - Mark as urgent/hotfix

# After merge
git checkout main
git pull origin main
git tag -a v1.2.1 -m "Hotfix v1.2.1: QNXT timeout fix"
git push origin v1.2.1
```

#### 4. Deploy to Production

```bash
# Immediate PROD deployment:
# 1. Go to Actions > Deploy PROD
# 2. Run workflow targeting main branch
# 3. Monitor closely
# 4. Verify fix in production
```

#### 5. Backport to Release Branch (if applicable)

```bash
# If active release branch exists, backport fix
git checkout release/v1.3.0  # Current release in progress
git cherry-pick <hotfix-commit-sha>
git push origin release/v1.3.0
# Deploys to UAT automatically
```

## Semantic Versioning

We follow [Semantic Versioning 2.0.0](https://semver.org/) for release versions.

### Version Format

```
MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]
```

**Examples:**
- `1.0.0` - Initial stable release
- `1.1.0` - Minor release with new features
- `1.1.1` - Patch release with bug fixes
- `2.0.0` - Major release with breaking changes
- `1.2.0-beta.1` - Pre-release beta version
- `1.2.0-rc.1+20240115` - Release candidate with build metadata

### Version Increment Rules

#### MAJOR Version (X.0.0)
Increment when making **incompatible API changes** or **breaking changes**:
- Changed Integration Account schema requirements
- Modified Service Bus topic structure
- Changed QNXT API integration contract
- Removed or renamed workflow parameters
- Changed Data Lake folder structure

**Example Commit:**
```
feat(infra)!: Migrate to new Service Bus topics

BREAKING CHANGE: Service Bus topics renamed to follow
new naming convention. Update all workflow subscriptions.
```

#### MINOR Version (0.X.0)
Increment when adding **functionality in a backward-compatible manner**:
- New Logic App workflow added
- New API endpoint or integration
- New optional workflow parameters
- Enhanced monitoring or logging
- New optional features

**Example Commit:**
```
feat(workflows): Add replay endpoint for 278 transactions
```

#### PATCH Version (0.0.X)
Increment when making **backward-compatible bug fixes**:
- Bug fixes in workflows
- Performance improvements
- Security patches (non-breaking)
- Documentation updates
- Dependency updates

**Example Commit:**
```
fix(auth): Correct QNXT API timeout handling
```

### Pre-release Versions

Use pre-release identifiers for testing:

- `alpha` - Early development, unstable
- `beta` - Feature complete, testing in progress
- `rc` - Release candidate, final testing

**Examples:**
```
1.2.0-alpha.1  # First alpha of 1.2.0
1.2.0-beta.1   # First beta of 1.2.0
1.2.0-rc.1     # First release candidate
1.2.0          # Final release
```

### Version Decision Tree

```
Does the change break existing functionality?
├─ YES → MAJOR version bump (X.0.0)
└─ NO  → Does the change add new functionality?
         ├─ YES → MINOR version bump (0.X.0)
         └─ NO  → PATCH version bump (0.0.X)
```

## CI/CD Integration

### Automated Workflows

| Branch Pattern | Workflow | Environment | Trigger |
|---------------|----------|-------------|---------|
| `main` | `deploy.yml` | PROD | Manual only |
| `release/*` | `deploy-uat.yml` | UAT | Auto on push |
| `feature/*`, `bugfix/*` | `pr-lint.yml` | N/A | Auto on PR |
| Any branch | `pr-lint.yml` | N/A | Auto on PR to main |

### Pull Request Checks

All PRs automatically run validation checks:

#### pr-lint.yml Workflow
- ✅ **JSON Validation**: All `workflow.json` files
  - Valid JSON syntax
  - Required keys: `definition`, `kind`, `parameters`
  - Stateful/Stateless verification
  
- ✅ **Bicep Compilation**: All `.bicep` files
  - Syntax validation
  - Parameter validation
  - Expected warnings (Service Bus topics) are ignored
  
- ✅ **GitHub Actions Linting**: `.github/workflows/*.yml`
  - actionlint for workflow validation
  - yamllint for YAML formatting
  
- ✅ **PowerShell Validation**: All `.ps1` files
  - Syntax checking
  - Load validation

**These checks must pass before merge to main.**

### Environment Deployments

#### DEV Environment
- **Branch**: Any (via manual workflow dispatch)
- **Purpose**: Developer testing
- **Deployment**: Manual via `deploy-dev.yml`
- **Frequency**: As needed

#### UAT Environment
- **Branch**: `release/*`
- **Purpose**: Release candidate testing
- **Deployment**: Automatic on push to release branch
- **Frequency**: With each release candidate
- **Workflow**: `deploy-uat.yml`

#### PROD Environment
- **Branch**: `main`
- **Purpose**: Production deployment
- **Deployment**: Manual via `deploy.yml`
- **Frequency**: After successful UAT testing
- **Approval**: Required via GitHub environment protection

### Deployment Stages

Each deployment includes:
1. **Validation**: JSON, Bicep, YAML, PowerShell checks
2. **Infrastructure**: ARM What-If analysis, Bicep deployment
3. **Application**: Logic App workflows package and deploy
4. **Verification**: Health checks and status validation
5. **Notification**: Deployment summary and status

## Best Practices

### Branch Management

✅ **DO:**
- Keep branches short-lived (< 2 weeks)
- Regularly sync with main branch
- Delete branches after merging
- Use descriptive branch names
- Commit frequently with clear messages
- Push daily to enable collaboration
- Create PR early for visibility
- Request reviews from team members

❌ **DON'T:**
- Commit directly to main
- Force push to protected branches
- Create long-lived feature branches
- Leave stale branches unmerged
- Merge without PR and review
- Push large binary files
- Commit secrets or credentials
- Merge failing status checks

### Pull Request Best Practices

**Creating PRs:**
- Write clear title and description
- Reference related issues (`Fixes #123`)
- Include testing evidence
- Document breaking changes
- Keep PRs focused and small
- Update documentation

**Reviewing PRs:**
- Review within 24 hours
- Test changes locally if possible
- Check for security issues
- Verify HIPAA compliance
- Validate infrastructure changes
- Ensure documentation updated
- Be constructive and specific

### Commit Best Practices

**Commit Frequency:**
- Commit after each logical unit of work
- Commit before switching tasks
- Commit before pulling updates
- Don't commit partial broken code

**Commit Content:**
- Each commit should be self-contained
- Include related test changes
- Update documentation in same commit
- One logical change per commit

**Commit Messages:**
- Follow conventional commits format
- Be clear and descriptive
- Explain "why" not just "what"
- Reference issues and PRs
- Use present tense ("Add" not "Added")
- Keep subject line under 72 characters

### Security Best Practices

🔒 **Security Rules:**
- Never commit secrets or credentials
- Use Azure Key Vault for sensitive values
- Review security alerts immediately
- Follow least privilege principle
- Enable branch protection rules
- Require signed commits (optional)
- Enable 2FA for all contributors
- Audit access regularly

### HIPAA Compliance

🏥 **HIPAA Considerations:**
- No PHI in commit messages
- No PHI in branch names
- No PHI in PR descriptions
- Audit trail for all changes
- Secure code review process
- Document compliance decisions
- Regular security reviews

## Summary

This branching strategy supports:

✅ **Quality**: Mandatory code reviews and automated checks
✅ **Security**: Protected branches and audit trails
✅ **Compliance**: HIPAA-aware workflows and documentation
✅ **Automation**: CI/CD integration with environment-specific deployments
✅ **Versioning**: Clear semantic versioning aligned with branch workflow
✅ **Collaboration**: Clear conventions and review processes

**Key Takeaways:**
- `main` is always production-ready
- `release/*` branches auto-deploy to UAT
- All changes require PR and review
- Follow semantic versioning guidelines
- Use conventional commit messages
- Protect sensitive data at all times

---

**Questions or suggestions?** Open a [GitHub Discussion](https://github.com/aurelianware/hipaa-attachments/discussions) or contact the team.

**Related Documentation:**
- [CONTRIBUTING.md](CONTRIBUTING.md) - Development workflow and setup
- [DEPLOYMENT.md](DEPLOYMENT.md) - Deployment procedures
- [SECURITY.md](SECURITY.md) - Security and HIPAA compliance
- [ARCHITECTURE.md](ARCHITECTURE.md) - System architecture
