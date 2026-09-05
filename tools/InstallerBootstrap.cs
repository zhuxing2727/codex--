using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

internal static class InstallerBootstrap
{
    private static readonly byte[] Marker = System.Text.Encoding.ASCII.GetBytes("ERGOUZI_PAYLOAD_START\n");

    private static int FindMarker(byte[] data)
    {
        for (int i = data.Length - Marker.Length; i >= 0; i--)
        {
            bool match = true;
            for (int j = 0; j < Marker.Length; j++)
            {
                if (data[i + j] != Marker[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static string ExtractPayload()
    {
        byte[] executable = File.ReadAllBytes(Process.GetCurrentProcess().MainModule.FileName);
        int marker = FindMarker(executable);
        if (marker < 0) throw new InvalidDataException("Installer payload marker was not found.");
        string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErgouziWhaleWidget");
        Directory.CreateDirectory(target);
        using (var input = new MemoryStream(executable, marker + Marker.Length, executable.Length - marker - Marker.Length))
        using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!destination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid installer entry path.");
                if (String.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                entry.ExtractToFile(destination, true);
            }
        }
        return target;
    }

    private static void StopPreviousInstall(string target)
    {
        string script = "$needle=" + ToPowerShellString(target) + "; " +
            "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($needle) } | " +
            "ForEach-Object { if ($_.ProcessId -ne $PID) { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } }";
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        using (var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            UseShellExecute = false,
            CreateNoWindow = true,
        }))
        {
            process.WaitForExit(10000);
        }
        System.Threading.Thread.Sleep(1000);
    }

    private static string ToPowerShellString(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static void CreateShortcut(string target)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string shortcut = Path.Combine(desktop, "Ergouzi 小鲸鱼.lnk");
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell = Activator.CreateInstance(shellType);
        dynamic link = shell.CreateShortcut(shortcut);
        link.TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        link.Arguments = "/d /c call \"" + Path.Combine(target, "start-whale-overlay.cmd") + "\"";
        link.WorkingDirectory = target;
        link.Description = "启动 Ergouzi 小鲸鱼余额挂件";
        link.Save();
    }

    public static int Main()
    {
        try
        {
            string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErgouziWhaleWidget");
            StopPreviousInstall(target);
            target = ExtractPayload();
            CreateShortcut(target);
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(target, "start-whale-overlay.cmd"),
                WorkingDirectory = target,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            Console.WriteLine("Installed to " + target);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
