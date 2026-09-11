// Run against the published macOS executable before packaging.
import { spawn, execFileSync } from 'node:child_process';
import { mkdtemp, readFile, mkdir, writeFile, symlink, lstat, readdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

if (process.platform !== 'darwin') throw new Error('This smoke check requires macOS.');
const executable = resolve(process.argv[2] ?? 'publish/Cove');
const root = await mkdtemp(join(tmpdir(), 'cove postgres smoke '));
const home = join(root, 'Cove Home');
await mkdir(home);
// Reproduce an older Cove run that cached a client-only Homebrew installation.
const clientBin = join(root, 'client only bin');
await mkdir(clientBin);
for (const tool of ['pg_ctl', 'initdb', 'pg_isready', 'psql', 'createdb']) {
  await writeFile(join(clientBin, tool), '#!/bin/sh\nprintf "client tool (PostgreSQL) 18.4\\n"\n', { mode: 0o755 });
}
const clientFiles = await readdir(clientBin);
await mkdir(join(home, 'pgsql'));
await symlink(clientBin, join(home, 'pgsql', 'bin'), 'dir');
const env = {
  ...process.env,
  // Exclude Homebrew from discovery; assert the download path below as well.
  PATH: clientBin + ':/usr/bin:/bin:/usr/sbin:/sbin',
  COVE_HOME: home,
  Cove__Postgres__Managed: 'true',
  Cove__Postgres__DataPath: home,
  Cove__Postgres__Port: '55432',
  Cove__Postgres__Database: 'cove',
  Cove__Port: '55073',
  ASPNETCORE_URLS: 'http://127.0.0.1:55073',
};
let child;
let exited;
let output = '';

function start() {
  output = '';
  child = spawn(executable, [], { cwd: root, env, stdio: ['ignore', 'pipe', 'pipe'] });
  exited = new Promise((resolveExit, reject) => {
    child.once('error', reject);
    child.once('exit', (code, signal) => resolveExit({ code, signal }));
  });
  // Attach a handler immediately, including when startup fails before polling.
  exited.catch(() => {});
  for (const stream of [child.stdout, child.stderr]) {
    stream.setEncoding('utf8');
    stream.on('data', text => { output += text; });
  }
}

async function ready() {
  const deadline = Date.now() + 15 * 60_000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null || child.signalCode !== null)
      throw new Error('Cove exited before becoming ready.');
    if (output.includes('Managed PostgreSQL is ready')) {
      try {
        const response = await fetch('http://127.0.0.1:55073/', { signal: AbortSignal.timeout(2000) });
        if (response.ok && (await response.text()).includes('<html')) return;
      } catch { /* The web host starts after database initialization. */ }
    }
    await delay(1000);
  }
  throw new Error('Timed out waiting for Cove database and web UI.');
}

async function stop() {
  child.kill('SIGTERM');
  const result = await Promise.race([exited, delay(30_000).then(() => null)]);
  if (!result) throw new Error('Cove did not stop within 30 seconds.');
  if (result.code !== 0) throw new Error(`Cove shutdown failed: ${JSON.stringify(result)}`);
  try {
    pg('pg_ctl', ['status', '-D', join(home, 'pgdata')]);
  } catch (error) {
    if (error.status === 3) return; // pg_ctl: server is not running.
    throw error;
  }
  throw new Error('PostgreSQL remained running after Cove stopped.');
}

function pg(command, args) {
  return execFileSync(join(home, 'pgsql', 'bin', command), args, {
    encoding: 'utf8', timeout: 30_000,
    env: { ...env, DYLD_LIBRARY_PATH: join(home, 'pgsql', 'lib') },
    stdio: ['ignore', 'pipe', 'pipe'],
  }).trim();
}
function sql(query) {
  return pg('psql', ['-h', '127.0.0.1', '-p', '55432', '-U', 'postgres', '-d', 'cove', '-At', '-v', 'ON_ERROR_STOP=1', '-c', query]);
}

try {
  start();
  await ready();
  if (!output.includes('PostgreSQL binaries not found'))
    throw new Error('System PostgreSQL masked the managed download path.');
  if ((await lstat(join(home, 'pgsql', 'bin'))).isSymbolicLink())
    throw new Error('The cached client-only bin link was not replaced.');
  if (JSON.stringify(await readdir(clientBin)) !== JSON.stringify(clientFiles))
    throw new Error('Cove modified the client-only installation.');
  const initLog = await readFile(join(home, 'pg-initdb.log'), 'utf8');
  if ((await readFile(join(home, 'pgdata', 'PG_VERSION'), 'utf8')).trim() !== '18')
    throw new Error('Unexpected PostgreSQL major version.');
  if (sql("SELECT count(*) FROM pg_extension WHERE extname = 'vector'") !== '1')
    throw new Error('pgvector was not enabled.');
  sql('CREATE TABLE cove_startup_smoke (value integer); INSERT INTO cove_startup_smoke VALUES (32)');
  await stop();
  start();
  await ready();
  if (output.includes('Initializing data directory') || await readFile(join(home, 'pg-initdb.log'), 'utf8') !== initLog)
    throw new Error('Restart unexpectedly reinitialized the database.');
  if (sql('SELECT value FROM cove_startup_smoke') !== '32')
    throw new Error('Database contents did not survive restart.');
  await stop();
  console.log('macOS smoke passed: client-only link recovery, managed download, initialization, pgvector, web UI, shutdown, and persistent restart.');
} catch (error) {
  console.error(output);
  console.error(`Smoke data and logs retained at ${root}`);
  throw error;
} finally {
  if (child && child.exitCode === null && child.signalCode === null) {
    child.kill('SIGKILL');
    await exited.catch(() => {});
  }
  try { pg('pg_ctl', ['stop', '-D', join(home, 'pgdata'), '-m', 'fast', '-w']); } catch { /* Already stopped or never initialized. */ }
}
