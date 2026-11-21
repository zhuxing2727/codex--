using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

internal static class Uninstaller
{
    private const string InstallFolderName = "m3QAQ";

    private static string NormalizeDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full);
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (String.IsNullOrEmpty(trimmed) || String.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return root;
        return trimmed;
    }

    [STAThread]
    public static int Main()
    {
        string parent = NormalizeDirectory(AppDomain.CurrentDomain.BaseDirectory);
        string target = NormalizeDirectory(Path.Combine(parent, InstallFolderName));
        string selfPath = Path.Combine(parent, Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName));
        string markerPath = Path.Combine(target, "install.path");
        string trayPath = Path.Combine(target, "ErgouziWhaleWidget.exe");
        string markerValue = "";
        try { markerValue = File.ReadAllText(markerPath).Trim(); } catch { }
        bool markerMatches = false;
        try { markerMatches = String.Equals(NormalizeDirectory(markerValue), NormalizeDirectory(target), StringComparison.OrdinalIgnoreCase); } catch { }
        bool targetIsReparsePoint = false;
        try { targetIsReparsePoint = (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0; } catch { }
        if (!String.Equals(Path.GetFileName(target), InstallFolderName, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(target) || targetIsReparsePoint || !File.Exists(markerPath) || !File.Exists(trayPath) || !markerMatches || !String.Equals(NormalizeDirectory(Path.GetDirectoryName(target)), parent, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("卸载已取消：只允许删除带有有效安装标记的 m3QAQ 目录。", "卸载余额挂件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }
        string[] args = Environment.GetCommandLineArgs();
        bool silent = args.Any(value => String.Equals(value, "--silent", StringComparison.OrdinalIgnoreCase) || String.Equals(value, "/S", StringComparison.OrdinalIgnoreCase));
        if (!silent && MessageBox.Show("确定要完全卸载余额挂件吗？\n将删除程序、快捷方式、挂件状态和本地账户代理数据。", "卸载余额挂件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return 0;
        string tempScript = null;
        string progressFile = null;
        Process cleaner = null;
        try
        {
            int ownerPid = Process.GetCurrentProcess().Id;
            tempScript = Path.Combine(Path.GetTempPath(), "ergouzi-uninstall-" + Guid.NewGuid().ToString("N") + ".ps1");
            progressFile = Path.Combine(Path.GetTempPath(), "ergouzi-uninstall-" + Guid.NewGuid().ToString("N") + ".progress");
            string escapedTarget = target.Replace("'", "''");
string script = @"
$ErrorActionPreference = 'Stop'
$ownerPid = OWNER_PID
$target = 'TARGET'
$cleanupScript = 'CLEANUP_SCRIPT'
$selfPath = 'SELF_PATH'
$progressFile = 'PROGRESS_FILE'
$targetPrefix = $target.TrimEnd('\') + '\'
$app = [Environment]::GetFolderPath('ApplicationData')
$agentData = Join-Path $app 'ergouzi-account-agent'
$overlayData = Join-Path $app 'DeepSeekWhaleOverlay'
$profileData = Join-Path $target 'wallet-browser'
$profileNeedle = $profileData.TrimEnd('\')
$desktop = [Environment]::GetFolderPath('Desktop')
$programs = Join-Path $app 'Microsoft\Windows\Start Menu\Programs\余额挂件'

function Set-Progress([int]$percent, [string]$status, [string]$message) {
    try { Set-Content -LiteralPath $progressFile -Value ($percent.ToString() + '|' + $status + '|' + $message) -Encoding UTF8 -Force } catch {}
}

try {
Set-Progress 3 'RUNNING' '正在停止后台组件...'

function Stop-WidgetProcesses {
    $items = @{}
    $processes = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        if ($process.ProcessId -eq $PID -or $process.ProcessId -eq $ownerPid) { continue }
        $commandLine = [string]$process.CommandLine
        $processPath = [string]$process.ExecutablePath
        $owned = ($processPath -and $processPath.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) -or
            ($commandLine -and ($commandLine.IndexOf($target, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $commandLine.IndexOf($agentData, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $commandLine.IndexOf($profileData, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $commandLine.IndexOf($overlayData, [StringComparison]::OrdinalIgnoreCase) -ge 0))
        if ($owned) { $items[[int]$process.ProcessId] = $true }
    }
    foreach ($id in $items.Keys) { Stop-Process -Id ([int]$id) -Force -ErrorAction SilentlyContinue }
}

function Stop-ProfileEdge {
    $all = @(Get-CimInstance Win32_Process -Filter ""Name='msedge.exe'"" -ErrorAction SilentlyContinue)
    $byId = @{}
    foreach ($item in $all) { $byId[[int]$item.ProcessId] = $item }
    $matched = @{}
    foreach ($item in $all) {
        $commandLine = [string]$item.CommandLine
        if ($commandLine -and $commandLine.IndexOf($profileNeedle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $matched[[int]$item.ProcessId] = $true
        }
    }
    if ($matched.Count -eq 0) { return }
    $roots = @{}
    foreach ($id in @($matched.Keys)) {
        $current = [int]$id
        for ($depth = 0; $depth -lt 32; $depth++) {
            if (-not $byId.ContainsKey($current)) { break }
            $parent = $byId[$current]
            $parentId = [int]$parent.ParentProcessId
            if ($parentId -le 0 -or -not $matched.ContainsKey($parentId)) { $roots[$current] = $true; break }
            $current = $parentId
        }
    }
    foreach ($rootId in @($roots.Keys)) {
        try { & taskkill.exe /PID ([int]$rootId) /T /F *> $null } catch {}
    }
    foreach ($id in @($matched.Keys)) { Stop-Process -Id ([int]$id) -Force -ErrorAction SilentlyContinue }
}

Stop-ProfileEdge
Set-Progress 15 'RUNNING' '正在关闭钱包浏览器...'
Stop-WidgetProcesses
Start-Sleep -Milliseconds 350
Set-Progress 35 'RUNNING' '正在删除安装目录...'

$removed = $false
for ($i = 0; $i -lt 8; $i++) {
    if (-not (Test-Path -LiteralPath $target)) { $removed = $true; break }
    if ($i -eq 0) {
        try {
            $rootItem = Get-Item -LiteralPath $target -Force
            $rootItem.Attributes = $rootItem.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)
        } catch {}
        Get-ChildItem -LiteralPath $target -Force -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
            if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
                try { $_.Attributes = $_.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) } catch {}
            }
        }
    } elseif ($i -eq 2 -or $i -eq 5) {
        Stop-ProfileEdge
        Stop-WidgetProcesses
    }
    try { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop } catch {}
    if (-not (Test-Path -LiteralPath $target)) { $removed = $true; break }
    Set-Progress ([Math]::Min(60, 38 + ($i * 3))) 'RUNNING' ('正在等待文件释放...（第 ' + ($i + 1) + ' 次检查）')
    Start-Sleep -Milliseconds ([Math]::Min(800, 150 * ($i + 1)))
}
if (-not $removed) { throw '安装目录仍有文件被占用，请关闭相关程序后重试。' }

Set-Progress 70 'RUNNING' '正在删除快捷方式...'
Remove-Item -LiteralPath (Join-Path $desktop '余额挂件.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $desktop 'Ergouzi 小鲸鱼.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $programs '卸载余额挂件.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $programs 'Uninstall-ErgouziWhaleWidget.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $programs -Recurse -Force -ErrorAction SilentlyContinue

Set-Progress 85 'RUNNING' '正在清理本地数据...'
$cleanupWarnings = @()
foreach ($dataPath in @($agentData, $overlayData)) {
    try {
        if (Test-Path -LiteralPath $dataPath) { Remove-Item -LiteralPath $dataPath -Recurse -Force -ErrorAction Stop }
        if (Test-Path -LiteralPath $dataPath) { throw '目录仍存在' }
    } catch { $cleanupWarnings += (Split-Path -Leaf $dataPath) }
}

if ($cleanupWarnings.Count -gt 0) {
    Set-Progress 100 'WARN' '程序已卸载，但部分本地数据未能清理。'
} else {
    Set-Progress 100 'DONE' '卸载完成'
}
while (Get-Process -Id $ownerPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }
Remove-Item -LiteralPath $selfPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $cleanupScript -Force -ErrorAction SilentlyContinue
} catch {
    $message = ([string]$_.Exception.Message).Replace('|', '/').Replace([char]13, ' ').Replace([char]10, ' ')
    Set-Progress 100 'FAILED' ('卸载失败：' + $message)
    while (Get-Process -Id $ownerPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }
    Remove-Item -LiteralPath $cleanupScript -Force -ErrorAction SilentlyContinue
    exit 1
}
";
            script = script.Replace("OWNER_PID", ownerPid.ToString()).Replace("TARGET", escapedTarget).Replace("CLEANUP_SCRIPT", tempScript.Replace("'", "''")).Replace("SELF_PATH", selfPath.Replace("'", "''")).Replace("PROGRESS_FILE", progressFile.Replace("'", "''"));
            File.WriteAllText(tempScript, script, Encoding.UTF8);
            // The cleaner must not inherit the install directory as its current directory;
            // Windows otherwise keeps that directory open and refuses to remove it.
            cleaner = Process.Start(new ProcessStartInfo { FileName = "powershell.exe", Arguments = "-NoLogo -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + tempScript + "\"", WorkingDirectory = Path.GetTempPath(), UseShellExecute = false, CreateNoWindow = true });
            if (cleaner == null) throw new InvalidOperationException("卸载清理进程未能启动。");
            using (cleaner)
            {
                int result = ShowProgressWindow(progressFile, target, cleaner);
                try { File.Delete(progressFile); } catch { }
                return result;
            }
        }
        catch (Exception error)
        {
            if (cleaner == null)
            {
                try { if (tempScript != null) File.Delete(tempScript); } catch { }
                try { if (progressFile != null) File.Delete(progressFile); } catch { }
            }
            MessageBox.Show(error.Message, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int ShowProgressWindow(string progressFile, string target, Process cleanupProcess)
    {
        int result = 1;
        using (var form = new Form { Text = "正在卸载余额挂件", Width = 500, Height = 170, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ControlBox = false })
        using (var label = new Label { Text = "正在准备...", Left = 18, Top = 18, Width = 445, AutoEllipsis = true })
        using (var bar = new ProgressBar { Left = 18, Top = 52, Width = 445, Height = 24, Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous })
        using (var close = new Button { Text = "关闭", Left = 383, Top = 90, Width = 80, Height = 28, Visible = false })
        using (var timer = new System.Windows.Forms.Timer { Interval = 150 })
        {
            form.Controls.Add(label);
            form.Controls.Add(bar);
            form.Controls.Add(close);
            close.Click += delegate { form.Close(); };
            timer.Tick += delegate
            {
                string status = "";
                try
                {
                    string value = File.ReadAllText(progressFile, Encoding.UTF8);
                    int firstSplit = value.IndexOf('|');
                    int secondSplit = value.IndexOf('|', firstSplit + 1);
                    int percent;
                    if (firstSplit > 0 && secondSplit > firstSplit && Int32.TryParse(value.Substring(0, firstSplit), out percent))
                    {
                        bar.Value = Math.Max(0, Math.Min(100, percent));
                        status = value.Substring(firstSplit + 1, secondSplit - firstSplit - 1).Trim();
                        label.Text = value.Substring(secondSplit + 1).Trim();
                    }
                }
                catch { }
                if (status == "DONE")
                {
                    bar.Value = 100;
                    result = 0;
                    timer.Stop();
                    form.BeginInvoke(new Action(delegate { form.Close(); }));
                }
                else if (status == "WARN" || status == "FAILED" || cleanupProcess.HasExited)
                {
                    if (status != "WARN" && status != "FAILED") label.Text = Directory.Exists(target) ? "卸载未完成，请关闭相关程序后重试。" : "卸载清理进程意外结束。";
                    else if (status == "WARN") result = 1;
                    bar.Value = 100;
                    close.Visible = true;
                    form.ControlBox = true;
                    timer.Stop();
                }
            };
            timer.Start();
            Application.Run(form);
        }
        return result;
    }
}
