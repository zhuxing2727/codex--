import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import readline from 'node:readline/promises'
import { stdin as input, stdout as output } from 'node:process'
import http from 'node:http'
import crypto from 'node:crypto'
import { spawnSync } from 'node:child_process'
import { pathToFileURL } from 'node:url'
import { CONFIG, CONFIG_DIR as DEFAULT_CONFIG_DIR, endpoint } from './ergouzi-config.mjs'

const BASE_URL = CONFIG.baseUrl
const PORT = CONFIG.agentPort
const BIND_HOST = CONFIG.bindHost
const QUOTA_PER_UNIT = CONFIG.quotaPerUnit
const CONFIG_DIR = DEFAULT_CONFIG_DIR
const CONFIG_FILE = path.join(CONFIG_DIR, 'config.json')
const SUMMARY_CACHE_FILE = path.join(CONFIG_DIR, 'summary-cache.json')
const BRIDGE_SECRET_FILE = path.join(CONFIG_DIR, 'bridge.secret')
const TOKEN_SYNC_URL = process.env.ERGOUZI_TOKEN_SYNC_URL || endpoint(BIND_HOST, CONFIG.tokenSyncPort)
const TOKEN_SYNC_RELOAD_COOLDOWN_MS = CONFIG.tokenRefreshCooldownMs

function ensureBridgeSecret() {
  fs.mkdirSync(CONFIG_DIR, { recursive: true })
  try {
    const existing = fs.readFileSync(BRIDGE_SECRET_FILE, 'utf8').trim()
    if (existing.length >= 32) return existing
  } catch {}
  const generated = crypto.randomBytes(32).toString('base64url')
  try {
    fs.writeFileSync(BRIDGE_SECRET_FILE, generated + '\n', { encoding: 'utf8', mode: 0o600, flag: 'wx' })
  } catch {}
  return fs.readFileSync(BRIDGE_SECRET_FILE, 'utf8').trim()
}

const BRIDGE_SECRET = ensureBridgeSecret()

function dpapi(value, decrypt = false) {
  const command = decrypt
    ? '$s=[Console]::In.ReadToEnd().Trim(); $sec=ConvertTo-SecureString -String $s; $ptr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec); try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }'
    : '$s=[Console]::In.ReadToEnd(); ConvertTo-SecureString -String $s -AsPlainText -Force | ConvertFrom-SecureString'
  const p = requirePowerShell(command, value)
  if (p.status !== 0 || !p.stdout.trim()) throw new Error('Windows DPAPI failed')
  return p.stdout.trim()
}

function requirePowerShell(command, value) {
  const windowsPowerShellModules = [
    path.join(process.env.WINDIR || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'Modules'),
    path.join(process.env.ProgramFiles || 'C:\\Program Files', 'WindowsPowerShell', 'Modules'),
    path.join(process.env.USERPROFILE || os.homedir(), 'Documents', 'WindowsPowerShell', 'Modules'),
  ].join(';')
  return spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', command], {
    input: String(value), encoding: 'utf8', windowsHide: true,
    env: { ...process.env, PSModulePath: windowsPowerShellModules },
  })
}

function saveConfig(values) {
  fs.mkdirSync(CONFIG_DIR, { recursive: true })
  const body = {
    accessToken: dpapi(values.accessToken),
    sessionId: values.sessionId ? dpapi(values.sessionId) : '',
    cookie: values.cookie ? dpapi(values.cookie) : '',
    accessExpiresAt: Number(values.accessExpiresAt || 0),
    updatedAt: new Date().toISOString(),
  }
  fs.writeFileSync(CONFIG_FILE, JSON.stringify(body, null, 2), { encoding: 'utf8', mode: 0o600 })
}

function tokenExpiry(token) {
  try {
    const part = String(token).split('.')[1]
    const payload = JSON.parse(Buffer.from(part, 'base64url').toString('utf8'))
    return Number(payload.exp || 0)
  } catch { return 0 }
}

function importedExpiry(accessToken, value) {
  const supplied = Number(value || 0)
  return Number.isFinite(supplied) && supplied > 0
    ? supplied
    : tokenExpiry(accessToken) || Math.floor(Date.now() / 1000) + 300
}

