# Ergouzi 小鲸鱼余额挂件

适用于 Windows 的 CC Switch / Codex 余额挂件迁移版。

## 功能

- 透明桌面悬浮窗，支持拖动、边缘吸附和位置记忆。
- 显示 `ergouzi余额` 和今日用量。
- 中文/日文角色语音，支持单选、多选、循环播放和音量设置。
- 独立 Edge 钱包页面令牌同步。
- 统一托盘后台，负责账户代理、钱包同步、悬浮窗和钱包页面生命周期。
- 内置 Node.js 的 Windows 安装程序，支持自定义安装目录、桌面图标和完整卸载。
- 托盘菜单支持从 GitHub 检查更新，并可覆盖当前目录或卸载后改装到其他目录。

## 快速安装

从仓库源码运行构建脚本即可生成 `ErgouziWhaleWidget-Setup.exe`。安装时用户选择父文件夹，程序会固定在该文件夹内部创建 `m3QAQ` 作为实际安装目录，并创建桌面快捷方式 `余额挂件.lnk`。快捷方式使用缩小后的角色图标 `assets/balance-widget.ico`。安装目录内的 `卸载余额挂件.exe` 会停止组件、删除整个 `m3QAQ` 目录、删除快捷方式，并清理挂件状态和本地账户代理数据；开始菜单“余额挂件”文件夹也会生成同名卸载快捷方式。旧版 `Uninstall-ErgouziWhaleWidget.exe` 不再打包。

钱包登录页使用安装目录内独立的 `wallet-browser` Edge 配置，不再复用旧安装的浏览器自动填充账号或密码。卸载器只接受目录名为 `m3QAQ` 且包含有效安装标记的路径，会先停止相关 Edge/Node/PowerShell 进程，再从安装目录外启动清理脚本，反复删除整个 `m3QAQ` 目录，直到目录消失；不会删除用户选择的父文件夹或磁盘根目录。

首次启动后，托盘后台会自动打开 Ergouzi 钱包页面，在页面完成登录一次即可。托盘菜单可重新打开登录页、显示挂件、重启组件、检查更新或退出后台。也可以下载 `ErgouziWhaleWidget-Portable.zip`，解压后运行 `ErgouziWhaleWidget.exe`（旧版 `start-whale-overlay.cmd` 仍兼容）。

更新检查使用仓库 `https://github.com/zhuxing2727/codex--` 的最新 Release。检测到新版本后，“是”会下载 Windows 安装包并覆盖当前安装目录；“否”会先完全卸载，再打开安装器选择新的安装目录；“取消”则不执行更新。程序会自动尝试显式代理、Windows 系统代理/PAC 和直连；如需手动指定，可设置 `ERGOUZI_UPDATE_PROXY` 或 `HTTPS_PROXY`，值使用系统提供的代理地址。

## CC Switch 配置

1. 双击桌面快捷方式，托盘后台会自动启动本地代理、钱包同步和悬浮窗。
2. 在 CC Switch 的 Ergouzi Codex provider 中打开 Usage Query。
3. Usage Base URL 设置为：

```text
http://127.0.0.1:17891
```

4. 查询方式选择 `Custom`。
5. 粘贴 [`ccswitch-ergouzi-agent-usage-query.js`](./ccswitch-ergouzi-agent-usage-query.js) 的完整内容并点击 `Test Script`。

本地代理会管理账户认证，Usage Query 不需要反复粘贴短期令牌。
本地统一查询接口为 `GET http://127.0.0.1:17891/api/summary`，一次返回余额和今日已用；旧的 `/api/balance` 与 `/api/today-usage` 接口仍保留兼容。

如果上游返回认证过期，账户代理会通过本机桥接通知钱包同步页面刷新一次，重新取得令牌后自动重试一次。若钱包会话也已失效，挂件会提示重新登录；网络瞬时失败时会继续显示最近一次成功的今日用量。

PowerShell 悬浮窗会合并快速点击产生的刷新请求，最后一次点击会在当前请求完成后继续执行。气泡背景、尾巴、圆点和文字面板由统一状态管理并保持常驻显示。

## 源码启动

运行参数统一放在 `ergouzi.config.json`，包括上游地址、本地监听地址和端口、CDP 端口、数据目录名称、超时及轮询周期。环境变量仍可覆盖对应配置，例如 `ERGOUZI_BASE_URL`、`ERGOUZI_AGENT_PORT`、`ERGOUZI_TOKEN_SYNC_PORT`、`ERGOUZI_CDP_PORT` 和 `ERGOUZI_AGENT_HOME`，便于隔离测试和多实例部署。

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
GET http://127.0.0.1:17891/api/summary
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

安装器也支持更新器传入 `--target "D:\\Apps\\m3QAQ"` 或父文件夹路径；传入父文件夹时仍会自动落到其下的 `m3QAQ`，用于无须再次选择目录的覆盖安装。即使父文件夹是 `C:\\` 或 `D:\\`，卸载器也只会删除其中的 `m3QAQ` 子目录。

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
<安装目录>\wallet-browser\
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
