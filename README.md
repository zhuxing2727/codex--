# Ergouzi 小鲸鱼余额挂件

适用于 Windows 的 CC Switch / Codex 余额挂件迁移版。

## 功能

- 透明桌面悬浮窗，支持拖动、边缘吸附和位置记忆。
- 显示 `ergouzi余额` 和今日用量。
- 中文/日文角色语音，支持单选、多选、循环播放和音量设置。
- 独立 Edge 钱包页面令牌同步。
- 内置 Node.js 的单文件 Windows 安装程序。

## 快速安装

从仓库源码运行构建脚本即可生成 `ErgouziWhaleWidget-Setup.exe`。程序会安装到 `%LOCALAPPDATA%\ErgouziWhaleWidget`，并创建桌面快捷方式 `Ergouzi 小鲸鱼.lnk`。

首次启动后，在弹出的 Ergouzi 钱包页面完成登录一次即可。也可以下载 `ErgouziWhaleWidget-Portable.zip`，解压后运行 `start-whale-overlay.cmd`。

## CC Switch 配置

1. 双击桌面快捷方式，等待本地代理启动。
2. 在 CC Switch 的 Ergouzi Codex provider 中打开 Usage Query。
3. Usage Base URL 设置为：

```text
http://127.0.0.1:17891
```

4. 查询方式选择 `Custom`。
5. 粘贴 [`ccswitch-ergouzi-agent-usage-query.js`](./ccswitch-ergouzi-agent-usage-query.js) 的完整内容并点击 `Test Script`。

本地代理会管理账户认证，Usage Query 不需要反复粘贴短期令牌。

## 源码启动

```powershell
git clone https://github.com/zhuxing2727/codex--.git
cd codex--
.\start-whale-overlay.cmd
```

手动配置账户令牌：

```powershell
node .\ergouzi-account-agent.mjs setup
node .\ergouzi-account-agent.mjs
```

代理接口：

```text
GET http://127.0.0.1:17891/health
GET http://127.0.0.1:17891/api/balance
GET http://127.0.0.1:17891/api/today-usage
```

## 构建安装包

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build-installer.ps1
```

输出：

```text
dist\ErgouziWhaleWidget-Setup.exe
dist\ErgouziWhaleWidget-Portable.zip
```

安装包包含图片、中文/日文语音、PowerShell 悬浮窗、本地代理和内置 Node.js，不依赖构建电脑的用户名或固定盘符。

## 适配其他中转站

当前版本默认适配 Ergouzi。只有 `/v1` 推理接口的中转站不能直接提供余额挂件数据。适配余额功能需要对方提供余额接口、今日用量接口、认证请求头格式和脱敏 JSON 示例。

通常修改：

- `ergouzi-account-agent.mjs`：基础地址、认证头、余额接口、用量接口和字段解析。
- `ergouzi-wallet-token-sync.mjs`：对方有钱包网页且需要自动同步令牌时才修改。
- `ccswitch-ergouzi-usage-query.js`：不使用本地代理时的直接查询脚本。
- `whale-overlay.ps1`：本地代理地址或端口变化时修改。

本地代理保持以下返回结构即可：

```json
{"ok":true,"totalBalance":12.34,"currency":"USD"}
```

```json
{"ok":true,"amount":0.56,"currency":"USD"}
```

## 安全说明

不要上传真实 API Key、Bearer Token、Cookie、`X-Auth-Session`、`.env`、日志或旧快捷方式。也不要上传：

```text
%APPDATA%\ergouzi-account-agent\config.json
%APPDATA%\ergouzi-account-agent\bridge.secret
%APPDATA%\ergouzi-account-agent\wallet-browser\
```

项目的 `.gitignore` 已排除认证相关文件、本机验证记录、回滚目录和临时文件。每台电脑都应使用自己的 Ergouzi 账号登录。

## 测试

```powershell
node --check .\lib\index.js
node --check .\ergouzi-account-agent.mjs
node --check .\ergouzi-wallet-token-sync.mjs
node --check .\ccswitch-ergouzi-usage-query.js
node --check .\ccswitch-ergouzi-agent-usage-query.js
powershell -ExecutionPolicy Bypass -File .\tools\build-installer.ps1 -SkipRuntimeDownload
```
