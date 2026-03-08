import fs from 'fs';
import path from 'path';
import { loadAll } from 'js-yaml';

describe('SFTP Argo EventSources', () => {
  const eventSourcePath = path.resolve(
    __dirname,
    '../../..',
    'infrastructure',
    'argo-events',
    'sftp-event-source.yaml',
  );

  const documents = loadAll(fs.readFileSync(eventSourcePath, 'utf8')) as any[];
  const pollingSource = documents.find((doc) => doc?.metadata?.name === 'sftp-polling');
  const watcherSource = documents.find((doc) => doc?.metadata?.name === 'sftp-file-watcher');

  it('defines the polling event source for 275 traffic', () => {
    expect(pollingSource?.kind).toBe('EventSource');
    expect(pollingSource?.spec?.calendar?.['sftp-poll-275']?.schedule).toBe('*/15 * * * *');
    expect(pollingSource?.spec?.calendar?.['sftp-poll-275']?.metadata?.workflow).toBe('x12-275-ingest');
    expect(pollingSource?.spec?.calendar?.['sftp-poll-275']?.metadata?.['sftp-folder']).toBe('/inbound/attachments');
    expect(pollingSource?.spec?.calendar?.['sftp-poll-275']?.metadata?.['file-pattern']).toBe('*.edi');
  });

  it('keeps the 275 and 278 calendars aligned but offset', () => {
    const poll275 = pollingSource?.spec?.calendar?.['sftp-poll-275'];
    const poll278 = pollingSource?.spec?.calendar?.['sftp-poll-278'];
    expect(poll275?.timezone).toBe('UTC');
    expect(poll278?.timezone).toBe('UTC');
    expect(poll278?.schedule).toBe('5,20,35,50 * * * *');
    expect(poll278?.metadata?.workflow).toBe('x12-278-ingest');
  });

  it('includes the file watcher alternative for observability', () => {
    expect(watcherSource?.kind).toBe('EventSource');
    expect(watcherSource?.spec?.generic?.['sftp-new-file']?.url).toBe('http://sftp-watcher-service:8080/events');
  });
});