function loadConfig() {
  const body = JSON.parse(fs.readFileSync(CONFIG_FILE, 'utf8'))
  return {
    accessToken: dpapi(body.accessToken, true),
    sessionId: body.sessionId ? dpapi(body.sessionId, true) : '',
    cookie: body.cookie ? dpapi(body.cookie, true) : '',
    accessExpiresAt: Number(body.accessExpiresAt || 0),
  }
}

function unwrap(value) {
  for (let i = 0; i < 3 && value && typeof value === 'object' && !Array.isArray(value); i += 1) {
    if (value.data && typeof value.data === 'object') value = value.data
    else if (value.result && typeof value.result === 'object') value = value.result
    else break
  }
  return value
}

function numberFrom(object, names) {
  for (const name of names) {
    const value = object && object[name]
    if (value !== null && value !== undefined && value !== '' && Number.isFinite(Number(value))) return Number(value)
  }
  return null
}

function todayRange() {
  const now = new Date()
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate())
  const end = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1)
  const pad = (value) => String(value).padStart(2, '0')
  const date = start.getFullYear() + '-' + pad(start.getMonth() + 1) + '-' + pad(start.getDate())
  return { start: Math.floor(start.getTime() / 1000), end: Math.floor(end.getTime() / 1000) - 1, date }
}

let config
// Single-flight lock: all concurrent callers await the same token refresh.
let refreshLock = null
let lastWalletReloadAt = 0
let todayUsageCache = null

