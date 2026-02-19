using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.Kubernetes;
using VaultSharp.V1.AuthMethods.AppRole;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace CloudHealthOffice.Shared.Configuration;

/// <summary>
/// Extension methods for adding HashiCorp Vault configuration to ASP.NET Core applications
/// </summary>
public static class VaultConfigurationExtensions
{
    /// <summary>
    /// Adds HashiCorp Vault as a configuration source
    /// </summary>
    /// <param name="builder">The configuration builder</param>
    /// <param name="bootstrapConfig">Bootstrap configuration containing Vault settings</param>
    /// <returns>The configuration builder for chaining</returns>
    public static IConfigurationBuilder AddVaultConfiguration(
        this IConfigurationBuilder builder,
        IConfiguration bootstrapConfig)
    {
        var vaultEndpoint = bootstrapConfig["Vault:Address"] 
            ?? Environment.GetEnvironmentVariable("VAULT_ADDR");
        
        if (string.IsNullOrEmpty(vaultEndpoint))
        {
            Console.WriteLine("ℹ️  HashiCorp Vault not configured - skipping");
            return builder;
        }

        try
        {
            var vaultRole = bootstrapConfig["Vault:Role"] ?? "cho-microservices";
            var authMethod = bootstrapConfig["Vault:AuthMethod"] ?? "kubernetes";
            var basePath = bootstrapConfig["Vault:BasePath"] ?? "secret/data/cloudhealthoffice";
            var reloadOnChange = bool.Parse(bootstrapConfig["Vault:ReloadOnChange"] ?? "true");
            var reloadIntervalMinutes = int.Parse(bootstrapConfig["Vault:ReloadIntervalMinutes"] ?? "5");

            Console.WriteLine($"🔐 Configuring HashiCorp Vault integration:");
            Console.WriteLine($"   Endpoint: {vaultEndpoint}");
            Console.WriteLine($"   Auth Method: {authMethod}");
            Console.WriteLine($"   Role: {vaultRole}");
            Console.WriteLine($"   Base Path: {basePath}");

            IAuthMethodInfo authMethodInfo = authMethod.ToLower() switch
            {
                "kubernetes" => CreateKubernetesAuth(vaultRole),
                "approle" => CreateAppRoleAuth(bootstrapConfig),
                _ => throw new ArgumentException($"Unsupported auth method: {authMethod}")
            };

            var vaultClientSettings = new VaultClientSettings(vaultEndpoint, authMethodInfo);
            var vaultClient = new VaultClient(vaultClientSettings);

            // Test connection
            var healthStatus = vaultClient.V1.System.GetHealthStatusAsync().GetAwaiter().GetResult();
            Console.WriteLine($"✓ Connected to Vault (sealed: {healthStatus.Sealed})");

            builder.Add(new VaultConfigurationSource
            {
                Client = vaultClient,
                BasePath = basePath,
                ReloadOnChange = reloadOnChange,
                ReloadInterval = TimeSpan.FromMinutes(reloadIntervalMinutes)
            });

            Console.WriteLine("✅ HashiCorp Vault configuration provider added successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Failed to configure HashiCorp Vault: {ex.Message}");
            Console.WriteLine("   Application will use fallback configuration sources");
        }

        return builder;
    }

    private static IAuthMethodInfo CreateKubernetesAuth(string role)
    {
        // Read service account token
        var tokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
        
        if (!File.Exists(tokenPath))
        {
            throw new InvalidOperationException(
                "Kubernetes service account token not found. Ensure pod is using correct ServiceAccount.");
        }

        var jwt = File.ReadAllText(tokenPath);
        return new KubernetesAuthMethodInfo(role, jwt);
    }

    private static IAuthMethodInfo CreateAppRoleAuth(IConfiguration config)
    {
        var roleId = config["Vault:RoleId"] 
            ?? throw new InvalidOperationException("Vault:RoleId not configured");
        var secretId = config["Vault:SecretId"] 
            ?? throw new InvalidOperationException("Vault:SecretId not configured");

        return new AppRoleAuthMethodInfo(roleId, secretId);
    }
}

