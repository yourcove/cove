import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { createServer } from 'node:net';
import { expect, test } from '@playwright/test';
import { captureScreenshotPair, frozenMotionCss } from './capture-helpers';

async function availablePort() {
  const server = createServer();
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('Could not reserve an instance-manager port.');
  const { port } = address;
  server.close();
  await once(server, 'close');
  return port;
}

async function waitForManagerUrl(process: ChildProcessWithoutNullStreams) {
  return new Promise<string>((resolve, reject) => {
    let output = '';
    const timeout = setTimeout(() => reject(new Error(`Instance manager did not start.\n${output}`)), 120_000);
    const inspect = (chunk: Buffer) => {
      output += chunk.toString();
      const match = output.match(/Cove Instance Manager: (http:\/\/[^\s]+)/);
      if (!match) return;
      clearTimeout(timeout);
      resolve(match[1]);
    };
    process.stdout.on('data', inspect);
    process.stderr.on('data', inspect);
    process.once('error', (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    process.once('exit', (code) => {
      clearTimeout(timeout);
      reject(new Error(`Instance manager exited with code ${code}.\n${output}`));
    });
  });
}

async function stopManagerProcess(managerProcess: ChildProcessWithoutNullStreams | undefined) {
  if (!managerProcess || managerProcess.exitCode !== null || managerProcess.pid == null) return;
  const exited = once(managerProcess, 'exit').then(() => true);
  try { process.kill(-managerProcess.pid, 'SIGTERM'); } catch { return; }
  const stopped = await Promise.race([exited, new Promise<false>((resolve) => setTimeout(() => resolve(false), 5_000))]);
  if (stopped) return;
  try { process.kill(-managerProcess.pid, 'SIGKILL'); } catch { return; }
  await exited;
}

test('capture the native instance manager screenshot', async ({ page }) => {
  test.setTimeout(180_000);
  const repositoryRoot = path.resolve(process.cwd(), '..');
  const managerDataRoot = await mkdtemp(path.join(tmpdir(), 'cove-instance-manager-capture-'));
  let managerProcess: ChildProcessWithoutNullStreams | undefined;

  try {
    const port = await availablePort();
    managerProcess = spawn('dotnet', [
      'run',
      '--project', path.join(repositoryRoot, 'src/Cove.InstanceManager/Cove.InstanceManager.csproj'),
      '--configuration', 'Release',
      '--no-launch-profile',
      '--',
      '--port', String(port),
      '--no-browser',
    ], {
      cwd: repositoryRoot,
      detached: true,
      env: { ...process.env, XDG_DATA_HOME: managerDataRoot },
    });
    const managerUrl = await waitForManagerUrl(managerProcess);
    await page.route('**/api/instances', async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        json: [
          {
            id: 'default', name: 'Default', homePath: '/local/cove/default', port: 5073,
            managedPostgres: true, postgresPort: 5433, running: true, status: 'running',
            processId: 1001, logPath: null, errorLogPath: null, url: 'http://127.0.0.1:5073',
            lastStartedAt: null, lastStoppedAt: null,
          },
          {
            id: 'archive', name: 'Archive', homePath: '/local/cove/archive', port: 5074,
            managedPostgres: true, postgresPort: 5434, running: false, status: 'stopped',
            processId: null, logPath: null, errorLogPath: null, url: 'http://127.0.0.1:5074',
            lastStartedAt: null, lastStoppedAt: null,
          },
        ],
      });
    });
    await page.goto(managerUrl);
    await expect(page.getByRole('heading', { level: 1, name: 'Cove Instance Manager' })).toBeVisible();
    await expect(page.getByRole('article')).toHaveCount(2);
    await expect(page.getByText('Default', { exact: true })).toBeVisible();
    await expect(page.getByText('Archive', { exact: true })).toBeVisible();
    await expect(page.getByText('Running', { exact: true })).toBeVisible();
    await expect(page.getByText('Stopped', { exact: true })).toBeVisible();

    await page.addStyleTag({ content: `${frozenMotionCss} .meta > span:last-child { font-size: 0; } .meta > span:last-child::after { content: '[local path hidden]'; font-size: 14px; }` });
    await captureScreenshotPair(page, 'instance-manager');
  } finally {
    await stopManagerProcess(managerProcess);
    await rm(managerDataRoot, { recursive: true, force: true });
  }
});