function wait(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

function authExpiredError(detail = '') {
  const error = new Error('登录状态已过期，请在钱包页面重新登录' + (detail ? '：' + detail : ''))
  error.code = 'AUTH_EXPIRED'
  return error
}

async function waitForImportedToken(deadline) {
  while (Date.now() < deadline) {
    try {
      const next = loadConfig()
      if (next.accessExpiresAt > Math.floor(Date.now() / 1000) + 60) {
        config = next
        return true
      }
    } catch {}
    await wait(CONFIG.tokenImportPollIntervalMs)
  }
  return false
}

async function requestWalletReload() {
  const now = Date.now()
  if (now - lastWalletReloadAt < TOKEN_SYNC_RELOAD_COOLDOWN_MS) {
    return waitForImportedToken(Date.now() + CONFIG.tokenRefreshWaitMs)
  }
  lastWalletReloadAt = now
  let response
  try {
    response = await fetch(TOKEN_SYNC_URL + '/internal/reload', {
      method: 'POST',
      headers: { 'X-Ergouzi-Bridge': BRIDGE_SECRET },
      signal: AbortSignal.timeout(Math.min(CONFIG.requestTimeoutMs, 5000)),
    })
  } catch {
    return false
  }
  const body = await response.json().catch(() => ({}))
  if (!response.ok || !body.ok) return false
  return waitForImportedToken(Date.now() + CONFIG.bridgeReloadWaitMs)
}

async function performTokenRefresh() {
  config ||= loadConfig()
  if (config.accessExpiresAt > Math.floor(Date.now() / 1000) + 60) return
  if (!config.sessionId && !config.cookie) {
    if (await requestWalletReload()) return
    throw authExpiredError('钱包同步未能取得新令牌')
  }
  const headers = { Accept: 'application/json' }
  if (config.sessionId) headers['X-Auth-Session'] = config.sessionId
  if (config.cookie) headers.Cookie = config.cookie
  const res = await fetch(BASE_URL + '/api/user/auth/refresh', { method: 'POST', headers })
  const envelope = await res.json().catch(() => ({}))
  const data = unwrap(envelope)
  if (res.ok && envelope.success && data && data.access_token) {
    config.accessToken = data.access_token
    config.accessExpiresAt = Number(data.access_expires_at || 0)
    if (data.session && data.session.sid) config.sessionId = data.session.sid
    saveConfig(config)
    return
  }
  if (res.status !== 401) throw new Error('Ergouzi token refresh failed (HTTP ' + res.status + ')')
  if (await requestWalletReload()) return
  throw authExpiredError('钱包会话或令牌已失效')
}

async function refreshIfNeeded() {
  config ||= loadConfig()
  if (config.accessExpiresAt > Math.floor(Date.now() / 1000) + 60) return
  if (!refreshLock) {
    refreshLock = performTokenRefresh().finally(() => { refreshLock = null })
  }
  await refreshLock
}

async function api(pathname, params) {
  let retried = false
  while (true) {
    await refreshIfNeeded()
    const url = new URL(BASE_URL + pathname)
    for (const [key, value] of Object.entries(params || {})) url.searchParams.set(key, String(value))
    const res = await fetch(url, { headers: { Authorization: 'Bearer ' + config.accessToken, Accept: 'application/json' }, signal: AbortSignal.timeout(CONFIG.requestTimeoutMs) })
    const body = await res.json().catch(() => ({}))
    if (res.ok) return unwrap(body)
    if (res.status === 401 && !retried) {
      retried = true
      config.accessExpiresAt = 0
      await refreshIfNeeded()
      continue
    }
    if (res.status === 401) throw authExpiredError('上游接口拒绝当前令牌')
    throw new Error('Ergouzi API HTTP ' + res.status)
  }
}

async function balance() {
  const data = await api('/api/user/self')
  return parseSelf(data)
}

export function parseSelf(data) {
  const quota = numberFrom(data, ['quota', 'remaining_quota', 'remain_quota', 'balance_quota', 'remaining', 'balance'])
  if (quota === null) throw new Error('quota field not found in /api/user/self')
  return { ok: true, totalBalance: quota / QUOTA_PER_UNIT, currency: 'USD', updatedAt: new Date().toISOString() }
}

async function todayUsage() {
  const range = todayRange()
  const data = await api('/api/billing/analysis/self', { start_timestamp: range.start, end_timestamp: range.end })
  return parseBilling(data, range.date)
}

function loadTodayUsageCache() {
  if (todayUsageCache) return todayUsageCache
  try {
    const body = JSON.parse(fs.readFileSync(SUMMARY_CACHE_FILE, 'utf8'))
    if (body && typeof body === 'object') todayUsageCache = body
  } catch {}
  return todayUsageCache
}

function rememberTodayUsage(value) {
  const next = {
    amount: Number(value.amount),
    currency: String(value.currency || 'USD'),
    date: String(value.date || todayRange().date),
    savedAt: new Date().toISOString(),
  }
  if (!Number.isFinite(next.amount)) return
  todayUsageCache = next
  try {
    fs.mkdirSync(CONFIG_DIR, { recursive: true })
    fs.writeFileSync(SUMMARY_CACHE_FILE, JSON.stringify(next, null, 2), { encoding: 'utf8', mode: 0o600 })
  } catch {}
}

function cachedTodayUsage() {
  const cached = loadTodayUsageCache()
  if (!cached || cached.date !== todayRange().date || !Number.isFinite(Number(cached.amount))) return null
  return {
    ok: true,
    amount: Number(cached.amount),
    currency: String(cached.currency || 'USD'),
    date: cached.date,
    usageStale: true,
    usageError: '暂时使用上次成功数据',
  }
}

async function todayUsageWithCache() {
  try {
    const value = await todayUsage()
    rememberTodayUsage(value)
    return value
  } catch (error) {
    // Authentication failures must remain explicit; a stale quota must not hide them.
    if (error && error.code === 'AUTH_EXPIRED') throw error
    const cached = cachedTodayUsage()
    if (cached) return cached
    throw error
  }
}

async function summary() {
  const currentBalance = await balance()
  const usage = await todayUsageWithCache()
  return {
    ok: true,
    totalBalance: currentBalance.totalBalance,
    currency: currentBalance.currency,
    todayUsage: usage.amount,
    todayUsageCurrency: usage.currency || currentBalance.currency,
    todayUsageDate: usage.date,
    usageStale: usage.usageStale === true,
    usageError: usage.usageError || '',
    updatedAt: new Date().toISOString(),
  }
}

export function parseBilling(data, date = todayRange().date) {
  const root = unwrap(data)
  const summary = root && root.summary && typeof root.summary === 'object' ? root.summary : root
  const quota = numberFrom(summary, ['total_quota', 'wallet_quota', 'quota', 'amount', 'cost', 'total'])
  if (quota === null) throw new Error('total_quota field not found in billing analysis')
  return { ok: true, amount: quota / QUOTA_PER_UNIT, currency: 'USD', date }
}

function send(res, status, body) {
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'GET,POST,OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type',
  })
  res.end(JSON.stringify(body))
}

