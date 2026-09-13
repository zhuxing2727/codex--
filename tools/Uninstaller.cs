using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Uninstaller
{
    private const string InstallFolderName = "m3QAQ";

    [STAThread]
    public static int Main()
    {
        string parent = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string target = Path.Combine(parent, InstallFolderName);
        string selfPath = Path.Combine(parent, Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName));
        string markerPath = Path.Combine(target, "install.path");
        string trayPath = Path.Combine(target, "ErgouziWhaleWidget.exe");
        string markerValue = "";
        try { markerValue = File.ReadAllText(markerPath).Trim(); } catch { }
        bool markerMatches = String.Equals(Path.GetFullPath(markerValue).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase);
        if (!String.Equals(Path.GetFileName(target), InstallFolderName, StringComparison.OrdinalIgnoreCase) || !File.Exists(markerPath) || !File.Exists(trayPath) || !markerMatches || !String.Equals(Path.GetDirectoryName(target), parent, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("卸载已取消：只允许删除带有有效安装标记的 m3QAQ 目录。", "卸载余额挂件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }
        string[] args = Environment.GetCommandLineArgs();
        bool silent = args.Any(value => String.Equals(value, "--silent", StringComparison.OrdinalIgnoreCase) || String.Equals(value, "/S", StringComparison.OrdinalIgnoreCase));
        if (!silent && MessageBox.Show("确定要完全卸载余额挂件吗？\n将删除程序、快捷方式、挂件状态和本地账户代理数据。", "卸载余额挂件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return 0;
        try
        {
            int ownerPid = Process.GetCurrentProcess().Id;
            string tempScript = Path.Combine(Path.GetTempPath(), "ergouzi-uninstall-" + Guid.NewGuid().ToString("N") + ".ps1");
            string progressFile = Path.Combine(Path.GetTempPath(), "ergouzi-uninstall-" + Guid.NewGuid().ToString("N") + ".progress");
            string escapedTarget = target.Replace("'", "''");
            string script = @"
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

function Set-Progress([int]$percent, [string]$message) {
    try { Set-Content -LiteralPath $progressFile -Value ($percent.ToString() + '|' + $message) -Encoding UTF8 -Force } catch {}
}

while (Get-Process -Id $ownerPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }
Set-Progress 5 '正在停止后台组件...'

function Stop-WidgetProcesses {
    $items = @{}
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.ProcessId -eq $PID -or $_.ProcessId -eq $ownerPid) { return }
        $commandLine = [string]$_.CommandLine
        $processPath = ''
        try { $processPath = [string](Get-Process -Id $_.ProcessId -ErrorAction Stop).Path } catch {}
        $owned = ($processPath -and $processPath.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) -or
            ($commandLine -and ($commandLine.IndexOf($target, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $commandLine.IndexOf($agentData, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $commandLine.IndexOf($profileData, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $commandLine.IndexOf($overlayData, [StringComparison]::OrdinalIgnoreCase) -ge 0))
        if ($owned) { $items[$_.ProcessId] = $true }
    }
    foreach ($id in $items.Keys) { Stop-Process -Id ([int]$id) -Force -ErrorAction SilentlyContinue }
}

function Stop-ProfileEdge {
    $all = @(Get-CimInstance Win32_Process -Filter ""Name='msedge.exe'"" -ErrorAction SilentlyContinue)
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
            $parent = $all | Where-Object { [int]$_.ProcessId -eq $current } | Select-Object -First 1
            if (-not $parent) { break }
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
Set-Progress 20 '正在关闭钱包浏览器...'
Stop-WidgetProcesses
Start-Sleep -Milliseconds 700
Set-Progress 35 '正在删除快捷方式...'
Remove-Item -LiteralPath (Join-Path $desktop '余额挂件.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $desktop 'Ergouzi 小鲸鱼.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $programs '卸载余额挂件.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $programs 'Uninstall-ErgouziWhaleWidget.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $programs -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $agentData -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $overlayData -Recurse -Force -ErrorAction SilentlyContinue
Set-Progress 50 '正在清理本地数据...'

for ($i = 0; $i -lt 30; $i++) {
    Stop-ProfileEdge
    Stop-WidgetProcesses
    Get-ChildItem -LiteralPath $target -Force -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Attributes = $_.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) } catch {}
    }
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
    Set-Progress ([Math]::Min(95, 55 + ($i * 2))) ('正在清空安装目录...（第 ' + ($i + 1) + ' 次检查）')
    if (-not (Test-Path -LiteralPath $target)) { break }
    Start-Sleep -Milliseconds 500
}
Set-Progress 100 '卸载完成'
Remove-Item -LiteralPath $cleanupScript -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $selfPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $progressFile -Force -ErrorAction SilentlyContinue
";
            script = script.Replace("OWNER_PID", ownerPid.ToString()).Replace("TARGET", escapedTarget).Replace("CLEANUP_SCRIPT", tempScript.Replace("'", "''")).Replace("SELF_PATH", selfPath.Replace("'", "''")).Replace("PROGRESS_FILE", progressFile.Replace("'", "''"));
            File.WriteAllText(tempScript, script, Encoding.UTF8);
            // The cleaner must not inherit the install directory as its current directory;
            // Windows otherwise keeps that directory open and refuses to remove it.
            Process.Start(new ProcessStartInfo { FileName = "powershell.exe", Arguments = "-NoLogo -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + tempScript + "\"", WorkingDirectory = Path.GetTempPath(), UseShellExecute = false, CreateNoWindow = true });
            ShowProgressWindow(progressFile, target);
            return 0;
        }
        catch (Exception error) { MessageBox.Show(error.Message, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }

    private static void ShowProgressWindow(string progressFile, string target)
    {
        using (var form = new Form { Text = "正在卸载余额挂件", Width = 500, Height = 150, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ControlBox = false })
        using (var label = new Label { Text = "正在准备...", Left = 18, Top = 18, Width = 445, AutoEllipsis = true })
        using (var bar = new ProgressBar { Left = 18, Top = 52, Width = 445, Height = 24, Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous })
        using (var timer = new System.Windows.Forms.Timer { Interval = 150 })
        {
            form.Controls.Add(label);
            form.Controls.Add(bar);
            timer.Tick += delegate
            {
                try
                {
                    string value = File.ReadAllText(progressFile, Encoding.UTF8);
                    int split = value.IndexOf('|');
                    int percent;
                    if (split > 0 && Int32.TryParse(value.Substring(0, split), out percent))
                    {
                        bar.Value = Math.Max(0, Math.Min(100, percent));
                        label.Text = value.Substring(split + 1).Trim();
                    }
                }
                catch { }
                if (!Directory.Exists(target))
                {
                    bar.Value = 100;
                    label.Text = "卸载完成";
                    timer.Stop();
                    form.BeginInvoke(new Action(delegate { form.Close(); }));
                }
            };
            timer.Start();
            Application.Run(form);
        }
    }
}
