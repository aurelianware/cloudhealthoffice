import fs from 'fs';
import path from 'path';
import { load } from 'js-yaml';

describe('SFTP Sensor wiring', () => {
  const sensorPath = path.resolve(
    __dirname,
    '../../..',
    'infrastructure',
    'argo-events',
    'sensors',
    'sftp-sensor.yaml',
  );

  const sensorDoc = load(fs.readFileSync(sensorPath, 'utf8')) as any;
  const spec = sensorDoc?.spec ?? {};

  it('listens to both 275 and 278 polling events', () => {
    expect(sensorDoc?.kind).toBe('Sensor');

    const dependencyNames = (spec.dependencies ?? []).map((dependency: any) => dependency?.name);
    expect(dependencyNames).toEqual(expect.arrayContaining(['sftp-275-poll', 'sftp-278-poll']));

    const poll275 = (spec.dependencies ?? []).find((dependency: any) => dependency?.name === 'sftp-275-poll');
    expect(poll275?.eventSourceName).toBe('sftp-polling');
    expect(poll275?.eventName).toBe('sftp-poll-275');
  });

  it('submits the 275 workflow with correct parameters', () => {
    const trigger275 = (spec.triggers ?? []).find(
      (trigger: any) => trigger?.template?.name === 'trigger-275-ingest',
    );
    const argoWorkflow = trigger275?.template?.argoWorkflow;

    expect(argoWorkflow?.operation).toBe('submit');
    expect(argoWorkflow?.source?.resource?.metadata?.generateName).toBe('x12-275-ingest-');
    expect(argoWorkflow?.source?.resource?.spec?.workflowTemplateRef?.name).toBe('x12-275-ingest');

    const parameterNames = (
      argoWorkflow?.source?.resource?.spec?.arguments?.parameters ?? []
    ).map((param: any) => param?.name);
    expect(parameterNames).toEqual(expect.arrayContaining(['sftp-host', 'sftp-folder', 'file-pattern']));

    const paramMapping = (argoWorkflow?.parameters ?? []).find(
      (mapping: any) => mapping?.dest === 'spec.arguments.parameters.0.value',
    );
    expect(paramMapping?.src?.dependencyName).toBe('sftp-275-poll');
    expect(paramMapping?.src?.dataKey).toBe('metadata.transaction-type');
  });

  it('submits the 278 workflow with correct parameters', () => {
    const trigger278 = (spec.triggers ?? []).find(
      (trigger: any) => trigger?.template?.name === 'trigger-278-ingest',
    );
    const argoWorkflow = trigger278?.template?.argoWorkflow;

    expect(argoWorkflow?.operation).toBe('submit');
    expect(argoWorkflow?.source?.resource?.metadata?.generateName).toBe('x12-278-ingest-');
    expect(argoWorkflow?.source?.resource?.spec?.workflowTemplateRef?.name).toBe('x12-278-ingest');

    const parameterNames = (
      argoWorkflow?.source?.resource?.spec?.arguments?.parameters ?? []
    ).map((param: any) => param?.name);
    expect(parameterNames).toEqual(expect.arrayContaining(['sftp-host', 'sftp-folder', 'file-pattern']));

    const paramMapping = (argoWorkflow?.parameters ?? []).find(
      (mapping: any) => mapping?.dest === 'spec.arguments.parameters.0.value',
    );
    expect(paramMapping?.src?.dependencyName).toBe('sftp-278-poll');
    expect(paramMapping?.src?.dataKey).toBe('metadata.transaction-type');
  });

  it('configures retries for ingestion triggers to handle transient failures', () => {
    const triggers = spec.triggers ?? [];
    ['trigger-275-ingest', 'trigger-278-ingest'].forEach((name) => {
      const trigger = triggers.find((entry: any) => entry?.template?.name === name);
      const retryStrategy = trigger?.retryStrategy ?? {};

      expect(retryStrategy?.steps).toBeGreaterThanOrEqual(3);
      expect(retryStrategy?.duration).toBe('30s');
    });
  });
});
