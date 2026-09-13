using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

internal static class Uninstaller
{
    [STAThread]
    public static int Main()
    {
        string target = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string[] args = Environment.GetCommandLineArgs();
        bool silent = args.Any(value => String.Equals(value, "--silent", StringComparison.OrdinalIgnoreCase) || String.Equals(value, "/S", StringComparison.OrdinalIgnoreCase));
        if (!silent && MessageBox.Show("确定要完全卸载余额挂件吗？\n将删除程序、快捷方式、挂件状态和本地账户代理数据。", "卸载余额挂件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return 0;
        try
        {
            int ownerPid = Process.GetCurrentProcess().Id;
            string script = "$ownerPid=" + ownerPid + "; $target='" + target.Replace("'", "''") + "'; $app=[Environment]::GetFolderPath('ApplicationData'); $agentData=Join-Path $app 'ergouzi-account-agent'; $overlayData=Join-Path $app 'DeepSeekWhaleOverlay'; $desktop=[Environment]::GetFolderPath('Desktop'); $programs=Join-Path $app 'Microsoft\\Windows\\Start Menu\\Programs\\余额挂件'; while (Get-Process -Id $ownerPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }; Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and ( $_.CommandLine.Contains($target) -or $_.CommandLine.Contains($agentData) -or $_.CommandLine.Contains($overlayData) ) -and $_.ProcessId -ne $PID } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; Start-Sleep -Milliseconds 700; Remove-Item -LiteralPath ($desktop + '\\余额挂件.lnk') -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath ($desktop + '\\Ergouzi 小鲸鱼.lnk') -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath (Join-Path $programs '卸载余额挂件.lnk') -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath (Join-Path $programs 'Uninstall-ErgouziWhaleWidget.lnk') -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $programs -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $agentData -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $overlayData -Recurse -Force -ErrorAction SilentlyContinue; for ($i=0; $i -lt 20; $i++) { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $agentData -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $overlayData -Recurse -Force -ErrorAction SilentlyContinue; if (-not (Test-Path -LiteralPath $target)) { break }; Start-Sleep -Milliseconds 500 }";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Process.Start(new ProcessStartInfo { FileName = "powershell.exe", Arguments = "-NoLogo -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand " + encoded, UseShellExecute = false, CreateNoWindow = true });
            return 0;
        }
        catch (Exception error) { MessageBox.Show(error.Message, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
