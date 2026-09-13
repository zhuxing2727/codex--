using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

internal static class Uninstaller
{
    private const string InstallFolderName = "m3QAQ";

    [STAThread]
    public static int Main()
    {
        string target = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string markerPath = Path.Combine(target, "install.path");
        string trayPath = Path.Combine(target, "ErgouziWhaleWidget.exe");
        string markerValue = "";
        try { markerValue = File.ReadAllText(markerPath).Trim(); } catch { }
        bool markerMatches = String.Equals(Path.GetFullPath(markerValue).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase);
        if (!String.Equals(Path.GetFileName(target), InstallFolderName, StringComparison.OrdinalIgnoreCase) || !File.Exists(markerPath) || !File.Exists(trayPath) || !markerMatches)
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
            string escapedTarget = target.Replace("'", "''");
            string script = @"
$ownerPid = OWNER_PID
$target = 'TARGET'
$cleanupScript = 'CLEANUP_SCRIPT'
$targetPrefix = $target.TrimEnd('\') + '\'
$app = [Environment]::GetFolderPath('ApplicationData')
$agentData = Join-Path $app 'ergouzi-account-agent'
$overlayData = Join-Path $app 'DeepSeekWhaleOverlay'
$profileData = Join-Path $agentData 'wallet-browser'
$desktop = [Environment]::GetFolderPath('Desktop')
$programs = Join-Path $app 'Microsoft\Windows\Start Menu\Programs\余额挂件'

while (Get-Process -Id $ownerPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }

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

Stop-WidgetProcesses
Start-Sleep -Milliseconds 700
Remove-Item -LiteralPath (Join-Path $desktop '余额挂件.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $desktop 'Ergouzi 小鲸鱼.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $programs '卸载余额挂件.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $programs 'Uninstall-ErgouziWhaleWidget.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $programs -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $agentData -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $overlayData -Recurse -Force -ErrorAction SilentlyContinue

for ($i = 0; $i -lt 30; $i++) {
    Stop-WidgetProcesses
    Get-ChildItem -LiteralPath $target -Force -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Attributes = $_.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) } catch {}
    }
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $target)) { break }
    Start-Sleep -Milliseconds 500
}
Remove-Item -LiteralPath $cleanupScript -Force -ErrorAction SilentlyContinue
";
            script = script.Replace("OWNER_PID", ownerPid.ToString()).Replace("TARGET", escapedTarget).Replace("CLEANUP_SCRIPT", tempScript.Replace("'", "''"));
            File.WriteAllText(tempScript, script, Encoding.UTF8);
            // The cleaner must not inherit the install directory as its current directory;
            // Windows otherwise keeps that directory open and refuses to remove it.
            Process.Start(new ProcessStartInfo { FileName = "powershell.exe", Arguments = "-NoLogo -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + tempScript + "\"", WorkingDirectory = Path.GetTempPath(), UseShellExecute = false, CreateNoWindow = true });
            return 0;
        }
        catch (Exception error) { MessageBox.Show(error.Message, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
