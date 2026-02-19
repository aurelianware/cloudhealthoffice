# HashiCorp Vault Configuration for Cloud Health Office Microservices

This directory contains shared configuration providers for integrating HashiCorp Vault with Cloud Health Office microservices.

## 📁 Files

- **VaultConfigurationExtensions.cs**: Extension methods for adding Vault configuration to ASP.NET Core applications

## 🚀 Usage

### 1. Add VaultSharp Package Reference

Add to your `.csproj` file:
```xml
<PackageReference Include="VaultSharp" Version="1.13.0.1" />
<PackageReference Include="VaultSharp.Extensions.Configuration" Version="1.13.0.1" />
```

Or run the automated script:
```bash
./scripts/add-vault-packages.sh
```

### 2. Copy Configuration File

Copy `VaultConfigurationExtensions.cs` to your service:
```bash
# Option 1: Link as shared file (recommended)
# Add to your .csproj:
<Compile Include="../../shared/Configuration/VaultConfigurationExtensions.cs" Link="Configuration/VaultConfigurationExtensions.cs" />

# Option 2: Copy directly
cp src/shared/Configuration/VaultConfigurationExtensions.cs src/services/your-service/Configuration/
```

### 3. Update Program.cs

Add Vault configuration in your `Program.cs`:

```csharp
using CloudHealthOffice.Shared.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Add HashiCorp Vault configuration (multi-cloud secret management)
if (builder.Environment.IsProduction() || builder.Environment.IsStaging())
{
    builder.Configuration.AddVaultConfiguration(builder.Configuration);
}

// Rest of your configuration...
```

### 4. Configure Vault Settings

**Option A: Environment Variables (Kubernetes)**
```yaml
env:
- name: VAULT_ADDR
  value: "https://vault.vault.svc.cluster.local:8200"
- name: Vault__Role
  value: "cho-microservices"
- name: Vault__AuthMethod
  value: "kubernetes"
```

**Option B: appsettings.json**
```json
{
  "Vault": {
    "Address": "https://vault.vault.svc.cluster.local:8200",
    "Role": "cho-microservices",
    "AuthMethod": "kubernetes",
    "BasePath": "secret/data/cloudhealthoffice",
    "ReloadOnChange": true,
    "ReloadIntervalMinutes": 5
  }
}
```

### 5. Access Secrets

Secrets from Vault are automatically mapped to configuration keys:

```csharp
// Vault secret path: secret/cloudhealthoffice/cosmosdb
// Keys: connection-string, endpoint, key

// Access in code:
var connectionString = configuration["cosmosdb:connection-string"];
var endpoint = configuration["cosmosdb:endpoint"];

// Or use strongly-typed options:
builder.Services.Configure<CosmosDbOptions>(configuration.GetSection("cosmosdb"));
```

## 🔐 Authentication Methods

### Kubernetes Authentication (Production)

Used when running in Kubernetes with a ServiceAccount:

```yaml
# Deployment manifest
serviceAccountName: member-service-sa
```

The configuration provider automatically reads the service account token from `/var/run/secrets/kubernetes.io/serviceaccount/token`.

### AppRole Authentication (CI/CD)

Used for GitHub Actions or other CI/CD systems:

```csharp
// Configure in appsettings or environment variables
{
  "Vault": {
    "AuthMethod": "approle",
    "RoleId": "your-role-id",
    "SecretId": "your-secret-id"
  }
}
```

## 📦 Vault Secret Structure

Organize secrets in Vault following this structure:

```
secret/
├── cloudhealthoffice/
│   ├── cosmosdb/
│   │   ├── connection-string
│   │   ├── endpoint
│   │   └── key
│   ├── stripe/
│   │   ├── secret-key
│   │   ├── publishable-key
│   │   └── webhook-secret
│   ├── sftp/
│   │   └── clouddentaloffice/
│   │       ├── host
│   │       ├── username
│   │       └── password
│   └── servicebus/
│       └── connection-string
```

## 🔄 Automatic Secret Reload

The provider supports automatic secret reloading:

- **ReloadOnChange**: When `true`, secrets are periodically reloaded
- **ReloadIntervalMinutes**: How often to reload (default: 5 minutes)

This ensures your application always has the latest secrets without restart.

## ⚠️ Error Handling

The configuration provider is fault-tolerant:

- If Vault is unreachable, the application falls back to other configuration sources
- Failed secret retrievals are logged but don't crash the application
- Reload failures are logged but the application continues with existing secrets

## 🧪 Testing Locally

### Without Vault

The application works without Vault - it simply skips Vault configuration:

```bash
# Run without Vault
dotnet run
# Output: ℹ️  HashiCorp Vault not configured - skipping
```

### With Local Vault

For local development with Vault:

```bash
# Start Vault in dev mode
vault server -dev

# Set environment variable
export VAULT_ADDR="http://127.0.0.1:8200"
export VAULT_TOKEN="root-token"

# Populate test secrets
vault kv put secret/cloudhealthoffice/cosmosdb \
  connection-string="AccountEndpoint=https://localhost:8081/..."

# Run application
dotnet run
```

## 📚 References

- [VaultSharp Documentation](https://github.com/rajanadar/VaultSharp)
- [HashiCorp Vault Docs](https://www.vaultproject.io/docs)
- [Cloud Health Office Vault Integration Guide](../../docs/security/HASHICORP-VAULT-INTEGRATION.md)

## 🆘 Troubleshooting

### "Vault not configured - skipping"

**Cause**: `VAULT_ADDR` or `Vault:Address` not set  
**Solution**: Set environment variable or add to appsettings.json

### "Kubernetes service account token not found"

**Cause**: Running outside Kubernetes or wrong ServiceAccount  
**Solution**: Ensure deployment uses correct ServiceAccount or use AppRole auth

### "Permission denied"

**Cause**: Vault role doesn't have access to secret path  
**Solution**: Update Vault policy to grant read access:
```bash
vault policy write cho-microservices - <<EOF
path "secret/data/cloudhealthoffice/*" {
  capabilities = ["read", "list"]
}
EOF
```

### Secrets not loading

**Check these:**
1. Vault is running and unsealed: `vault status`
2. Authentication is working: Check application logs for "Connected to Vault"
3. Secrets exist in Vault: `vault kv list secret/cloudhealthoffice`
4. Policy allows access: `vault token capabilities secret/data/cloudhealthoffice/cosmosdb`