function isAllowedBrowserOrigin(origin) {
  return !origin || origin === new URL(BASE_URL).origin
}

function hasBridgeSecret(req) {
  const provided = String(req.headers['x-ergouzi-bridge'] || '')
  if (!provided || provided.length !== BRIDGE_SECRET.length) return false
  return crypto.timingSafeEqual(Buffer.from(provided), Buffer.from(BRIDGE_SECRET))
}

function readBody(req, maxBytes = 16384) {
  return new Promise((resolve, reject) => {
    let size = 0
    const chunks = []
    req.on('data', (chunk) => {
      size += chunk.length
      if (size > maxBytes) {
        reject(new Error('request body too large'))
        req.destroy()
        return
      }
      chunks.push(chunk)
    })
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')))
    req.on('error', reject)
  })
}

async function importToken(req) {
  const body = JSON.parse(await readBody(req))
  const accessToken = String(body.accessToken || body.access_token || '').trim()
  if (accessToken.length < 16 || accessToken.length > 8192) throw new Error('access token is invalid')

  let current = config
  if (!current) {
    try { current = loadConfig() } catch { current = { accessToken: '', sessionId: '', cookie: '', accessExpiresAt: 0 } }
  }
  const sessionId = body.sessionId === undefined && body.session_id === undefined
    ? (current.sessionId || '')
    : String(body.sessionId || body.session_id || '').trim()
  const cookie = body.cookie === undefined
    ? (current.cookie || '')
    : String(body.cookie || '').trim()
  const accessExpiresAt = importedExpiry(accessToken, body.accessExpiresAt || body.access_expires_at)
  const next = { accessToken, sessionId, cookie, accessExpiresAt }
  saveConfig(next)
  config = next
  return { ok: true, accessExpiresAt }
}

function startServer() {
  const server = http.createServer(async (req, res) => {
    const origin = req.headers.origin
    if (req.method === 'OPTIONS') {
      if (!isAllowedBrowserOrigin(origin)) return send(res, 403, { ok: false, code: 'ORIGIN_DENIED' })
      return send(res, 204, {})
    }
    try {
      if (req.url === '/health') return send(res, 200, { ok: true })
      if (req.method === 'POST' && req.url === '/internal/import-token') {
        if (!isAllowedBrowserOrigin(origin) || !hasBridgeSecret(req)) return send(res, 403, { ok: false, code: 'BRIDGE_DENIED' })
        return send(res, 200, await importToken(req))
      }
      if (req.url === '/api/summary') return send(res, 200, await summary())
      if (req.url === '/api/balance') return send(res, 200, await balance())
      if (req.url === '/api/today-usage') return send(res, 200, await todayUsageWithCache())
      return send(res, 404, { ok: false, code: 'NOT_FOUND', error: 'not found' })
    } catch (err) {
      send(res, 502, { ok: false, code: err.code || 'UPSTREAM', error: String(err.message || err) })
    }
  })
  server.listen(PORT, BIND_HOST, () => console.log('Ergouzi account agent listening on ' + endpoint(BIND_HOST, PORT)))
}

async function setup() {
  const rl = readline.createInterface({ input, output })
  const accessToken = (await rl.question('一次性访问令牌: ')).trim()
  const sessionId = (await rl.question('X-Auth-Session（可选，刷新所需）: ')).trim()
  const cookie = (await rl.question('Cookie（可选；若站点刷新需要，粘贴一次）: ')).trim()
  const expires = (await rl.question('令牌过期时间 Unix 秒（未知填 0）: ')).trim()
  rl.close()
  if (!accessToken) throw new Error('access token is required')
  saveConfig({ accessToken, sessionId, cookie, accessExpiresAt: importedExpiry(accessToken, expires) })
  console.log('已加密保存到 ' + CONFIG_FILE)
}

if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) {
  if (process.argv[2] === 'setup') await setup()
  else startServer()
}
