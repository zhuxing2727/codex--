import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import crypto from 'node:crypto'
import { spawn } from 'node:child_process'
import { pathToFileURL } from 'node:url'

const BASE_URL = process.env.ERGOUZI_BASE_URL || 'https://ergouzi.life'
const WALLET_URL = process.env.ERGOUZI_WALLET_URL || new URL('/wallet', BASE_URL).href
const AGENT_URL = process.env.ERGOUZI_AGENT_URL || 'http://127.0.0.1:17891'
const CDP_PORT = Number(process.env.ERGOUZI_CDP_PORT || 17929)
const CDP_BASE = 'http://127.0.0.1:' + CDP_PORT
const CONFIG_DIR = process.env.ERGOUZI_AGENT_HOME || path.join(process.env.APPDATA || os.homedir(), 'ergouzi-account-agent')
const BRIDGE_SECRET_FILE = path.join(CONFIG_DIR, 'bridge.secret')
const PROFILE_DIR = process.env.ERGOUZI_WALLET_PROFILE || path.join(CONFIG_DIR, 'wallet-browser')
const LOCK_FILE = path.join(CONFIG_DIR, 'wallet-token-sync.lock')
const RELOAD_INTERVAL_MS = Number(process.env.ERGOUZI_WALLET_RELOAD_MS || 180000)
const STORAGE_SCAN_INTERVAL_MS = 5000

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

export function tokenExpiry(token) {
  try {
    const part = String(token).split('.')[1]
    const payload = JSON.parse(Buffer.from(part, 'base64url').toString('utf8'))
    const expiry = Number(payload.exp || 0)
    return Number.isFinite(expiry) && expiry > 0 ? expiry : 0
  } catch {
    return 0
  }
}

export function extractBearerToken(value) {
  if (typeof value !== 'string') return null
  const trimmed = value.trim()
  const match = trimmed.match(/^Bearer\s+([^\s]+)$/i)
  const token = (match ? match[1] : trimmed)
  if (token.length < 16 || token.length > 8192 || /\s/.test(token)) return null
  if (!/^[A-Za-z0-9._~+/=-]+$/.test(token)) return null
  return token
}

function tokenFromStructuredValue(value, key = '', depth = 0) {
  if (depth > 5 || value === null || value === undefined) return null
  if (typeof value === 'string') {
    if (!/(?:access[_-]?token|authorization|auth(?:entication)?|bearer|token|credential)/i.test(key)) return null
    return extractBearerToken(value)
  }
  if (Array.isArray(value)) {
    for (const item of value) {
      const token = tokenFromStructuredValue(item, key, depth + 1)
      if (token) return token
    }
    return null
  }
  if (typeof value !== 'object') return null

  const entries = Object.entries(value)
  const preferred = entries.sort(([left], [right]) => {
    const score = (name) => /access[_-]?token|authorization|auth[_-]?token/i.test(name) ? 0 : 1
    return score(left) - score(right)
  })
  for (const [childKey, childValue] of preferred) {
    const token = tokenFromStructuredValue(childValue, childKey, depth + 1)
    if (token) return token
  }
  return null
}

export function findTokenInStorage(entries) {
  if (!Array.isArray(entries)) return null
  for (const entry of entries) {
    if (!Array.isArray(entry) || entry.length < 2) continue
    const [key, rawValue] = entry
    const keyText = String(key || '')
    let value = rawValue
    if (typeof rawValue === 'string') {
      try { value = JSON.parse(rawValue) } catch {}
    }
    const token = tokenFromStructuredValue(value, keyText)
    if (token) return token
  }
  return null
}

export function isErgouziUrl(value) {
  try {
    const actual = new URL(value)
    const expected = new URL(BASE_URL)
    return actual.origin === expected.origin && actual.protocol === 'https:'
  } catch {
    return false
  }
}

