import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const rootDir = path.dirname(fileURLToPath(import.meta.url))
const defaultConfigFile = path.join(rootDir, 'ergouzi.config.json')
const configFile = process.env.ERGOUZI_CONFIG_FILE || defaultConfigFile
const defaults = {
  version: '0.2.16',
  githubRepo: 'zhuxing2727/codex--',
  githubReleasesUrl: 'https://github.com/zhuxing2727/codex--/releases',
  updateProxy: '',
  baseUrl: 'https://ergouzi.life',
  bindHost: '127.0.0.1',
  agentPort: 17891,
  tokenSyncPort: 17892,
  cdpPort: 17929,
  quotaPerUnit: 500000,
  walletPath: '/wallet',
  agentHomeName: 'ergouzi-account-agent',
  overlayStateName: 'DeepSeekWhaleOverlay',
  tokenRefreshCooldownMs: 60000,
  walletReloadIntervalMs: 180000,
  storageScanIntervalMs: 5000,
  requestTimeoutMs: 25000,
  cdpTimeoutMs: 15000,
  bridgeReloadWaitMs: 5000,
  walletProfileName: 'wallet-browser',
  trayMonitorIntervalMs: 5000,
  tokenRetryIntervalMs: 5000,
  cdpPollIntervalMs: 250,
  pageReloadWaitMs: 1500,
  walletOpenWaitMs: 500,
  cdpProbeTimeoutMs: 500,
  tokenImportPollIntervalMs: 500,
  tokenRefreshWaitMs: 1500,
  balanceTtlMs: 25000,
  widgetRefreshMs: 60000,
  widgetBubbleMs: 5000,
}

function number(value, fallback) {
  const parsed = Number(value)
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback
}

function readJson(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')) } catch { return {} }
}

const fileValues = readJson(configFile)
const env = process.env
const config = {
  ...defaults,
  ...fileValues,
  baseUrl: env.ERGOUZI_BASE_URL || fileValues.baseUrl || defaults.baseUrl,
  bindHost: env.ERGOUZI_BIND_HOST || fileValues.bindHost || defaults.bindHost,
  agentPort: number(env.ERGOUZI_AGENT_PORT || fileValues.agentPort, defaults.agentPort),
  tokenSyncPort: number(env.ERGOUZI_TOKEN_SYNC_PORT || fileValues.tokenSyncPort, defaults.tokenSyncPort),
  cdpPort: number(env.ERGOUZI_CDP_PORT || fileValues.cdpPort, defaults.cdpPort),
  quotaPerUnit: number(env.ERGOUZI_QUOTA_PER_UNIT || fileValues.quotaPerUnit, defaults.quotaPerUnit),
  tokenRefreshCooldownMs: number(env.ERGOUZI_TOKEN_SYNC_RELOAD_COOLDOWN_MS || fileValues.tokenRefreshCooldownMs, defaults.tokenRefreshCooldownMs),
  walletReloadIntervalMs: number(env.ERGOUZI_WALLET_RELOAD_MS || fileValues.walletReloadIntervalMs, defaults.walletReloadIntervalMs),
  storageScanIntervalMs: number(env.ERGOUZI_STORAGE_SCAN_MS || fileValues.storageScanIntervalMs, defaults.storageScanIntervalMs),
  requestTimeoutMs: number(env.ERGOUZI_REQUEST_TIMEOUT_MS || fileValues.requestTimeoutMs, defaults.requestTimeoutMs),
  cdpTimeoutMs: number(env.ERGOUZI_CDP_TIMEOUT_MS || fileValues.cdpTimeoutMs, defaults.cdpTimeoutMs),
  bridgeReloadWaitMs: number(env.ERGOUZI_BRIDGE_RELOAD_WAIT_MS || fileValues.bridgeReloadWaitMs, defaults.bridgeReloadWaitMs),
  trayMonitorIntervalMs: number(env.ERGOUZI_TRAY_MONITOR_MS || fileValues.trayMonitorIntervalMs, defaults.trayMonitorIntervalMs),
  tokenRetryIntervalMs: number(env.ERGOUZI_TOKEN_RETRY_MS || fileValues.tokenRetryIntervalMs, defaults.tokenRetryIntervalMs),
  cdpPollIntervalMs: number(env.ERGOUZI_CDP_POLL_MS || fileValues.cdpPollIntervalMs, defaults.cdpPollIntervalMs),
  pageReloadWaitMs: number(env.ERGOUZI_PAGE_RELOAD_WAIT_MS || fileValues.pageReloadWaitMs, defaults.pageReloadWaitMs),
  walletOpenWaitMs: number(env.ERGOUZI_WALLET_OPEN_WAIT_MS || fileValues.walletOpenWaitMs, defaults.walletOpenWaitMs),
  cdpProbeTimeoutMs: number(env.ERGOUZI_CDP_PROBE_TIMEOUT_MS || fileValues.cdpProbeTimeoutMs, defaults.cdpProbeTimeoutMs),
  tokenImportPollIntervalMs: number(env.ERGOUZI_TOKEN_IMPORT_POLL_MS || fileValues.tokenImportPollIntervalMs, defaults.tokenImportPollIntervalMs),
  tokenRefreshWaitMs: number(env.ERGOUZI_TOKEN_REFRESH_WAIT_MS || fileValues.tokenRefreshWaitMs, defaults.tokenRefreshWaitMs),
}

export const CONFIG = Object.freeze(config)
export const CONFIG_FILE = configFile
export const CONFIG_DIR = process.env.ERGOUZI_AGENT_HOME || path.join(process.env.APPDATA || os.homedir(), CONFIG_DIR_NAME())
export const WALLET_PROFILE_DIR = process.env.ERGOUZI_WALLET_PROFILE || path.join(CONFIG_DIR, CONFIG.walletProfileName)
export const OVERLAY_STATE_DIR = process.env.ERGOUZI_OVERLAY_STATE_HOME || path.join(process.env.APPDATA || os.homedir(), CONFIG.overlayStateName)

function CONFIG_DIR_NAME() { return CONFIG.agentHomeName }

export function endpoint(host, port) { return 'http://' + host + ':' + port }
export function walletUrl() { return new URL(CONFIG.walletPath, CONFIG.baseUrl).href }
