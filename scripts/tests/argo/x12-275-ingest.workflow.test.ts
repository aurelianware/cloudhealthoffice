import fs from 'fs';
import path from 'path';
import { load } from 'js-yaml';

describe('x12-275-ingest WorkflowTemplate', () => {
  const workflowPath = path.resolve(
    __dirname,
    '../../..',
    'infrastructure',
    'argo-workflows',
    'x12-275-ingest.yaml',
  );

  const workflowDoc = load(fs.readFileSync(workflowPath, 'utf8')) as any;
  const spec = workflowDoc?.spec ?? {};
  const templates: any[] = spec.templates ?? [];

  const getTemplate = (name: string) => templates.find((template) => template?.name === name);
  const getTask = (templateName: string, taskName: string) => {
    const template = getTemplate(templateName);
    return template?.dag?.tasks?.find((task: any) => task?.name === taskName);
  };

  it('declares workflow metadata and global settings', () => {
    expect(workflowDoc?.kind).toBe('WorkflowTemplate');
    expect(workflowDoc?.metadata?.name).toBe('x12-275-ingest');
    expect(spec.entrypoint).toBe('ingest-275');
    expect(spec.serviceAccountName).toBe('argo-workflow-sa');

    const ttlStrategy = spec.ttlStrategy ?? {};
    expect(ttlStrategy.secondsAfterCompletion).toBe(86400);
    expect(ttlStrategy.secondsAfterSuccess).toBe(43200);
    expect(ttlStrategy.secondsAfterFailure).toBe(604800);

    const retryStrategy = spec.retryStrategy ?? {};
    expect(retryStrategy.limit).toBe(3);
    expect(retryStrategy.retryPolicy).toBe('OnFailure');
  });

  it('orchestrates required DAG tasks with appropriate dependencies', () => {
    const ingestTemplate = getTemplate('ingest-275');
    expect(ingestTemplate).toBeDefined();

    const taskNames = (ingestTemplate?.dag?.tasks ?? []).map((task: any) => task.name);
    expect(taskNames).toEqual(
      expect.arrayContaining([
        'sftp-fetch',
        'parse-x12-275',
        'validate-schema',
        'extract-metadata',
        'store-data-lake',
        'publish-kafka',
        'cleanup-sftp',
      ]),
    );

    expect(getTask('ingest-275', 'sftp-fetch')?.template).toBe('sftp-fetch-template');
    expect(getTask('ingest-275', 'parse-x12-275')?.dependencies).toEqual(['sftp-fetch']);
    expect(getTask('ingest-275', 'parse-x12-275')?.template).toBe('parse-x12-template');
    expect(getTask('ingest-275', 'validate-schema')?.dependencies).toEqual(['parse-x12-275']);
    expect(getTask('ingest-275', 'extract-metadata')?.dependencies).toEqual(['validate-schema']);

    const publishKafka = getTask('ingest-275', 'publish-kafka');
    expect(publishKafka?.dependencies).toEqual(expect.arrayContaining(['extract-metadata', 'store-data-lake']));
    expect(publishKafka?.template).toBe('kafka-publish-template');

    const cleanupSftp = getTask('ingest-275', 'cleanup-sftp');
    expect(cleanupSftp?.dependencies).toEqual(['publish-kafka']);
  });

  it('exposes required parameters, volumes, and artifact wiring', () => {
    const argumentNames = (spec.arguments?.parameters ?? []).map((param: any) => param?.name);
    expect(argumentNames).toEqual(
      expect.arrayContaining([
        'sftp-host',
        'sftp-folder',
        'file-pattern',
        's3-bucket',
        'kafka-topic',
        'sender-id',
        'receiver-id',
      ]),
    );

    const volumeNames = (spec.volumes ?? []).map((volume: any) => volume?.name);
    expect(volumeNames).toEqual(
      expect.arrayContaining(['work-volume', 'sftp-credentials', 'kafka-credentials']),
    );

    const kafkaTemplate = getTemplate('kafka-publish-template');
    const kafkaArtifact = kafkaTemplate?.inputs?.artifacts?.find((artifact: any) => artifact?.name === 'metadata-json');
    expect(kafkaArtifact?.path).toBe('/data/input/metadata.json');

    const storeTemplate = getTemplate('store-s3-template');
    const storeArtifact = storeTemplate?.inputs?.artifacts?.find((artifact: any) => artifact?.name === 'edi-file');
    expect(storeArtifact?.path).toBe('/data/input');

    const sftpTemplate = getTemplate('sftp-fetch-template');
    expect(sftpTemplate?.container?.env).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ name: 'SFTP_USERNAME' }),
        expect.objectContaining({ name: 'SFTP_PASSWORD' }),
      ]),
    );
  });
});
