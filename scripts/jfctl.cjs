#!/usr/bin/env node
'use strict';
/*
 * jfctl.cjs — JFlash 工作台 · 生产网关生命周期控制（单一权威）
 * JF-1 自建，2026-10-18。
 * 原则：只做 start/stop/restart/guard；绝不杀进程；意图先落盘；失败如实上报。
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const http = require('http');
const { execFileSync } = require('child_process');

const SHELL = path.join(process.env.USERPROFILE || os.homedir(), '.openclaw', 'jf-shell');
const CONTROL = path.join(SHELL, 'control');
const STATE = path.join(CONTROL, 'control-state.json');
const LOCK = path.join(CONTROL, '.op.lock');
const LOGDIR = path.join(SHELL, 'logs');
const LOG = path.join(LOGDIR, 'supervisor.jsonl');
const REAL_CLI = path.join(process.env.APPDATA || '', 'npm', 'openclaw.cmd');
const PORT = 18789;
const TASK = 'OpenClaw Gateway';
const GRACE_MS = 180000;
const STABLE_RESET_MS = 10 * 60 * 1000;
const BACKOFF_MIN = [1, 5, 15];

function ensureDirs() {
  for (const d of [CONTROL, LOGDIR]) fs.mkdirSync(d, { recursive: true });
}
function nowIso() { return new Date().toISOString(); }
function log(event, extra) {
  try {
    ensureDirs();
    const row = Object.assign({ at: nowIso(), event, pid: process.pid }, extra || {});
    fs.appendFileSync(LOG, JSON.stringify(row) + '\n');
  } catch (_) {}
}
function defaultState(desired) {
  return {
    schemaVersion: 1, desired: desired || 'running', mode: 'owner', ownerEnabled: true,
    autoRecover: true, rev: 1, desiredRevision: 1, phase: 'unknown',
    pid: null, attempts: [], nextRetryAt: null, pausedReason: null,
    lastStableAt: null, lastObservedAt: null, updatedAt: nowIso()
  };
}
function readState() {
  try {
    const raw = fs.readFileSync(STATE, 'utf8');
    const s = JSON.parse(raw);
    if (!s || s.schemaVersion !== 1) return null;
    return s;
  } catch (_) { return null; }
}
function writeState(s) {
  ensureDirs();
  s.updatedAt = nowIso();
  const tmp = STATE + '.' + process.pid + '.tmp';
  fs.writeFileSync(tmp, JSON.stringify(s, null, 2));
  fs.renameSync(tmp, STATE);
}
function statSafe(p) { try { return fs.statSync(p); } catch (_) { return null; } }

function acquireLock() {
  ensureDirs();
  try {
    const fd = fs.openSync(LOCK, 'wx');
    fs.writeSync(fd, JSON.stringify({ pid: process.pid, at: nowIso() }));
    fs.closeSync(fd);
    process.on('exit', () => { try { fs.unlinkSync(LOCK); } catch (_) {} });
    return true;
  } catch (_) {
    const st = statSafe(LOCK);
    if (st && Date.now() - st.mtimeMs > 300000) {
      try { fs.unlinkSync(LOCK); } catch (_) {}
      return acquireLock();
    }
    return false;
  }
}
function releaseLock() { try { fs.unlinkSync(LOCK); } catch (_) {} }

function isReady() {
  return new Promise((resolve) => {
    const req = http.get({ host: '127.0.0.1', port: PORT, path: '/readyz', timeout: 2000 }, (res) => {
      res.resume();
      resolve(res.statusCode === 200);
    });
    req.on('timeout', () => { req.destroy(); resolve(false); });
    req.on('error', () => resolve(false));
  });
}
function portListening() {
  return new Promise((resolve) => {
    const sock = new (require('net').Socket)();
    let done = false;
    const fin = (v) => { if (!done) { done = true; try { sock.destroy(); } catch (_) {} resolve(v); } };
    sock.setTimeout(1200);
    sock.on('connect', () => fin(true));
    sock.on('timeout', () => fin(false));
    sock.on('error', () => fin(false));
    sock.connect(PORT, '127.0.0.1');
  });
}
function findGateway() {
  const ps = [
    "Get-CimInstance Win32_Process -Filter \"Name='node.exe'\" |",
    "Where-Object { $_.CommandLine -and $_.CommandLine -match 'openclaw' -and $_.CommandLine -match 'gateway' -and $_.CommandLine -notmatch '18790' -and $_.CommandLine -notmatch 'jf-shell-p1' -and $_.CommandLine -notmatch 'codex' } |",
    "ForEach-Object { \"$($_.ProcessId)|$($_.CreationDate.ToUniversalTime().ToString('o'))\" }"
  ].join(' ');
  try {
    const out = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', ps], { encoding: 'utf8', timeout: 20000, windowsHide: true }).trim();
    if (!out) return [];
    return out.split(/\r?\n/).filter(Boolean).map((line) => {
      const parts = line.split('|');
      return { pid: Number(parts[0]), createdAt: parts[1] || null };
    });
  } catch (_) { return []; }
}
function runTask() {
  try {
    execFileSync('schtasks.exe', ['/Run', '/TN', TASK], { encoding: 'utf8', timeout: 30000 });
    return true;
  } catch (e) { log('task-run-failed', { error: String(e && e.message || e) }); return false; }
}
function cliStop() {
  const line = REAL_CLI + ' gateway stop --force';
  try {
    const out = execFileSync('cmd.exe', ['/d', '/c', line], {
      encoding: 'utf8', timeout: 120000, windowsHide: true,
      env: Object.assign({}, process.env, { OPENCLAW_WINDOWS_TASK_NAME: TASK })
    });
    return { ok: true, out: String(out).slice(-1200) };
  } catch (e) {
    const msg = String((e && (e.stdout || e.stderr || e.message)) || e).slice(-1200);
    return { ok: false, out: msg };
  }
}
function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

async function cmdStatus(jsonOut) {
  const [ready, port, procs] = await Promise.all([isReady(), portListening(), Promise.resolve(findGateway())]);
  const s = readState();
  const out = {
    ok: true, desired: s ? s.desired : 'unknown', phase: s ? s.phase : 'uninitialized',
    ready, portListening: port, gatewayProcesses: procs,
    autoRecover: s ? s.autoRecover : null, pausedReason: s ? s.pausedReason : null,
    attempts: s ? s.attempts.length : 0, nextRetryAt: s ? s.nextRetryAt : null,
    lastStableAt: s ? s.lastStableAt : null, rev: s ? s.rev : null,
    controlState: STATE
  };
  if (jsonOut) process.stdout.write(JSON.stringify(out, null, 2) + '\n');
  else {
    console.log('desired=' + out.desired + ' phase=' + out.phase + ' ready=' + ready +
      ' port=' + port + ' procs=' + procs.length + ' attempts=' + out.attempts +
      (out.pausedReason ? ' paused=' + out.pausedReason : ''));
  }
  return out;
}

async function cmdInit() {
  ensureDirs();
  if (readState()) { console.log('already-initialized'); return 0; }
  const ready = await isReady();
  const s = defaultState(ready ? 'running' : 'stopped');
  s.phase = ready ? 'ready' : 'stopped';
  s.lastStableAt = ready ? nowIso() : null;
  writeState(s);
  log('init', { desired: s.desired, ready });
  console.log(JSON.stringify({ ok: true, desired: s.desired, ready }));
  return 0;
}

async function cmdStart(wait) {
  if (!acquireLock()) { console.log(JSON.stringify({ ok: false, error: 'busy' })); return 3; }
  try {
    let s = readState() || defaultState('running');
    s.desired = 'running'; s.desiredRevision = ++s.rev; s.pausedReason = null;
    s.attempts = []; s.nextRetryAt = null;
    if (await isReady()) {
      s.phase = 'ready'; s.lastStableAt = nowIso(); writeState(s);
      log('start-already-ready', { rev: s.rev });
      console.log(JSON.stringify({ ok: true, already: true, desired: 'running' }));
      return 0;
    }
    if (findGateway().length > 0) {
      s.phase = 'starting'; writeState(s);
      log('start-leave-alone-existing-process', { rev: s.rev });
      console.log(JSON.stringify({ ok: true, alreadyStarting: true, desired: 'running' }));
      return 0;
    }
    s.phase = 'starting'; writeState(s);
    const launched = runTask();
    log('start-requested', { rev: s.rev, launched });
    if (wait) {
      const deadline = Date.now() + wait * 1000;
      while (Date.now() < deadline) {
        await sleep(2000);
        if (await isReady()) { s.phase = 'ready'; s.lastStableAt = nowIso(); writeState(s); break; }
      }
    }
    console.log(JSON.stringify({ ok: launched, desired: 'running', phase: (readState() || s).phase }));
    return launched ? 0 : 2;
  } finally { releaseLock(); }
}

async function stopConfirmed(deadlineMs) {
  const deadline = Date.now() + deadlineMs;
  while (Date.now() < deadline) {
    const [ready, port, procs] = await Promise.all([isReady(), portListening(), Promise.resolve(findGateway())]);
    if (!ready && !port && procs.length === 0) return { confirmed: true, ready, port, procs };
    await sleep(2000);
  }
  const [ready, port, procs] = await Promise.all([isReady(), portListening(), Promise.resolve(findGateway())]);
  return { confirmed: false, ready, port, procs };
}

async function cmdStop() {
  if (!acquireLock()) { console.log(JSON.stringify({ ok: false, error: 'busy' })); return 3; }
  try {
    let s = readState() || defaultState('running');
    // 意图先落盘：即使后续失败，desired=stopped 也保留
    s.desired = 'stopped'; s.desiredRevision = ++s.rev; s.phase = 'stopping';
    s.lastStableAt = null; s.nextRetryAt = null;
    writeState(s);
    log('stop-intent', { rev: s.rev });
    const res = cliStop();
    log('stop-cli', { ok: res.ok, out: res.out });
    const v = await stopConfirmed(90000);
    if (v.confirmed) {
      s = readState() || s;
      s.phase = 'stopped'; s.pid = null;
      s.stopEvidence = {
        confirmedAt: nowIso(), processCount: v.procs.length, ready: v.ready, port: v.port,
        cliOk: res.ok, desiredPersisted: true, revision: s.rev
      };
      writeState(s);
      log('stop-confirmed', s.stopEvidence);
      console.log(JSON.stringify({ ok: true, desired: 'stopped', evidence: s.stopEvidence }));
      return 0;
    }
    s = readState() || s;
    s.phase = 'stop-failed';
    s.stopEvidence = { confirmedAt: nowIso(), processCount: v.procs.length, ready: v.ready, port: v.port, cliOk: res.ok };
    writeState(s);
    log('stop-failed', s.stopEvidence);
    console.log(JSON.stringify({ ok: false, error: 'stop-not-confirmed', evidence: s.stopEvidence }));
    return 2;
  } finally { releaseLock(); }
}

async function cmdRestart() {
  const stopCode = await cmdStop();
  if (stopCode !== 0) {
    const s = readState() || {};
    s.desired = 'running'; s.desiredRevision = ++s.rev; s.phase = 'restart-failed'; writeState(s);
    console.log(JSON.stringify({ ok: false, error: 'restart-stop-failed' }));
    return 2;
  }
  return await cmdStart(0);
}

function parseArgs(argv) {
  const args = argv.slice(2);
  const cmd = args[0] || 'status';
  const opts = { json: args.includes('--json'), wait: 0 };
  const wi = args.indexOf('--wait');
  if (wi >= 0) opts.wait = Number(args[wi + 1] || 240) || 240;
  return { cmd, opts };
}

async function guardOnce(verbose) {
  const s0 = readState();
  if (!s0) { if (verbose) console.log('uninitialized'); return 0; }
  if (!s0.ownerEnabled || s0.mode !== 'owner') { log('guard-skip-not-owner'); return 0; }
  if (s0.desired !== 'running') { log('guard-skip-desired-' + s0.desired); return 0; }
  if (s0.pausedReason) { log('guard-skip-paused'); return 0; }
  if (s0.nextRetryAt && Date.now() < Date.parse(s0.nextRetryAt)) { log('guard-backoff'); return 0; }
  const ready = await isReady();
  const procs = findGateway();
  let s = readState() || s0;
  if (ready) {
    if (s.phase !== 'ready') { s.phase = 'ready'; writeState(s); log('guard-phase-ready'); }
    if (!s.lastStableAt) { s.lastStableAt = nowIso(); writeState(s); }
    else if (Date.now() - Date.parse(s.lastStableAt) >= STABLE_RESET_MS && (s.attempts.length || s.pausedReason)) {
      s.attempts = []; s.pausedReason = null; s.nextRetryAt = null; writeState(s); log('guard-budget-reset');
    }
    return 0;
  }
  if (procs.length > 0) {
    log('guard-leave-alone', { procs: procs.length, pid: procs[0].pid });
    return 0;
  }
  // 无进程、未就绪 → 按预算恢复
  if (s.attempts.length >= 3) {
    s.pausedReason = 'budget'; s.nextRetryAt = null; s.phase = 'paused'; writeState(s);
    log('guard-budget-exhausted');
    return 0;
  }
  if (!acquireLock()) { log('guard-busy'); return 0; }
  try {
    s = readState() || s;
    if (s.desired !== 'running' || s.pausedReason) return 0;
    if (await isReady()) return 0;
    if (findGateway().length > 0) { log('guard-leave-alone-race'); return 0; }
    const seq = s.attempts.length + 1;
    s.attempts.push({ seq, at: nowIso(), trigger: 'guard' });
    s.nextRetryAt = new Date(Date.now() + BACKOFF_MIN[Math.min(seq - 1, BACKOFF_MIN.length - 1)] * 60000).toISOString();
    s.phase = 'recovering'; writeState(s);
    const launched = runTask();
    log('guard-recover', { seq, launched, nextRetryAt: s.nextRetryAt });
    if (verbose) console.log(JSON.stringify({ ok: launched, attempt: seq }));
    return launched ? 0 : 2;
  } finally { releaseLock(); }
}

async function cmdLogon(verbose) {
  if (!acquireLock()) { log('logon-busy'); return 3; }
  try {
    let s = readState() || defaultState('stopped');
    const running = findGateway().length > 0 || await isReady();
    if (running) { log('logon-gateway-already-running'); if (verbose) console.log(JSON.stringify({ ok: true, note: 'already-running' })); return 0; }
    s.desired = 'stopped';
    s.desiredRevision = ++s.rev;
    s.phase = 'stopped-manual';
    s.lastStableAt = null;
    s.attempts = [];
    s.nextRetryAt = null;
    s.pausedReason = null;
    writeState(s);
    log('logon-manual-mode', { rev: s.rev });
    if (verbose) console.log(JSON.stringify({ ok: true, desired: 'stopped', mode: 'manual-start' }));
    return 0;
  } finally { releaseLock(); }
}

async function ensureOnce(verbose) {
  const s = readState();
  if (!s) { if (verbose) console.log('uninitialized'); return 0; }
  if (!s.ownerEnabled || s.desired !== 'running') { log('ensure-skip', { desired: s.desired }); return 0; }
  if (await isReady()) { log('ensure-ready'); return 0; }
  if (findGateway().length > 0) { log('ensure-leave-alone'); return 0; }
  if (!acquireLock()) { log('ensure-busy'); return 0; }
  try {
    if (await isReady() || findGateway().length > 0) return 0;
    const launched = runTask();
    log('ensure-start', { launched });
    if (verbose) console.log(JSON.stringify({ ok: launched }));
    return launched ? 0 : 2;
  } finally { releaseLock(); }
}

(async () => {
  const { cmd, opts } = parseArgs(process.argv);
  try {
    switch (cmd) {
      case 'status': await cmdStatus(opts.json); return;
      case 'init': process.exitCode = await cmdInit(); return;
      case 'start': process.exitCode = await cmdStart(opts.wait); return;
      case 'stop': process.exitCode = await cmdStop(); return;
      case 'restart': process.exitCode = await cmdRestart(); return;
      case 'guard': process.exitCode = await guardOnce(opts.json); return;
      case 'ensure': process.exitCode = await ensureOnce(opts.json); return;
      case 'logon': process.exitCode = await cmdLogon(opts.json); return;
      case 'recover-on':
      case 'recover-off': {
        const s = readState() || defaultState('running');
        s.autoRecover = cmd === 'recover-on'; s.rev++; writeState(s);
        console.log(JSON.stringify({ ok: true, autoRecover: s.autoRecover })); return;
      }
      default:
        console.log('usage: jfctl.cjs status [--json] | init | start [--wait N] | stop | restart | guard | ensure | recover-on|off');
        process.exitCode = 64;
    }
  } catch (e) {
    log('fatal', { error: String(e && e.stack || e) });
    console.log(JSON.stringify({ ok: false, error: String(e && e.message || e) }));
    process.exitCode = 1;
  }
})();
