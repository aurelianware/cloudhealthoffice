/**
 * Unified Payer Configuration Schema
 * Foundation for Issue #49 - Enables zero-code payer onboarding
 */

export interface PayerConfig {
  payerId: string;
  payerName: string;
  organizationName: string;
  contactInfo: ContactInfo;
  enabledModules: EnabledModules;
  appeals?: AppealsConfig;
  ecs?: EcsConfig;
  attachments?: AttachmentsConfig;
  infrastructure: InfrastructureConfig;
  monitoring: MonitoringConfig;
}

export interface ContactInfo {
  primaryContact: string;
  email: string;
  phone: string;
  supportEmail?: string;
}

export interface EnabledModules {
  appeals: boolean;
  ecs: boolean;
  attachments: boolean;
  authorizations: boolean;
}

export interface AppealsConfig {
  enabled: boolean;
  apiEndpoints: {
    test: string;
    prod: string;
  };
  authentication: {
    type: 'oauth' | 'apikey' | 'basic';
    keyVaultSecretName?: string;
  };
  timeout: number;
  retryCount: number;
  retryInterval: number;
  requestReasons: RequestReason[];
  subStatuses: SubStatus[];
  attachmentRules: AttachmentRules;
  modes: {
    realTimeWeb: boolean;
    realTimeB2B: boolean;
    ediBatch: boolean;
  };
}

export interface RequestReason {
  code: string;
  description: string;
  requiresAttachments: boolean;
}

export interface SubStatus {
  code: string;
  description: string;
  isFinal: boolean;
}

export interface AttachmentRules {
  pattern: 'pre-appeal' | 'post-appeal';
  maxFileSize: number;
  allowedFormats: string[];
  maxAttachments: number;
  requiredMetadata: string[];
}

export interface EcsConfig {
  enabled: boolean;
  apiEndpoints: {
    test: string;
    prod: string;
  };
  authentication: {
    type: 'oauth' | 'apikey' | 'basic';
    keyVaultSecretName?: string;
  };
  searchMethods: {
    serviceDate: boolean;
    member: boolean;
    checkNumber: boolean;
    claimHistory: boolean;
  };
  timeout: number;
  retryCount: number;
}

export interface AttachmentsConfig {
  enabled: boolean;
  sftpConfig: {
    host: string;
    port: number;
    username: string;
    keyVaultSecretName: string;
    inboundFolder: string;
    outboundFolder: string;
  };
  x12Config: {
    isa: {
      senderId: string;
      receiverId: string;
      senderQualifier: string;
      receiverQualifier: string;
    };
    transactionSets: {
      '275': boolean;
      '277': boolean;
      '278': boolean;
    };
  };
  archivalConfig: {
    storageAccountName: string;
    containerName: string;
    retentionDays: number;
  };
}

export interface InfrastructureConfig {
  resourceNamePrefix: string;
  location: string;
  environment: 'dev' | 'uat' | 'prod';
  tags: Record<string, string>;
  storageConfig: {
    sku: string;
    containers: string[];
    lifecycleRules: LifecycleRule[];
  };
  serviceBusConfig: {
    sku: string;
    topics: TopicConfig[];
    queues: QueueConfig[];
  };
  logicAppConfig: {
    sku: string;
    workerCount: number;
    alwaysOn: boolean;
  };
  keyVaultConfig: {
    sku: 'standard' | 'premium';
    enableSoftDelete: boolean;
    softDeleteRetentionDays: number;
  };
}

export interface LifecycleRule {
  name: string;
  enabled: boolean;
  daysAfterModificationGreaterThan: number;
  tierToCool?: number;
  tierToArchive?: number;
  delete?: number;
}

export interface TopicConfig {
  name: string;
  maxSizeInMegabytes: number;
  defaultMessageTimeToLive: string;
  requiresDuplicateDetection: boolean;
  subscriptions: SubscriptionConfig[];
}

export interface SubscriptionConfig {
  name: string;
  maxDeliveryCount: number;
  lockDuration: string;
}

export interface QueueConfig {
  name: string;
  maxSizeInMegabytes: number;
  defaultMessageTimeToLive: string;
  requiresDuplicateDetection: boolean;
}

export interface MonitoringConfig {
  applicationInsights: {
    enabled: boolean;
    samplingPercentage: number;
  };
  alertRules: AlertRule[];
  logRetentionDays: number;
}

export interface AlertRule {
  name: string;
  description: string;
  severity: number;
  frequency: string;
  timeWindow: string;
  query: string;
  threshold: number;
}

export interface ValidationResult {
  valid: boolean;
  errors: ValidationError[];
  warnings: ValidationWarning[];
}

export interface ValidationError {
  field: string;
  message: string;
  value?: any;
}

export interface ValidationWarning {
  field: string;
  message: string;
  suggestion?: string;
}