export function getHeader(headers, name) {
  if (!headers || typeof headers !== 'object') return ''
  const wanted = String(name).toLowerCase()
  for (const [key, value] of Object.entries(headers)) {
    if (String(key).toLowerCase() === wanted) return String(value || '')
  }
  return ''
}

export function findSessionId(value, depth = 0) {
  if (depth > 5 || value === null || value === undefined) return ''
  if (Array.isArray(value)) {
    for (const item of value) {
      const result = findSessionId(item, depth + 1)
      if (result) return result
    }
    return ''
  }
  if (typeof value !== 'object') return ''
  for (const [key, child] of Object.entries(value)) {
    if (String(key).toLowerCase() === 'sid' && typeof child === 'string' && child.length > 0 && child.length <= 512) return child
    const result = findSessionId(child, depth + 1)
    if (result) return result
  }
  return ''
}

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

function acquireLock() {
  fs.mkdirSync(CONFIG_DIR, { recursive: true })
  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      const fd = fs.openSync(LOCK_FILE, 'wx')
      fs.writeFileSync(fd, String(process.pid), 'utf8')
      return fd
    } catch (error) {
      if (error.code !== 'EEXIST') throw error
      try {
        const pid = Number(fs.readFileSync(LOCK_FILE, 'utf8').trim())
        process.kill(pid, 0)
        return null
      } catch {
        try { fs.unlinkSync(LOCK_FILE) } catch {}
      }
    }
  }
  return null
}

function releaseLock(fd) {
  try { fs.closeSync(fd) } catch {}
  try { fs.unlinkSync(LOCK_FILE) } catch {}
}

async function getJson(url) {
  const response = await fetch(url)
  if (!response.ok) throw new Error('CDP HTTP ' + response.status)
  return response.json()
}

function findEdgeExecutable() {
  const candidates = [
    path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    path.join(process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    path.join(process.env.LOCALAPPDATA || '', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  ]
  return candidates.find((candidate) => fs.existsSync(candidate)) || null
}

async function waitForCdp(timeoutMs = 15000) {
  const deadline = Date.now() + timeoutMs
  let lastError
  while (Date.now() < deadline) {
    try { return await getJson(CDP_BASE + '/json/version') } catch (error) { lastError = error }
    await sleep(250)
  }
  throw lastError || new Error('Edge DevTools endpoint did not start')
}

async function ensureCdpBrowser() {
  try {
    return await waitForCdp(500)
  } catch {}

  const edge = findEdgeExecutable()
  if (!edge) throw new Error('Microsoft Edge was not found')
  fs.mkdirSync(PROFILE_DIR, { recursive: true })
  const args = [
    '--remote-debugging-port=' + CDP_PORT,
    '--remote-debugging-address=127.0.0.1',
    '--remote-allow-origins=*',
    '--user-data-dir=' + PROFILE_DIR,
    '--profile-directory=Default',
    '--no-first-run',
    '--no-default-browser-check',
    '--new-window',
    WALLET_URL,
  ]
  const child = spawn(edge, args, { detached: true, stdio: 'ignore', windowsHide: false })
  child.unref()
  return waitForCdp()
}

async function listPages() {
  return getJson(CDP_BASE + '/json/list')
}

async function choosePage() {
  const pages = await listPages()
  const expectedOrigin = new URL(BASE_URL).origin
  const walletPages = pages.filter((page) => page.type === 'page' && (() => {
    try { return new URL(page.url).origin === expectedOrigin } catch { return false }
  })()).sort((left, right) => {
    const score = (value) => {
      try {
        const pathname = new URL(value).pathname
        return pathname === '/wallet' || pathname === '/billing-analysis' ? 0 : pathname === '/sign-in' ? 2 : 1
      } catch { return 3 }
    }
    return score(left.url) - score(right.url)
  })
  const page = walletPages[0]
  if (!page || !page.webSocketDebuggerUrl) throw new Error('wallet page target was not found on the sync browser')
  return page
}

class CdpConnection {
  constructor(url) {
    this.url = url
    this.nextId = 0
    this.pending = new Map()
    this.handlers = new Map()
  }

  async connect() {
    this.socket = new WebSocket(this.url)
    this.socket.onmessage = (event) => this.#onMessage(event.data)
    await new Promise((resolve, reject) => {
      const onOpen = () => { cleanup(); resolve() }
      const onError = (event) => { cleanup(); reject(new Error('CDP WebSocket failed')) }
      const cleanup = () => {
        this.socket.removeEventListener('open', onOpen)
        this.socket.removeEventListener('error', onError)
      }
      this.socket.addEventListener('open', onOpen)
      this.socket.addEventListener('error', onError)
    })
  }

  #onMessage(raw) {
    let message
    try { message = JSON.parse(raw) } catch { return }
    if (message.id) {
      const pending = this.pending.get(message.id)
      if (!pending) return
      this.pending.delete(message.id)
      if (message.error) pending.reject(new Error(message.error.message || 'CDP command failed'))
      else pending.resolve(message.result || {})
      return
    }
    const listeners = this.handlers.get(message.method) || []
    for (const listener of listeners) listener(message.params || {})
  }

  on(method, listener) {
    const listeners = this.handlers.get(method) || []
    listeners.push(listener)
    this.handlers.set(method, listeners)
  }

  send(method, params = {}) {
    const id = ++this.nextId
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id)
        reject(new Error('CDP command timed out: ' + method))
      }, 15000)
      this.pending.set(id, {
        resolve: (value) => { clearTimeout(timer); resolve(value) },
        reject: (error) => { clearTimeout(timer); reject(error) },
      })
      this.socket.send(JSON.stringify({ id, method, params }))
    })
  }

  close() {
    try { this.socket?.close() } catch {}
  }
}