/// <summary>
/// Configuration source for HashiCorp Vault
/// </summary>
public class VaultConfigurationSource : IConfigurationSource
{
    public IVaultClient Client { get; set; } = null!;
    public string BasePath { get; set; } = "secret/data/cloudhealthoffice";
    public bool ReloadOnChange { get; set; } = true;
    public TimeSpan ReloadInterval { get; set; } = TimeSpan.FromMinutes(5);

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new VaultConfigurationProvider(this);
    }
}

/// <summary>
/// Configuration provider that loads secrets from HashiCorp Vault
/// </summary>
public class VaultConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly VaultConfigurationSource _source;
    private Timer? _reloadTimer;

    public VaultConfigurationProvider(VaultConfigurationSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public override void Load()
    {
        LoadAsync().GetAwaiter().GetResult();

        if (_source.ReloadOnChange)
        {
            _reloadTimer = new Timer(
                _ => {
                    try
                    {
                        LoadAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️  Failed to reload secrets from Vault: {ex.Message}");
                    }
                },
                null,
                _source.ReloadInterval,
                _source.ReloadInterval);
        }
    }

    private async Task LoadAsync()
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Console.WriteLine($"🔄 Loading secrets from Vault: {_source.BasePath}");

            // Determine the path format (with or without /data/ prefix)
            var secretPath = _source.BasePath.Contains("/data/") 
                ? _source.BasePath.Replace("/data/", "/") 
                : _source.BasePath;

            // List all secrets under the base path
            var listResult = await _source.Client.V1.Secrets.KeyValue.V2.ReadSecretPathsAsync(
                secretPath,
                mountPoint: "secret");

            if (listResult?.Data?.Keys != null)
            {
                foreach (var key in listResult.Data.Keys)
                {
                    try
                    {
                        var fullPath = $"{secretPath}/{key}".TrimStart('/');
                        var secret = await _source.Client.V1.Secrets.KeyValue.V2.ReadSecretAsync(
                            path: fullPath,
                            mountPoint: "secret");

                        if (secret?.Data?.Data != null)
                        {
                            FlattenSecrets(secret.Data.Data, key, data);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️  Failed to read secret '{key}': {ex.Message}");
                    }
                }
            }

            // Also try to read secrets directly from base path
            try
            {
                var baseSecret = await _source.Client.V1.Secrets.KeyValue.V2.ReadSecretAsync(
                    path: secretPath,
                    mountPoint: "secret");

                if (baseSecret?.Data?.Data != null)
                {
                    FlattenSecrets(baseSecret.Data.Data, string.Empty, data);
                }
            }
            catch
            {
                // Base path might not contain secrets directly - that's ok
            }

            Console.WriteLine($"✓ Loaded {data.Count} configuration values from Vault");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to load secrets from Vault: {ex.Message}");
            Console.WriteLine($"   {ex.GetType().Name}: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
        }

        Data = data;
        OnReload();
    }

    private void FlattenSecrets(
        IDictionary<string, object> secrets,
        string prefix,
        IDictionary<string, string> data)
    {
        foreach (var kvp in secrets)
        {
            var key = string.IsNullOrEmpty(prefix) 
                ? kvp.Key 
                : $"{prefix}:{kvp.Key}";

            if (kvp.Value is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.Object)
                {
                    var nestedDict = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText());
                    if (nestedDict != null)
                    {
                        FlattenSecrets(nestedDict, key, data);
                    }
                }
                else
                {
                    data[key] = jsonElement.ToString();
                }
            }
            else if (kvp.Value is IDictionary<string, object> nestedDict)
            {
                FlattenSecrets(nestedDict, key, data);
            }
            else
            {
                data[key] = kvp.Value?.ToString() ?? string.Empty;
            }
        }
    }

    public void Dispose()
    {
        _reloadTimer?.Dispose();
    }
}
