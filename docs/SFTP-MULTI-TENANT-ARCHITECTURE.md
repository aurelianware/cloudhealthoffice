# SFTP Multi-Tenant Architecture

## Overview

Cloud Health Office's SFTP service must provide **complete tenant isolation** to comply with HIPAA and maintain a secure multi-tenant SaaS architecture.

## Current Issues ⚠️

The existing SFTP deployment has:
- ❌ Shared credentials (`logicapp`, `clearinghouse`)
- ❌ No tenant isolation
- ❌ No chroot jails
- ❌ Single namespace for all files
- ❌ **HIPAA compliance risk** - tenants could access each other's PHI

## Required Architecture

### Tenant Directory Structure

```
/sftp-data/
├── tenants/
│   ├── bcbs-florida/           # Tenant ID: bcbs-florida
│   │   ├── inbound/
│   │   │   ├── 276/           # Claim status requests
│   │   │   ├── 278/           # Prior auth requests
│   │   │   ├── 834/           # Enrollment
│   │   │   └── 835/           # ERA
│   │   └── outbound/
│   │       ├── 277/           # Claim status responses
│   │       ├── 278/           # Prior auth responses
│   │       └── 837/           # Claims
│   ├── aetna/
│   │   ├── inbound/
│   │   │   ├── 276/
│   │   │   ├── 278/
│   │   │   └── ...
│   │   └── outbound/
│   │       └── ...
│   ├── cigna/
│   │   └── ...
│   └── anthem/
│       └── ...
└── archive/                    # Long-term storage
    ├── bcbs-florida/
    ├── aetna/
    └── ...
```

### User Configuration Format

Each tenant gets a dedicated SFTP user with chroot to their directory:

```
# Format: username:encrypted_password:uid:gid:directories:shell:chroot_path
bcbs-florida:$6$rounds=5000$salt$hash:1100:1100:inbound,outbound:/bin/false:/tenants/bcbs-florida
aetna:$6$rounds=5000$salt$hash:1101:1101:inbound,outbound:/bin/false:/tenants/aetna
cigna:$6$rounds=5000$salt$hash:1102:1102:inbound,outbound:/bin/false:/tenants/cigna
anthem:$6$rounds=5000$salt$hash:1103:1103:inbound,outbound:/bin/false:/tenants/anthem
```

### Security Requirements

#### 1. chroot Jail
Each user is locked to their home directory and **cannot traverse** to parent directories.

```bash
# User bcbs-florida can only see:
/
├── inbound/
│   ├── 276/
│   ├── 278/
│   └── 834/
└── outbound/
    ├── 277/
    ├── 278/
    └── 837/
```

They **cannot** see:
- `/tenants/aetna/`
- `/tenants/cigna/`
- Parent directory `/tenants/`

#### 2. Unique UIDs/GIDs
Each tenant has unique UID/GID (1100+) for filesystem isolation.

#### 3. Encrypted Passwords
Use SHA-512 hashed passwords (not plaintext).

#### 4. Read-Only Directories
- `outbound/*` - read-only for tenant, write-only for system
- `inbound/*` - write-only for tenant (upload), read by system

#### 5. File Permissions

```bash
# Tenant uploads 276 request
/tenants/bcbs-florida/inbound/276/request-001.edi
  Owner: bcbs-florida (1100)
  Group: cho-workflows (5000)
  Perms: -rw-r----- (640)

# System writes 277 response
/tenants/bcbs-florida/outbound/277/response-001.edi
  Owner: cho-workflows (5000)
  Group: bcbs-florida (1100)
  Perms: -r--r----- (440)
```

## Implementation

### Step 1: Enhanced SFTP Server Image

Use `atmoz/sftp` with chroot support:

```yaml
image: atmoz/sftp:latest
env:
  - name: SFTP_USERS
    valueFrom:
      secretKeyRef:
        name: sftp-tenant-users
        key: users.conf
```

### Step 2: Tenant Provisioning Script

Create `scripts/provision-sftp-tenant.sh`:

```bash
#!/bin/bash
TENANT_ID=$1
TENANT_NAME=$2

# Generate password
PASSWORD=$(openssl rand -base64 24)

# Get next available UID
NEXT_UID=$((1100 + $(kubectl -n cho-sftp get secret sftp-tenant-users -o json | jq '.data."users.conf"' | base64 -d | wc -l)))

# Add user to secret
kubectl -n cho-sftp patch secret sftp-tenant-users --type='json' \
  -p="[{\"op\":\"add\",\"path\":\"/stringData/users.conf\",\"value\":\"${TENANT_ID}:${PASSWORD}:${NEXT_UID}:${NEXT_UID}:inbound,outbound:/bin/false:/tenants/${TENANT_ID}\\n\"}]"

# Create directory structure
kubectl -n cho-sftp exec deployment/sftp-server -- bash -c "
  mkdir -p /home/tenants/${TENANT_ID}/{inbound,outbound}/{276,277,278,834,835,837}
  chown -R ${NEXT_UID}:${NEXT_UID} /home/tenants/${TENANT_ID}
  chmod 750 /home/tenants/${TENANT_ID}
  chmod 770 /home/tenants/${TENANT_ID}/inbound/*
  chmod 550 /home/tenants/${TENANT_ID}/outbound/*
"

# Store credentials in Key Vault
az keyvault secret set \
  --vault-name cho-keyvault-prod \
  --name "sftp-${TENANT_ID}-password" \
  --value "${PASSWORD}"

echo "✅ Tenant ${TENANT_NAME} provisioned"
echo "   Username: ${TENANT_ID}"
echo "   UID: ${NEXT_UID}"
echo "   Password stored in Key Vault: sftp-${TENANT_ID}-password"
```

### Step 3: Updated Deployment

**k8s/sftp-server-multitenant.yaml:**

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: sftp-init-script
  namespace: cho-sftp