async function storageEntries(cdp) {
  const expression = `(() => {
    const read = (storage) => {
      const result = []
      try { for (let i = 0; i < storage.length; i += 1) { const key = storage.key(i); result.push([key, storage.getItem(key)]) } } catch {}
      return result
    }
    return [...read(window.localStorage), ...read(window.sessionStorage)]
  })()`
  const result = await cdp.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })
  return result.result?.value || []
}

async function browserCookieHeader(cdp) {
  const result = await cdp.send('Network.getCookies', { urls: [BASE_URL, WALLET_URL] })
  const expectedHost = new URL(BASE_URL).hostname
  return (result.cookies || [])
    .filter((cookie) => {
      const domain = String(cookie.domain || '').replace(/^\./, '')
      return domain === expectedHost || expectedHost.endsWith('.' + domain)
    })
    .map((cookie) => String(cookie.name) + '=' + String(cookie.value))
    .join('; ')
}

function isAuthResponse(value) {
  try {
    const pathname = new URL(value).pathname
    return /^\/api\/(?:user\/(?:auth\/|login)|oauth\/|token)/i.test(pathname)
  } catch {
    return false
  }
}

async function postToken(secret, token, sessionId = '', cookie = '') {
  const accessExpiresAt = tokenExpiry(token) || Math.floor(Date.now() / 1000) + 300
  const payload = { accessToken: token, accessExpiresAt }
  if (sessionId) payload.sessionId = sessionId
  if (cookie) payload.cookie = cookie
  const response = await fetch(AGENT_URL + '/internal/import-token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Ergouzi-Bridge': secret },
    body: JSON.stringify(payload),
  })
  const body = await response.json().catch(() => ({}))
  if (!response.ok || !body.ok) throw new Error(String(body.error || body.code || 'local agent rejected token'))
  return body
}

