using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Net.Cache;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Text.RegularExpressions;

internal static class ErgouziTrayHost
{
    private const string UninstallerFileName = "\u5378\u8F7D\u4F59\u989D\u6302\u4EF6.exe";
    private static string MutexName;
    private static string SignalName;
    private static string bindHost;
    private static int agentPort;
    private static int tokenSyncPort;
    private static string agentHomeName;
    private static int monitorIntervalMs;
    private static string productName;
    private static string productVersion;
    private static string githubRepo;
    private static string githubReleasesUrl;
    private static string updateProxy;
    private static readonly object Gate = new object();
    private static readonly System.Collections.Generic.List<Process> Children = new System.Collections.Generic.List<Process>();
    private static NotifyIcon tray;
    private static Mutex mutex;
    private static EventWaitHandle signal;
    private static bool stopping;
    private static string root;

    [STAThread]
    private static void Main()
    {
        root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        LoadConfig();
        bool created;
        mutex = new Mutex(true, MutexName, out created);
        if (!created)
        {
            try { using (var wake = EventWaitHandle.OpenExisting(SignalName)) wake.Set(); } catch { }
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        tray = new NotifyIcon
        {
            Text = productName,
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        tray.DoubleClick += delegate { StartOrWakeOverlay(); };
        var signalTimer = new System.Windows.Forms.Timer { Interval = 300 };
        signalTimer.Tick += delegate { if (signal.WaitOne(0)) StartOrWakeOverlay(); };
        signalTimer.Start();
        StartComponents();
        var monitorTimer = new System.Windows.Forms.Timer { Interval = monitorIntervalMs };
        monitorTimer.Tick += delegate { EnsureComponents(); };
        monitorTimer.Start();
        Application.ApplicationExit += delegate { StopComponents(); };
        Application.Run();
    }

    private static void LoadConfig()
    {
        string json = "";
        try { json = File.ReadAllText(Path.Combine(root, "ergouzi.config.json")); } catch { }
        bindHost = GetConfig(json, "bindHost", "127.0.0.1");
        agentPort = IntConfig(json, "agentPort", 17891);
        tokenSyncPort = IntConfig(json, "tokenSyncPort", 17892);
        agentHomeName = GetConfig(json, "agentHomeName", "ergouzi-account-agent");
        monitorIntervalMs = IntConfig(json, "trayMonitorIntervalMs", 5000);
        productName = GetConfig(json, "productName", "ErgouziWhaleWidget");
        productVersion = GetConfig(json, "version", "0.0.0");
        githubRepo = GetConfig(json, "githubRepo", "zhuxing2727/codex--");
        githubReleasesUrl = GetConfig(json, "githubReleasesUrl", "https://github.com/zhuxing2727/codex--/releases");
        updateProxy = GetConfig(json, "updateProxy", "");
        if (String.IsNullOrWhiteSpace(updateProxy)) updateProxy = Environment.GetEnvironmentVariable("ERGOUZI_UPDATE_PROXY") ?? Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("HTTP_PROXY") ?? "";
        MutexName = "Local\\" + productName + "TrayHost";
        SignalName = "Local\\" + productName + "TraySignal";
    }

    private static string GetConfig(string json, string key, string fallback)
    {
        var match = Regex.Match(json ?? "", "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : fallback;
    }

    private static int IntConfig(string json, string key, int fallback)
    {
        var match = Regex.Match(json ?? "", "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*(\\d+)", RegexOptions.CultureInvariant);
        int value;
        return match.Success && Int32.TryParse(match.Groups[1].Value, out value) ? value : fallback;
    }

    private static ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示挂件", null, delegate { StartOrWakeOverlay(); });
        menu.Items.Add("更新令牌", null, delegate { ShowTokenUpdateDialog(); });
        menu.Items.Add("打开钱包登录页", null, delegate { PostControl("/internal/open-wallet"); });
        menu.Items.Add("重启账户代理", null, delegate { RestartComponent("ergouzi-account-agent.mjs"); });
        menu.Items.Add("重启钱包同步", null, delegate { RestartComponent("ergouzi-wallet-token-sync.mjs"); });
        menu.Items.Add("检查更新", null, delegate { CheckForUpdates(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出后台", null, delegate { Application.Exit(); });
        return menu;
    }

    private static string NodePath()
    {
        string bundled = Path.Combine(root, "runtime", "node.exe");
        return File.Exists(bundled) ? bundled : "node.exe";
    }

    private static void StartComponents()
    {
        StartChild("ergouzi-account-agent.mjs", hidden: true);
        StartChild("ergouzi-wallet-token-sync.mjs", hidden: true);
        StartOrWakeOverlay();
    }

    private static void EnsureComponents()
    {
        StartChild("ergouzi-account-agent.mjs", hidden: true);
        StartChild("ergouzi-wallet-token-sync.mjs", hidden: true);
        StartOrWakeOverlay();
    }

    private static void StartOrWakeOverlay()
    {
        string script = Path.Combine(root, "whale-overlay.ps1");
        if (!File.Exists(script)) return;
        StartChild(script, hidden: true, powershell: true);
    }

    private static void StartChild(string file, bool hidden, bool powershell = false)
    {
        string path = Path.IsPathRooted(file) ? file : Path.Combine(root, file);
        string exe = powershell ? "powershell.exe" : NodePath();
        string args = powershell
            ? "-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + path + "\""
            : "\"" + path + "\"";
        lock (Gate)
        {
            if (Children.Any(p => !p.HasExited && p.StartInfo.Arguments.IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0)) return;
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = hidden,
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            };
            if (String.Equals(file, "ergouzi-wallet-token-sync.mjs", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.EnvironmentVariables["ERGOUZI_WALLET_PROFILE"] = Path.Combine(root, "wallet-browser");
            }
            var child = Process.Start(startInfo);
            if (child != null)
            {
                child.EnableRaisingEvents = true;
                child.Exited += delegate { lock (Gate) { Children.Remove(child); } };
                Children.Add(child);
            }
        }
    }

    private static void RestartComponent(string file)
    {
        lock (Gate)
        {
            foreach (var child in Children.Where(p => !p.HasExited && p.StartInfo.Arguments.IndexOf(file, StringComparison.OrdinalIgnoreCase) >= 0).ToList())
            {
                try { child.Kill(); } catch { }
            }
        }
        StartChild(file, hidden: true);
    }

    private static void PostControl(string path)
    {
        Task.Run(async delegate
        {
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    string secretFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), agentHomeName, "bridge.secret");
                    string bridgeSecret = "";
                    try { bridgeSecret = File.ReadAllText(secretFile).Trim(); } catch { }
                    if (!String.IsNullOrEmpty(bridgeSecret)) client.DefaultRequestHeaders.Add("X-Ergouzi-Bridge", bridgeSecret);
                    await client.PostAsync("http://" + bindHost + ":" + tokenSyncPort + path, new StringContent(""));
                }
            }
            catch { }
        });
    }

    private static string JsonEscape(string value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static string ReadBridgeSecret()
    {
        string secretFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), agentHomeName, "bridge.secret");
        try { return File.ReadAllText(secretFile).Trim(); } catch { return ""; }
    }

