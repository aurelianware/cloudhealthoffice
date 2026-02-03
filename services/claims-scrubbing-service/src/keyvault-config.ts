/**
 * Cloud Health Office - Key Vault Configuration Helper
 * 
 * Provides auto-configuration for Azure Key Vault integration
 * Automatically loads secrets from Key Vault when available
 */

import { SecretClient } from '@azure/keyvault-secrets';
import { DefaultAzureCredential } from '@azure/identity';

export interface KeyVaultConfig {
  /**
   * Key Vault URI (e.g., https://my-keyvault.vault.azure.net/)
   * Can be set via KEY_VAULT_URI environment variable
   */
  keyVaultUri?: string;
  
  /**
   * Enable Key Vault integration (default: true if keyVaultUri is set)
   */
  enabled?: boolean;
}

export interface SecretMapping {
  /**
   * Environment variable name to look for
   */
  envVar: string;
  
  /**
   * Key Vault secret name (defaults to envVar if not specified)
   */
  secretName?: string;
  
  /**
   * Whether this secret is required
   */
  required?: boolean;
  
  /**
   * Default value if secret is not found (only used if not required)
   */
  defaultValue?: string;
}

/**
 * Key Vault client singleton
 */
let secretClient: SecretClient | null = null;

/**
 * Initialize Key Vault client
 */
export function initializeKeyVaultClient(config: KeyVaultConfig): SecretClient | null {
  if (!config.keyVaultUri || config.enabled === false) {
    console.log('[KeyVault] Not configured or disabled');
    return null;
  }
  
  try {
    const credential = new DefaultAzureCredential();
    secretClient = new SecretClient(config.keyVaultUri, credential);
    console.log(`[KeyVault] Initialized client for ${config.keyVaultUri}`);
    return secretClient;
  } catch (error) {
    console.error('[KeyVault] Failed to initialize client:', error);
    return null;
  }
}

/**
 * Get Key Vault client (must be initialized first)
 */
export function getKeyVaultClient(): SecretClient | null {
  return secretClient;
}

/**
 * Load a secret from Key Vault with fallback to environment variable
 * 
 * Priority order:
 * 1. Environment variable (if already set)
 * 2. Key Vault secret
 * 3. Default value (if provided and not required)
 * 
 * @param mapping Secret mapping configuration
 * @returns The secret value or undefined
 */
export async function loadSecret(mapping: SecretMapping): Promise<string | undefined> {
  // First, check if environment variable is already set
  const envValue = process.env[mapping.envVar];
  if (envValue) {
    console.log(`[KeyVault] Using environment variable for ${mapping.envVar}`);
    return envValue;
  }
  
  // Try to load from Key Vault if client is initialized
  if (secretClient) {
    const secretName = mapping.secretName || mapping.envVar.toLowerCase().replace(/_/g, '-');
    try {
      const secret = await secretClient.getSecret(secretName);
      if (secret.value) {
        console.log(`[KeyVault] Loaded ${mapping.envVar} from Key Vault secret: ${secretName}`);
        return secret.value;
      }
    } catch (error: any) {
      // 404 means secret doesn't exist, which is OK if not required
      if (error?.statusCode === 404) {
        console.log(`[KeyVault] Secret ${secretName} not found in Key Vault`);
      } else {
        console.warn(`[KeyVault] Failed to load ${secretName}:`, error);
      }
    }
  }
  
  // Fall back to default value
  if (mapping.defaultValue !== undefined) {
    console.log(`[KeyVault] Using default value for ${mapping.envVar}`);
    return mapping.defaultValue;
  }
  
  // If required and not found, log warning
  if (mapping.required) {
    console.warn(`[KeyVault] Required secret ${mapping.envVar} not found in environment or Key Vault`);
  }
  
  return undefined;
}

/**
 * Load multiple secrets from Key Vault
 * 
 * @param mappings Array of secret mappings
 * @returns Object with environment variable names as keys and secret values
 */
export async function loadSecrets(mappings: SecretMapping[]): Promise<Record<string, string | undefined>> {
  const results: Record<string, string | undefined> = {};
  
  for (const mapping of mappings) {
    results[mapping.envVar] = await loadSecret(mapping);
  }
  
  return results;
}

/**
 * Auto-configure service with Key Vault secrets
 * 
 * This function:
 * 1. Initializes Key Vault client if KEY_VAULT_URI is set
 * 2. Loads specified secrets from Key Vault
 * 3. Sets environment variables with loaded secrets (if not already set)
 * 
 * @param mappings Array of secret mappings to load
 * @returns True if Key Vault was configured, false otherwise
 */
export async function autoConfigureKeyVault(mappings: SecretMapping[]): Promise<boolean> {
  const keyVaultUri = process.env.KEY_VAULT_URI;
  
  if (!keyVaultUri) {
    console.log('[KeyVault] KEY_VAULT_URI not set, skipping auto-configuration');
    return false;
  }
  
  // Initialize Key Vault client
  const client = initializeKeyVaultClient({ keyVaultUri, enabled: true });
  if (!client) {
    console.warn('[KeyVault] Failed to initialize client, skipping auto-configuration');
    return false;
  }
  
  // Load secrets
  const secrets = await loadSecrets(mappings);
  
  // Set environment variables for secrets that were loaded
  let configuredCount = 0;
  for (const [envVar, value] of Object.entries(secrets)) {
    if (value !== undefined && !process.env[envVar]) {
      process.env[envVar] = value;
      configuredCount++;
    }
  }
  
  console.log(`[KeyVault] Auto-configured ${configuredCount} secrets from Key Vault`);
  return true;
}

/**
 * Common secret mappings for Cloud Health Office services
 */
export const commonSecretMappings: SecretMapping[] = [
  // Cosmos DB
  {
    envVar: 'COSMOS_ENDPOINT',
    secretName: 'cosmos-endpoint',
    required: false
  },
  {
    envVar: 'COSMOS_KEY',
    secretName: 'cosmos-key',
    required: false
  },
  
  // Storage
  {
    envVar: 'STORAGE_ACCOUNT_NAME',
    secretName: 'storage-account-name',
    required: false
  },
  {
    envVar: 'STORAGE_CONNECTION_STRING',
    secretName: 'storage-connection-string',
    required: false
  },
  
  // Event Grid
  {
    envVar: 'EVENT_GRID_ENDPOINT',
    secretName: 'event-grid-endpoint',
    required: false
  },
  {
    envVar: 'EVENT_GRID_KEY',
    secretName: 'event-grid-key',
    required: false
  },
  
  // Backend API
  {
    envVar: 'BACKEND_BASE_URL',
    secretName: 'backend-base-url',
    required: false
  },
  {
    envVar: 'BACKEND_API_TOKEN',
    secretName: 'backend-api-token',
    required: false
  },
  
  // Kafka
  {
    envVar: 'KAFKA_BOOTSTRAP_SERVERS',
    secretName: 'kafka-bootstrap-servers',
    required: false
  },
  {
    envVar: 'KAFKA_SASL_USERNAME',
    secretName: 'kafka-sasl-username',
    required: false
  },
  {
    envVar: 'KAFKA_SASL_PASSWORD',
    secretName: 'kafka-sasl-password',
    required: false
  }
];
