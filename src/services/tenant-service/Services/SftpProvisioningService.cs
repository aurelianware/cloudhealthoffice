using System.Diagnostics;
using System.Text;

namespace TenantService.Services;

public interface ISftpProvisioningService
{
    Task<SftpProvisioningResult> ProvisionTenantSftpAsync(
        string tenantId, 
        string tenantName, 
        List<string> environments,
        string? keyVaultName = null);
}

public class SftpProvisioningResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Output { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class SftpProvisioningService : ISftpProvisioningService
{
    private static readonly System.Text.RegularExpressions.Regex EnvironmentNameValidator = 
        new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);
    
    private readonly ILogger<SftpProvisioningService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _scriptsPath;

    public SftpProvisioningService(
        ILogger<SftpProvisioningService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        
        // Default to /app/scripts in container, fallback to local path
        _scriptsPath = configuration["Sftp:ScriptsPath"] ?? "/app/scripts";
    }

    public async Task<SftpProvisioningResult> ProvisionTenantSftpAsync(
        string tenantId, 
        string tenantName, 
        List<string> environments,
        string? keyVaultName = null)
    {
        var result = new SftpProvisioningResult();
        
        try
        {
            _logger.LogInformation(
                "Provisioning SFTP for tenant {TenantId} with environments: {Environments}",
                SanitizeForLog(tenantId),
                string.Join(",", environments));

            // Build script path
            var scriptPath = Path.Combine(_scriptsPath, "provision-sftp-tenant.sh");
            
            if (!File.Exists(scriptPath))
            {
                result.Success = false;
                result.Error = $"Provisioning script not found at: {scriptPath}";
                _logger.LogError(result.Error);
                return result;
            }

            // Validate environment names to prevent command injection
            // Only allow alphanumeric characters, hyphens, and underscores
            foreach (var env in environments)
            {
                if (!EnvironmentNameValidator.IsMatch(env))
                {
                    result.Success = false;
                    // Sanitize the environment name in error message to prevent log injection
                    var sanitizedEnv = System.Text.RegularExpressions.Regex.Replace(env, @"[^\w\-]", "?");
                    result.Error = $"Invalid environment name: {sanitizedEnv}. Only alphanumeric characters, hyphens, and underscores are allowed.";
                    _logger.LogError("Invalid environment name provided. Only alphanumeric characters, hyphens, and underscores are allowed.");
                    return result;
                }
            }

            // Build arguments
            var keyVault = keyVaultName ?? _configuration["Azure:KeyVault:Name"] ?? "cho-keyvault-prod";
            var environmentsArg = string.Join(",", environments);

            // Build structured argument list to avoid shell parsing of user-controlled values
            var scriptArguments = new[]
            {
                tenantId,
                tenantName,
                keyVault,
                "--environments",
                environmentsArg
            };

            // Execute script
            var processResult = await ExecuteScriptAsync(scriptPath, scriptArguments);
            
            result.Success = processResult.ExitCode == 0;
            result.Output = processResult.Output;
            result.Error = processResult.Error;
            result.Message = processResult.ExitCode == 0 
                ? $"SFTP provisioned successfully for tenant {tenantId}"
                : $"SFTP provisioning failed with exit code {processResult.ExitCode}";
                
            result.Metadata["tenantId"] = tenantId;
            result.Metadata["environments"] = environmentsArg;
            result.Metadata["keyVault"] = keyVault;
            result.Metadata["exitCode"] = processResult.ExitCode.ToString();

            if (result.Success)
            {
                _logger.LogInformation(
                    "Successfully provisioned SFTP for tenant {TenantId} with {EnvironmentCount} environments", 
                    tenantId, 
                    environments.Count);
            }
            else
            {
                _logger.LogError(
                    "Failed to provision SFTP for tenant {TenantId}: {Error}", 
                    tenantId, 
                    result.Error);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during SFTP provisioning for tenant {TenantId}", tenantId);
            result.Success = false;
            result.Error = ex.Message;
            result.Message = $"Exception during SFTP provisioning: {ex.Message}";
            return result;
        }
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    private async Task<ProcessExecutionResult> ExecuteScriptAsync(string scriptPath, IEnumerable<string> arguments)
    {
        var result = new ProcessExecutionResult();
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        try
        {
            var processInfo = new ProcessStartInfo
            {
                // Execute the script directly and supply each argument separately to avoid shell interpretation
                FileName = scriptPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in arguments)
            {
                processInfo.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = processInfo };
            
            // Capture output asynchronously
            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                    _logger.LogDebug("[SFTP Script] {Output}", e.Data);
                }
            };
            
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    errorBuilder.AppendLine(e.Data);
                    _logger.LogWarning("[SFTP Script Error] {Error}", e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            
            // Wait for completion with timeout (5 minutes)
            var completed = await Task.Run(() => process.WaitForExit(300000));
            
            if (!completed)
            {
                process.Kill();
                result.ExitCode = -1;
                result.Error = "Script execution timed out after 5 minutes";
            }
            else
            {
                result.ExitCode = process.ExitCode;
            }

            result.Output = outputBuilder.ToString();
            result.Error = errorBuilder.ToString();

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute script: {ScriptPath}", scriptPath);
            result.ExitCode = -1;
            result.Error = ex.Message;
            return result;
        }
    }

    private class ProcessExecutionResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }
}
