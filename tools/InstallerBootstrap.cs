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
        if (!String.IsNullOrWhiteSpace(requested)) return Path.GetFullPath(requested.Trim());
        string defaultPath = String.IsNullOrWhiteSpace(requested)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErgouziWhaleWidget")
            : requested;
        using (var form = new Form { Text = "安装余额挂件", Width = 560, Height = 180, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
        using (var label = new Label { Text = "选择安装位置：", Left = 18, Top = 20, AutoSize = true })
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
            return Path.GetFullPath(target);
        }
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
                using (var dialog = new FolderBrowserDialog { Description = "选择余额挂件安装目录", SelectedPath = initialPath, ShowNewFolderButton = true })
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
        if (String.IsNullOrEmpty(full) || String.Equals(full, root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能将磁盘根目录作为安装目录。");
        if (String.Equals(full, Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能将 Windows 系统目录作为安装目录。");
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

    private static void CreateShortcut(string target)
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
            CreateShortcut(target);
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
