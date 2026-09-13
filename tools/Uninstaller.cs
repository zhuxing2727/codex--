using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal static class Uninstaller
{
    [STAThread]
    public static int Main()
    {
        string target = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        bool silent = Environment.GetCommandLineArgs().Length > 1 && String.Equals(Environment.GetCommandLineArgs()[1], "--silent", StringComparison.OrdinalIgnoreCase);
        if (!silent && MessageBox.Show("确定要完全卸载余额挂件吗？\n将删除程序、快捷方式、挂件状态和本地账户代理数据。", "卸载余额挂件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return 0;
        try
        {
            int ownerPid = Process.GetCurrentProcess().Id;
            string script = "$ownerPid=" + ownerPid + "; $target='" + target.Replace("'", "''") + "'; $app=[Environment]::GetFolderPath('ApplicationData'); $agentData=Join-Path $app 'ergouzi-account-agent'; $overlayData=Join-Path $app 'DeepSeekWhaleOverlay'; while (Get-Process -Id $ownerPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }; Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and ( $_.CommandLine.Contains($target) -or $_.CommandLine.Contains($agentData) -or $_.CommandLine.Contains($overlayData) ) -and $_.Name -notmatch 'Uninstall-ErgouziWhaleWidget' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; Start-Sleep -Milliseconds 700; $desktop=[Environment]::GetFolderPath('Desktop'); Remove-Item -LiteralPath ($desktop + '\\余额挂件.lnk') -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath ($desktop + '\\Ergouzi 小鲸鱼.lnk') -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $agentData -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $overlayData -Recurse -Force -ErrorAction SilentlyContinue; for ($i=0; $i -lt 12; $i++) { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $agentData -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $overlayData -Recurse -Force -ErrorAction SilentlyContinue; if (-not (Test-Path -LiteralPath $target)) { break }; Start-Sleep -Milliseconds 500 }";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Process.Start(new ProcessStartInfo { FileName = "powershell.exe", Arguments = "-NoLogo -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand " + encoded, UseShellExecute = false, CreateNoWindow = true });
            return 0;
        }
        catch (Exception error) { MessageBox.Show(error.Message, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