data:
  init-tenants.sh: |
    #!/bin/bash
    set -e
    
    # Create base tenant directory
    mkdir -p /home/tenants
    chmod 755 /home/tenants
    
    # Parse users.conf and create directories
    while IFS=: read -r username password uid gid dirs shell chroot; do
      if [ -n "$username" ] && [[ ! "$username" =~ ^# ]]; then
        TENANT_DIR="/home${chroot}"
        
        echo "Provisioning tenant: $username (UID: $uid)"
        
        # Create home directory
        mkdir -p "$TENANT_DIR"
        
        # Create subdirectories
        IFS=',' read -ra DIR_ARRAY <<< "$dirs"
        for dir in "${DIR_ARRAY[@]}"; do
          mkdir -p "$TENANT_DIR/$dir"/{276,277,278,834,835,837}
        done
        
        # Set ownership
        chown -R ${uid}:${gid} "$TENANT_DIR"
        
        # Set permissions
        chmod 750 "$TENANT_DIR"
        chmod -R 770 "$TENANT_DIR/inbound"
        chmod -R 550 "$TENANT_DIR/outbound"
        
        echo "  ✅ Directories created for $username"
      fi
    done < /etc/sftp/users.conf
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: sftp-server
  namespace: cho-sftp
spec:
  template:
    spec:
      initContainers:
      - name: init-tenant-dirs
        image: busybox:latest
        command: ["/bin/sh"]
        args: ["/scripts/init-tenants.sh"]
        volumeMounts:
        - name: sftp-data
          mountPath: /home
        - name: sftp-users
          mountPath: /etc/sftp/users.conf
          subPath: users.conf
        - name: init-script
          mountPath: /scripts
      containers:
      - name: sftp
        image: atmoz/sftp:latest
        env:
        - name: SFTP_USERS
          valueFrom:
            secretKeyRef:
              name: sftp-tenant-users
              key: users.conf
      volumes:
      - name: init-script
        configMap:
          name: sftp-init-script
          defaultMode: 0755
```

### Step 4: Tenant Onboarding Workflow

When a new health plan signs up:

1. **Sales creates tenant** in CRM (Salesforce, HubSpot)
2. **Platform team provisions**:
   ```bash
   ./scripts/provision-sftp-tenant.sh "bcbs-florida" "Blue Cross Blue Shield of Florida"
   ```
3. **Credentials sent** via secure channel (not email!)
4. **Test connection**:
   ```bash
   sftp bcbs-florida@sftp.cloudhealthoffice.com
   ```
5. **Configure trading partner** in Azure Integration Account
6. **Verify isolation** - ensure tenant can't see other directories

## Monitoring & Compliance

### Audit Logging

Log all SFTP access:

```yaml
# Fluent Bit sidecar for SFTP audit logs
- name: audit-logger
  image: fluent/fluent-bit:latest
  volumeMounts:
  - name: sftp-logs
    mountPath: /var/log/sftp
  env:
  - name: TENANT_ID
    valueFrom:
      fieldRef:
        fieldPath: metadata.labels['tenant']
```

### Compliance Checks

**Daily validation script** (`scripts/validate-sftp-isolation.sh`):

```bash
#!/bin/bash
# Verify each tenant can only access their directory

for tenant in $(kubectl -n cho-sftp get secret sftp-tenant-users -o json | jq -r '.data."users.conf"' | base64 -d | cut -d: -f1); do
  echo "Testing isolation for: $tenant"
  
  # Try to list parent directory (should fail)
  if sshpass -p "$PASSWORD" sftp -o StrictHostKeyChecking=no ${tenant}@localhost <<EOF 2>&1 | grep -q "Permission denied"
    ls ..
    exit
EOF
  then
    echo "✅ $tenant is properly isolated"
  else
    echo "❌ SECURITY ISSUE: $tenant can access parent directory!"
    exit 1
  fi
done
```

## Migration Plan

### Phase 1: Parallel Deployment (Weeks 1-2)
- Deploy new multi-tenant SFTP alongside existing
- Keep `logicapp` user for internal testing
- No production traffic yet

### Phase 2: Tenant Provisioning (Weeks 3-4)
- Provision each existing health plan as tenant
- Provide credentials via secure channel
- Test connectivity individually

### Phase 3: Cutover (Week 5)
- Update Logic Apps to use tenant-specific credentials
- Update Argo Workflows to read from tenant directories
- Monitor for any access issues

### Phase 4: Decommission Old SFTP (Week 6)
- Archive old SFTP data
- Remove `logicapp`/`clearinghouse` shared accounts
- Full multi-tenant operation

## Cost Implications

### Storage
- Each tenant: ~50 GB estimated
- 100 tenants: 5 TB total
- Azure Files Premium: ~$100/TB/month = **$500/month**

### Network
- Ingress: Free
- Egress: $0.05/GB (first 100 TB)
- Estimated: **$200/month** for 4 TB egress

### Management
- 1 Platform Engineer (20% time): **$2,000/month**

**Total**: ~$2,700/month for 100-tenant SFTP infrastructure

## Security Checklist

- [ ] Each tenant has unique credentials
- [ ] chroot jails prevent directory traversal
- [ ] Unique UIDs/GIDs per tenant
- [ ] Encrypted passwords (SHA-512)
- [ ] File permissions enforce read/write separation
- [ ] Audit logging captures all access
- [ ] Credentials stored in Azure Key Vault
- [ ] Daily isolation validation runs
- [ ] Tenant directories created atomically
- [ ] SSH host keys rotated annually
- [ ] SFTP service behind Azure Firewall
- [ ] IP whitelisting per tenant (optional)

## Alternatives Considered

### 1. Azure Files with SAS Tokens
**Pros**: Native Azure, per-tenant SAS URLs  
**Cons**: Not SFTP protocol, requires client changes  
**Verdict**: ❌ Not SFTP-compatible

### 2. Separate SFTP Pod per Tenant
**Pros**: Ultimate isolation  
**Cons**: Expensive (100 pods), complex  
**Verdict**: ❌ Doesn't scale

### 3. SFTP Gateway (AWS Transfer Family equivalent)
**Pros**: Managed service  
**Cons**: Azure doesn't offer this yet  
**Verdict**: ❌ Not available on Azure

### 4. Multi-Tenant SFTP with chroot (SELECTED)
**Pros**: Standard SFTP, cost-effective, scales  
**Cons**: Requires careful configuration  
**Verdict**: ✅ **Best option**

## Next Steps

1. **Create provision-sftp-tenant.sh script**
2. **Deploy multi-tenant SFTP to staging**
3. **Provision 2-3 pilot tenants**
4. **Validate isolation thoroughly**
5. **Update Logic Apps for tenant-specific paths**
6. **Roll out to production incrementally**

## References

- [HIPAA SFTP Requirements](https://www.hhs.gov/hipaa/for-professionals/security/guidance/cybersecurity/index.html)
- [atmoz/sftp Docker Image](https://github.com/atmoz/sftp)
- [SSH chroot Best Practices](https://wiki.archlinux.org/title/SFTP_chroot)
- [Multi-Tenant SaaS Security](https://docs.microsoft.com/en-us/azure/architecture/guide/multitenant/overview)
