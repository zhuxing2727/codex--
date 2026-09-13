using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Text.RegularExpressions;

internal static class ErgouziTrayHost
{
    private static string MutexName;
    private static string SignalName;
    private static string bindHost;
    private static int tokenSyncPort;
    private static string agentHomeName;
    private static int monitorIntervalMs;
    private static string productName;
    private static string productVersion;
    private static string githubRepo;
    private static string githubReleasesUrl;
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
        tokenSyncPort = IntConfig(json, "tokenSyncPort", 17892);
        agentHomeName = GetConfig(json, "agentHomeName", "ergouzi-account-agent");
        monitorIntervalMs = IntConfig(json, "trayMonitorIntervalMs", 5000);
        productName = GetConfig(json, "productName", "ErgouziWhaleWidget");
        productVersion = GetConfig(json, "version", "0.0.0");
        githubRepo = GetConfig(json, "githubRepo", "zhuxing2727/codex--");
        githubReleasesUrl = GetConfig(json, "githubReleasesUrl", "https://github.com/zhuxing2727/codex--/releases");
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
            var child = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = hidden,
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            });
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

    private static string JsonString(string json, string key)
    {
        var match = Regex.Match(json ?? "", "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Replace("\\/", "/") : "";
    }

    private static void CheckForUpdates()
    {
        Task.Run(delegate
        {
            try
            {
                string api = "https://api.github.com/repos/" + githubRepo + "/releases/latest";
                string json;
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = productName + "/" + productVersion;
                    json = client.DownloadString(api);
                }
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
                    string json;
                    using (var client = new WebClient()) { client.Headers[HttpRequestHeader.UserAgent] = productName + "/" + productVersion; json = client.DownloadString(raw); }
                    ShowUpdateResult(JsonString(json, "version"), githubReleasesUrl, "");
                }
                catch { ShowMessage("暂时无法连接 GitHub 检查更新。", "检查更新"); }
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
                using (var client = new WebClient()) { client.Headers[HttpRequestHeader.UserAgent] = productName + "/" + productVersion; client.DownloadFile(assetUrl, setup); }
                tray.ContextMenuStrip.BeginInvoke(new Action(delegate
                {
                    if (overwrite)
                    {
                        Process.Start(new ProcessStartInfo { FileName = setup, Arguments = "--target \"" + root + "\"", UseShellExecute = true });
                    }
                    else
                    {
                        string uninstaller = Path.Combine(root, "卸载余额挂件.exe");
                        if (File.Exists(uninstaller)) Process.Start(new ProcessStartInfo { FileName = uninstaller, Arguments = "--silent", UseShellExecute = true });
                        Task.Run(delegate { Thread.Sleep(1800); Process.Start(new ProcessStartInfo { FileName = setup, UseShellExecute = true }); });
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