async function run() {
  const lock = acquireLock()
  if (lock === null) return
  const secret = ensureBridgeSecret()
  let cdp
  let latestSessionId = ''
  let latestCookie = ''
  let lastImported = null
  let candidate = null
  let importing = false
  let stopped = false

  const queueToken = (token, sessionId = latestSessionId, cookie = latestCookie) => {
    if (!token) return
    if (lastImported && token === lastImported.token && sessionId === lastImported.sessionId && cookie === lastImported.cookie) return
    candidate = { token, sessionId, cookie }
    if (!importing) void drainImportQueue()
  }

  const drainImportQueue = async () => {
    importing = true
    while (!stopped && candidate) {
      const current = candidate
      try {
        await postToken(secret, current.token, current.sessionId, current.cookie)
        lastImported = current
        candidate = candidate?.token === current.token && candidate?.sessionId === current.sessionId && candidate?.cookie === current.cookie ? null : candidate
        console.log('钱包页面令牌已自动同步到本地代理')
      } catch (error) {
        console.error('钱包令牌同步等待本地代理：' + String(error.message || error))
        await sleep(5000)
      }
    }
    importing = false
  }

  const scan = async () => {
    if (!cdp || stopped) return
    try {
      const entries = await storageEntries(cdp)
      try { latestCookie = await browserCookieHeader(cdp) } catch {}
      queueToken(findTokenInStorage(entries))
    } catch {}
  }

  try {
    await ensureCdpBrowser()
    const page = await choosePage()
    cdp = new CdpConnection(page.webSocketDebuggerUrl)
    await cdp.connect()
    await cdp.send('Network.enable')
    await cdp.send('Runtime.enable')
    await cdp.send('Page.enable')
    cdp.on('Network.requestWillBeSent', (event) => {
      if (!isErgouziUrl(event.request?.url)) return
      const sessionId = getHeader(event.request?.headers, 'x-auth-session')
      if (sessionId) latestSessionId = sessionId
      const cookie = getHeader(event.request?.headers, 'cookie')
      if (cookie) latestCookie = cookie
      const token = extractBearerToken(getHeader(event.request?.headers, 'authorization'))
      if (token) queueToken(token)
    })
    const authResponses = new Map()
    cdp.on('Network.responseReceived', (event) => {
      if (isErgouziUrl(event.response?.url) && isAuthResponse(event.response.url)) authResponses.set(event.requestId, event.response.url)
    })
    cdp.on('Network.loadingFinished', async (event) => {
      const url = authResponses.get(event.requestId)
      if (!url) return
      authResponses.delete(event.requestId)
      try {
        const result = await cdp.send('Network.getResponseBody', { requestId: event.requestId })
        const rawBody = result.base64Encoded
          ? Buffer.from(String(result.body || ''), 'base64').toString('utf8')
          : String(result.body || '')
        const body = JSON.parse(rawBody)
        const sessionId = findSessionId(body)
        if (sessionId) latestSessionId = sessionId
        const token = tokenFromStructuredValue(body, 'response')
        if (token) queueToken(token, sessionId || latestSessionId, latestCookie)
      } catch {}
    })
    cdp.on('Page.loadEventFired', () => { void scan() })

    if (new URL(page.url).origin !== new URL(BASE_URL).origin) {
      await cdp.send('Page.navigate', { url: WALLET_URL })
    } else {
      await cdp.send('Page.reload', { ignoreCache: false })
    }
    await scan()
    const storageTimer = setInterval(() => { void scan() }, STORAGE_SCAN_INTERVAL_MS)
    const reloadTimer = setInterval(() => {
      if (!stopped) void cdp.send('Page.reload', { ignoreCache: false }).catch(() => {})
    }, RELOAD_INTERVAL_MS)

    console.log('钱包令牌同步已运行：请在弹出的 Edge 钱包页面完成登录；以后令牌会自动更新')
    await new Promise((resolve) => {
      const stop = () => { stopped = true; clearInterval(storageTimer); clearInterval(reloadTimer); resolve() }
      process.once('SIGINT', stop)
      process.once('SIGTERM', stop)
    })
  } finally {
    stopped = true
    cdp?.close()
    releaseLock(lock)
  }
}

if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) {
  run().catch((error) => {
    console.error('钱包令牌同步启动失败：' + String(error.message || error))
    process.exitCode = 1
  })
}
