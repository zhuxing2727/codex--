using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class InstallerBootstrap
{
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("ERGOUZI_PAYLOAD_START\n");
    private const string InstallFolderName = "m3QAQ";
    private const string UninstallerFileName = "\u5378\u8F7D\u4F59\u989D\u6302\u4EF6.exe";

    private static int FindMarker(byte[] data)
    {
        for (int i = data.Length - Marker.Length; i >= 0; i--)
        {
            bool match = true;
            for (int j = 0; j < Marker.Length; j++) if (data[i + j] != Marker[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private static string PickTarget(string requested)
    {
        if (!String.IsNullOrWhiteSpace(requested)) return ResolveInstallTarget(requested);
        string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErgouziWhaleWidget");
        using (var form = new Form { Text = "安装余额挂件", Width = 560, Height = 180, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
        using (var label = new Label { Text = "选择安装父文件夹（程序将安装到其中的 m3QAQ 子目录）：", Left = 18, Top = 20, AutoSize = true })
        using (var box = new TextBox { Left = 18, Top = 48, Width = 420, Text = defaultPath })
        using (var browse = new Button { Text = "浏览...", Left = 446, Top = 46, Width = 82 })
        using (var install = new Button { Text = "安装", Left = 350, Top = 92, Width = 82, DialogResult = DialogResult.OK })
        using (var cancel = new Button { Text = "取消", Left = 440, Top = 92, Width = 82, DialogResult = DialogResult.Cancel })
        {
            browse.Click += delegate { BrowseForFolder(form, box, browse, defaultPath); };
            form.Controls.AddRange(new Control[] { label, box, browse, install, cancel });
            form.AcceptButton = install;
            form.CancelButton = cancel;
            if (form.ShowDialog() != DialogResult.OK) return null;
            string target = (box.Text ?? "").Trim();
            if (target.Length == 0) throw new InvalidOperationException("安装目录不能为空。");
            return ResolveInstallTarget(target);
        }
    }

    private static string ResolveInstallTarget(string selectedPath)
    {
        string selected = Path.GetFullPath(selectedPath.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (String.Equals(Path.GetFileName(selected), InstallFolderName, StringComparison.OrdinalIgnoreCase)) return selected;
        return Path.Combine(selected, InstallFolderName);
    }

    private static void BrowseForFolder(Form owner, TextBox targetBox, Button browseButton, string initialPath)
    {
        // FolderBrowserDialog can block the owner when a shell extension hangs.
        // Run it on its own STA so the installer remains repaintable/cancellable.
        var thread = new Thread(new ThreadStart(delegate
        {
            string selected = null;
            try
            {
                using (var dialog = new FolderBrowserDialog { Description = "选择余额挂件安装父文件夹，程序将创建 m3QAQ 子目录", SelectedPath = initialPath, ShowNewFolderButton = true })
                {
                    if (dialog.ShowDialog() == DialogResult.OK) selected = dialog.SelectedPath;
                }
            }
            catch { }
            if (!targetBox.IsDisposed)
            {
                try { owner.BeginInvoke(new Action(delegate { if (!String.IsNullOrWhiteSpace(selected) && !targetBox.IsDisposed) targetBox.Text = selected; if (!browseButton.IsDisposed) browseButton.Enabled = true; })); } catch { }
            }
        }));
        browseButton.Enabled = false;
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void ValidateTarget(string target)
    {
        string full = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!String.Equals(Path.GetFileName(full), InstallFolderName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("安装目录必须是所选文件夹内的 m3QAQ 子目录。\n实际安装位置：" + Path.Combine(full, InstallFolderName));
        if (String.IsNullOrEmpty(full) || String.Equals(full, root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能将磁盘根目录作为安装目录。");
        if (String.Equals(full, Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能将 Windows 系统目录作为安装目录。");
        DirectoryInfo parentInfo = Directory.GetParent(full);
        string parent = parentInfo == null ? "" : parentInfo.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string windowsPrefix = windows + Path.DirectorySeparatorChar;
        if (String.Equals(parent, windows, StringComparison.OrdinalIgnoreCase) || parent.StartsWith(windowsPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能将 Windows 系统目录或其子目录作为安装父文件夹。");
        if (Directory.Exists(full))
        {
            string tray = Path.Combine(full, "ErgouziWhaleWidget.exe");
            string marker = Path.Combine(full, "install.path");
            bool owned = File.Exists(tray) || File.Exists(marker);
            bool hasFiles = Directory.EnumerateFileSystemEntries(full).Any();
            if (hasFiles && !owned)
            {
                var result = MessageBox.Show("目标目录不是余额挂件目录，且已经包含文件。\n继续安装会覆盖同名文件，但不会自动删除其他文件。", "确认安装目录", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (result != DialogResult.OK) throw new OperationCanceledException();
            }
        }
    }

    private static void StopPreviousInstall(string target)
    {
        string needle = target.Replace("'", "''");
        string script = "$needle='" + needle + "'; Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($needle) -and $_.Name -notmatch 'ErgouziWhaleWidget-Setup|InstallerBootstrap' } | ForEach-Object { if ($_.ProcessId -ne $PID) { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } }";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using (var process = Process.Start(new ProcessStartInfo { FileName = "powershell.exe", Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, UseShellExecute = false, CreateNoWindow = true }))
        { if (process != null) process.WaitForExit(10000); }
        System.Threading.Thread.Sleep(500);
    }

    private static void ExtractPayload(string target)
    {
        byte[] executable = File.ReadAllBytes(Process.GetCurrentProcess().MainModule.FileName);
        int marker = FindMarker(executable);
        if (marker < 0) throw new InvalidDataException("Installer payload marker was not found.");
        Directory.CreateDirectory(target);
        using (var input = new MemoryStream(executable, marker + Marker.Length, executable.Length - marker - Marker.Length))
        using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
        {
            string root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid installer entry path.");
                if (String.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                entry.ExtractToFile(destination, true);
            }
        }
    }

    private static void CreateShortcut(string target, string uninstaller)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string shortcut = Path.Combine(desktop, "余额挂件.lnk");
        string oldShortcut = Path.Combine(desktop, "Ergouzi 小鲸鱼.lnk");
        try { if (File.Exists(oldShortcut)) File.Delete(oldShortcut); } catch { }
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell = Activator.CreateInstance(shellType);
        dynamic link = shell.CreateShortcut(shortcut);
        string tray = Path.Combine(target, "ErgouziWhaleWidget.exe");
        link.TargetPath = tray;
        link.WorkingDirectory = target;
        link.IconLocation = Path.Combine(target, "assets", "balance-widget.ico") + ",0";
        link.WindowStyle = 1;
        link.Description = "启动余额挂件";
        link.Save();
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", "余额挂件");
        Directory.CreateDirectory(programs);
        string uninstallShortcut = Path.Combine(programs, "卸载余额挂件.lnk");
        string oldUninstallShortcut = Path.Combine(programs, "Uninstall-ErgouziWhaleWidget.lnk");
        try { if (File.Exists(oldUninstallShortcut)) File.Delete(oldUninstallShortcut); } catch { }
        dynamic uninstallLink = shell.CreateShortcut(uninstallShortcut);
        uninstallLink.TargetPath = uninstaller;
        uninstallLink.WorkingDirectory = Path.GetDirectoryName(uninstaller);
        uninstallLink.IconLocation = Path.Combine(target, "assets", "balance-widget.ico") + ",0";
        uninstallLink.WindowStyle = 1;
        uninstallLink.Description = "卸载余额挂件";
        uninstallLink.Save();
    }

    public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string requested = args == null ? null : args.SkipWhile(a => !String.Equals(a, "--target", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
            string target = PickTarget(requested);
            if (target == null) return 0;
            ValidateTarget(target);
            StopPreviousInstall(target);
            ExtractPayload(target);
            try { File.Delete(Path.Combine(target, "Uninstall-ErgouziWhaleWidget.exe")); } catch { }
            string parent = Directory.GetParent(target).FullName;
            string bundledUninstaller = Path.Combine(target, "uninstaller.exe");
            string externalUninstaller = Path.Combine(parent, UninstallerFileName);
            if (!File.Exists(bundledUninstaller)) throw new InvalidDataException("安装包缺少卸载程序。");
            File.Copy(bundledUninstaller, externalUninstaller, true);
            File.Delete(bundledUninstaller);
            try { File.Delete(Path.Combine(target, UninstallerFileName)); } catch { }
            CreateShortcut(target, externalUninstaller);
            File.WriteAllText(Path.Combine(target, "install.path"), target + Environment.NewLine, Encoding.UTF8);
            string tray = Path.Combine(target, "ErgouziWhaleWidget.exe");
            if (File.Exists(tray)) Process.Start(new ProcessStartInfo { FileName = tray, WorkingDirectory = target, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            MessageBox.Show("余额挂件安装完成。\n安装位置：" + target, "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
