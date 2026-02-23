# SFTP Test Credentials - Security Guide

## ⚠️ Security Notice

**NEVER commit SFTP passwords to version control!**

The test scripts (`test-sftp-275-278-workflow.sh`, `test-276-277-claim-status.sh`) now require credentials to be set via environment variables.

## Quick Setup

### Option 1: Environment Variables (Recommended for CI/CD)

```bash
export SFTP_USER='logicapp'
export SFTP_PASSWORD='<get-from-kubernetes>'
```

### Option 2: Credentials File (Recommended for Local Development)

Create `~/.sftp-test-env`:

```bash
cat > ~/.sftp-test-env <<'EOF'
export SFTP_USER='logicapp'
export SFTP_PASSWORD='your-password-here'
EOF

chmod 600 ~/.sftp-test-env
```

Then source it before running tests:

```bash
source ~/.sftp-test-env
./test-276-277-claim-status.sh
```

## Getting the Current Password

### From Kubernetes Secret

```bash
# View all SFTP users and passwords
kubectl -n cho-sftp get secret sftp-users -o jsonpath='{.data.users\.conf}' | base64 -d

# Extract just the logicapp password
kubectl -n cho-sftp get secret sftp-users -o jsonpath='{.data.users\.conf}' | \
  base64 -d | grep '^logicapp:' | cut -d: -f2
```

### From Azure Key Vault (if synced)

```bash
az keyvault secret show \
  --vault-name cho-keyvault-prod \
  --name sftp-logicapp-password \
  --query value -o tsv
```

## Rotating Passwords

### Why Rotate?

- Compromised credentials
- Security audit requirements
- Regular security hygiene (every 90 days)
- After team member changes

### How to Rotate

Use the provided rotation script:

```bash
./scripts/rotate-sftp-password.sh logicapp
```

This will:
1. Generate a new strong password (24 characters)
2. Update the Kubernetes secret
3. Restart SFTP pods
4. Display the new password to set in your environment

**After rotation**, update your local environment:

```bash
export SFTP_PASSWORD='<new-password-from-script>'
```

Or update `~/.sftp-test-env` with the new password.

## CI/CD Integration

### GitHub Actions

Add secrets to your repository:

1. Go to **Settings** → **Secrets and variables** → **Actions**
2. Add secrets:
   - `SFTP_USER` = `logicapp`
   - `SFTP_PASSWORD` = `<from-kubernetes>`

Use in workflow:

```yaml
- name: Run 276/277 Tests
  env:
    SFTP_USER: ${{ secrets.SFTP_USER }}
    SFTP_PASSWORD: ${{ secrets.SFTP_PASSWORD }}
  run: |
    ./test-276-277-claim-status.sh
```

### Azure DevOps

Add variables to pipeline:

```yaml
variables:
  - group: sftp-credentials  # Variable group with SFTP_USER and SFTP_PASSWORD

steps:
  - bash: ./test-276-277-claim-status.sh
    displayName: Run 276/277 Tests
    env:
      SFTP_USER: $(SFTP_USER)
      SFTP_PASSWORD: $(SFTP_PASSWORD)
```

## Troubleshooting

### Error: "SECURITY WARNING: SFTP credentials not set!"

**Cause**: Environment variables not set.

**Fix**:
```bash
source ~/.sftp-test-env
# or
export SFTP_USER='logicapp'
export SFTP_PASSWORD='...'
```

### Error: "Permission denied (publickey,password)"

**Causes**:
1. Wrong password
2. Password was rotated but environment not updated
3. SFTP service not ready

**Fix**:
```bash
# Get current password from Kubernetes
kubectl -n cho-sftp get secret sftp-users -o jsonpath='{.data.users\.conf}' | base64 -d

# Update your environment
export SFTP_PASSWORD='<correct-password>'
```

### Error: "Connection refused"

**Cause**: Port-forward not working or SFTP service down.

**Fix**:
```bash
# Check SFTP service
kubectl -n cho-sftp get pods

# Manually port-forward
kubectl -n cho-sftp port-forward svc/sftp-service 12022:22
```

## Best Practices

### ✅ Do

- Store passwords in Kubernetes secrets
- Use environment variables in scripts
- Rotate passwords regularly
- Use strong passwords (24+ characters)
- Limit password access to authorized personnel
- Use `.gitignore` to exclude credentials files

### ❌ Don't

- Commit passwords to git
- Share passwords via email/Slack
- Use weak passwords (e.g., `changeme123`)
- Reuse passwords across environments
- Store passwords in plaintext files tracked by git

## Password Requirements

For SFTP user passwords:

- **Length**: 24+ characters
- **Characters**: Alphanumeric (a-z, A-Z, 0-9)
- **Avoid**: Special characters that might cause shell escaping issues
- **Generation**: Use `openssl rand -base64 24 | tr -d "=+/" | cut -c1-24`

## Compliance

- **HIPAA**: Credentials must be encrypted at rest and in transit
- **PCI DSS**: Password rotation every 90 days
- **SOC 2**: Access logging and credential rotation policies

Kubernetes secrets are encrypted at rest in etcd. All test communications use SSH encryption.

## Emergency Procedures

### Suspected Credential Compromise

1. **Immediately rotate password**:
   ```bash
   ./scripts/rotate-sftp-password.sh logicapp
   ```

2. **Audit access logs**:
   ```bash
   kubectl -n cho-sftp logs -l app=sftp --tail 1000 | grep logicapp
   ```

3. **Update all environments**:
   - CI/CD secrets
   - Developer machines
   - Documentation

4. **Investigate**:
   - Check git history for exposed credentials
   - Review who had access
   - File incident report if needed

## Reference

- **Test Scripts**: `test-276-277-claim-status.sh`, `test-sftp-275-278-workflow.sh`
- **Rotation Script**: `scripts/rotate-sftp-password.sh`
- **K8s Secret**: `cho-sftp/sftp-users`
- **Environment File**: `~/.sftp-test-env` (local, not in git)