    private static void ShowTokenUpdateDialog()
    {
        using (var form = new Form { Text = "更新余额挂件令牌", Width = 500, Height = 230, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
        using (var label = new Label { Text = "访问令牌（不会写入日志或源码）：", Left = 16, Top = 18, AutoSize = true })
        using (var token = new TextBox { Left = 16, Top = 42, Width = 450, UseSystemPasswordChar = true })
        using (var longLived = new CheckBox { Text = "长效令牌：不自动刷新，失效时提示重新更新", Left = 16, Top = 78, Width = 450, Checked = true })
        using (var expiryLabel = new Label { Text = "过期时间 Unix 秒（非长效令牌填写，未知填 0）：", Left = 16, Top = 112, AutoSize = true })
        using (var expiry = new TextBox { Left = 16, Top = 136, Width = 180, Text = "0" })
        using (var save = new Button { Text = "保存", Left = 300, Top = 166, Width = 78, DialogResult = DialogResult.OK })
        using (var cancel = new Button { Text = "取消", Left = 388, Top = 166, Width = 78, DialogResult = DialogResult.Cancel })
        {
            form.Controls.AddRange(new Control[] { label, token, longLived, expiryLabel, expiry, save, cancel });
            form.AcceptButton = save;
            form.CancelButton = cancel;
            if (form.ShowDialog() != DialogResult.OK) return;
            string value = (token.Text ?? "").Trim();
            if (value.Length < 16 || value.Length > 8192 || value.Any(Char.IsWhiteSpace))
            {
                MessageBox.Show("令牌长度或格式无效。", "更新令牌", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            long expires = 0;
            if (!longLived.Checked && (!Int64.TryParse((expiry.Text ?? "").Trim(), out expires) || expires < 0))
            {
                MessageBox.Show("过期时间必须是 Unix 秒数字。", "更新令牌", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            PostTokenUpdate(value, expires, longLived.Checked);
        }
    }

    private static void PostTokenUpdate(string token, long expires, bool longLived)
    {
        Task.Run(async delegate
        {
            try
            {
                string secret = ReadBridgeSecret();
                if (String.IsNullOrEmpty(secret)) throw new InvalidOperationException("本地代理尚未生成桥接密钥，请稍后重试。");
                string body = "{\"accessToken\":\"" + JsonEscape(token) + "\",\"accessExpiresAt\":" + expires + ",\"longLived\":" + (longLived ? "true" : "false") + "}";
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
                using (var request = new HttpRequestMessage(HttpMethod.Post, "http://" + bindHost + ":" + agentPort + "/internal/update-token"))
                {
                    request.Headers.Add("X-Ergouzi-Bridge", secret);
                    request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                    using (var response = await client.SendAsync(request))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode || responseBody.IndexOf("\"ok\":true", StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidOperationException("本地代理拒绝了令牌。");
                    }
                }
                ShowMessage("令牌已更新并加密保存。", "更新令牌");
            }
            catch (Exception error) { ShowMessage("令牌更新失败：" + error.Message, "更新令牌"); }
        });
    }

    private static string JsonString(string json, string key)
    {
        var match = Regex.Match(json ?? "", "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Replace("\\/", "/") : "";
    }

    private static WebClient CreateUpdateClient(bool direct)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
        var client = new WebClient();
        client.Headers[HttpRequestHeader.UserAgent] = productName + "/" + productVersion;
        client.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
        IWebProxy proxy = null;
        if (!direct && !String.IsNullOrWhiteSpace(updateProxy))
        {
            Uri proxyUri;
            string proxyValue = SelectProxyValue(updateProxy);
            if (proxyValue.IndexOf("://", StringComparison.Ordinal) < 0) proxyValue = "http://" + proxyValue;
            if (!Uri.TryCreate(proxyValue, UriKind.Absolute, out proxyUri) || String.IsNullOrWhiteSpace(proxyUri.Host) || proxyUri.Port <= 0) throw new InvalidOperationException("更新代理地址无效");
            proxy = new WebProxy(proxyUri);
        }
        else if (!direct) proxy = WebRequest.DefaultWebProxy;
        if (proxy != null)
        {
            proxy.Credentials = CredentialCache.DefaultCredentials;
            client.Proxy = proxy;
        }
        else client.Proxy = null;
        return client;
    }

    private static string SelectProxyValue(string value)
    {
        foreach (string part in (value ?? "").Split(';'))
        {
            string item = part.Trim();
            if (item.StartsWith("https=", StringComparison.OrdinalIgnoreCase) || item.StartsWith("http=", StringComparison.OrdinalIgnoreCase)) return item.Substring(item.IndexOf('=') + 1).Trim();
        }
        return (value ?? "").Trim();
    }

    private static string DownloadStringWithFallback(string url)
    {
        Exception last = null;
        foreach (bool direct in new[] { false, true })
        {
            try { using (var client = CreateUpdateClient(direct)) return client.DownloadString(url); }
            catch (Exception error) { last = error; }
        }
        throw last ?? new InvalidOperationException("更新请求失败");
    }

    private static void DownloadFileWithFallback(string url, string destination)
    {
        Exception last = null;
        foreach (bool direct in new[] { false, true })
        {
            try { using (var client = CreateUpdateClient(direct)) { client.DownloadFile(url, destination); return; } }
            catch (Exception error) { last = error; }
        }
        throw last ?? new InvalidOperationException("更新下载失败");
    }

    private static void CheckForUpdates()
    {
        Task.Run(delegate
        {
            try
            {
                string api = "https://api.github.com/repos/" + githubRepo + "/releases/latest";
                string json = DownloadStringWithFallback(api);
                string tag = JsonString(json, "tag_name");
                string page = JsonString(json, "html_url");
                string asset = Regex.Match(json, "\\\"browser_download_url\\\"\\s*:\\s*\\\"([^\\\"]+ErgouziWhaleWidget-Setup\\.exe)\\\"", RegexOptions.IgnoreCase).Groups[1].Value.Replace("\\/", "/");
                ShowUpdateResult(tag, page, asset);
            }
            catch
            {
                try
                {
                    string raw = "https://raw.githubusercontent.com/" + githubRepo + "/main/package.json";
                    string json = DownloadStringWithFallback(raw);
                    ShowUpdateResult(JsonString(json, "version"), githubReleasesUrl, "");
                }
                catch (Exception fallbackError)
                {
                    string detail = String.IsNullOrWhiteSpace(updateProxy) ? "请检查网络或设置 ERGOUZI_UPDATE_PROXY。" : "请检查更新代理地址：" + updateProxy;
                    ShowMessage("暂时无法连接 GitHub 检查更新。\n" + detail + "\n" + fallbackError.Message, "检查更新");
                }
            }
        });
    }

    private static void ShowMessage(string message, string title)
    {
        if (tray == null || tray.ContextMenuStrip == null) return;
        tray.ContextMenuStrip.BeginInvoke(new Action(delegate { MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information); }));
    }

    private static void ShowUpdateResult(string latest, string page, string asset)
    {
        Version currentVersion, latestVersion;
        if (!Version.TryParse((productVersion ?? "0.0.0").TrimStart('v'), out currentVersion)) currentVersion = new Version(0, 0, 0);
        if (!Version.TryParse((latest ?? "").Trim().TrimStart('v'), out latestVersion)) { ShowMessage("GitHub 暂未提供可识别的版本信息。", "检查更新"); return; }
        if (latestVersion <= currentVersion) { ShowMessage("当前已是最新版本（v" + currentVersion + "）。", "检查更新"); return; }
        string releasePage = String.IsNullOrEmpty(page) ? githubReleasesUrl : page;
        tray.ContextMenuStrip.BeginInvoke(new Action(delegate
        {
            string message = "检测到新版本 v" + latestVersion + "（当前 v" + currentVersion + "）。\n\n是：下载并覆盖当前版本\n否：卸载当前版本后选择新的安装位置\n取消：稍后处理";
            DialogResult choice = MessageBox.Show(message, "发现余额挂件更新", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
            if (choice == DialogResult.Cancel) return;
            if (String.IsNullOrEmpty(asset))
            {
                MessageBox.Show("该版本暂未上传 Windows 安装包，将打开 GitHub 发布页面。", "更新包不可用", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Process.Start(new ProcessStartInfo { FileName = releasePage, UseShellExecute = true });
                return;
            }
            DownloadAndLaunch(asset, latestVersion.ToString(), choice == DialogResult.Yes, releasePage);
        }));
    }

    private static void DownloadAndLaunch(string assetUrl, string version, bool overwrite, string releasePage)
    {
        Task.Run(delegate
        {
            try
            {
                string setup = Path.Combine(Path.GetTempPath(), "ErgouziWhaleWidget-Setup-v" + version + ".exe");
                DownloadFileWithFallback(assetUrl, setup);
                tray.ContextMenuStrip.BeginInvoke(new Action(delegate
                {
                    if (overwrite)
                    {
                        Process.Start(new ProcessStartInfo { FileName = setup, Arguments = "--target \"" + root + "\"", UseShellExecute = true });
                    }
                    else
                    {
                        string uninstaller = Path.Combine(Directory.GetParent(root).FullName, UninstallerFileName);
                        if (File.Exists(uninstaller)) Process.Start(new ProcessStartInfo { FileName = uninstaller, Arguments = "--silent", UseShellExecute = true });
                        Task.Run(delegate
                        {
                            for (int i = 0; i < 30 && Directory.Exists(root); i++) Thread.Sleep(500);
                            Process.Start(new ProcessStartInfo { FileName = setup, UseShellExecute = true });
                        });
                    }
                }));
            }
            catch { ShowMessage("更新安装包下载失败，请打开 GitHub 发布页面手动下载。\n" + releasePage, "更新失败"); }
        });
    }

    private static void StopComponents()
    {
        if (stopping) return;
        stopping = true;
        lock (Gate)
        {
            foreach (var child in Children.ToList())
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                try { child.Dispose(); } catch { }
            }
            Children.Clear();
        }
        try { tray.Visible = false; tray.Dispose(); } catch { }
        try { signal.Dispose(); } catch { }
        try { mutex.ReleaseMutex(); mutex.Dispose(); } catch { }
    }
}
