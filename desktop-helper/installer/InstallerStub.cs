using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Management;
using Microsoft.Win32;
using System.Reflection;
using System.Windows.Forms;

internal static class Program
{
    private const string AppName = "YoutubeClipHelper";
    private const string StartupShortcutName = "Youtube Clip Helper.lnk";

    [STAThread]
    private static int Main()
    {
        try
        {
            string installRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppName);
            string helperExePath = Path.Combine(installRoot, "LocalClipHelper.exe");
            string helperDllPath = Path.Combine(installRoot, "LocalClipHelper.dll");
            string startupShortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                StartupShortcutName);
            string extractionRoot = Path.Combine(
                Path.GetTempPath(),
                AppName + "Setup",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(extractionRoot);

            Assembly assembly = Assembly.GetExecutingAssembly();
            Stream resourceStream = assembly.GetManifestResourceStream("PayloadZip");
            if (resourceStream == null)
            {
                throw new InvalidOperationException("Installer payload is missing.");
            }

            string zipPath = Path.Combine(extractionRoot, "payload.zip");
            using (resourceStream)
            {
                using (Stream fileStream = File.Create(zipPath))
                {
                    resourceStream.CopyTo(fileStream);
                }
            }

            ZipFile.ExtractToDirectory(zipPath, extractionRoot);
            StopRunningHelper(helperExePath, helperDllPath);
            Directory.CreateDirectory(installRoot);
            CopyDirectoryContents(extractionRoot, installRoot);
            Directory.CreateDirectory(Path.Combine(installRoot, "jobs"));
            Directory.CreateDirectory(Path.Combine(installRoot, "logs"));
            Directory.CreateDirectory(Path.Combine(installRoot, "output"));
            Directory.CreateDirectory(Path.Combine(installRoot, "temp"));
            Directory.CreateDirectory(Path.Combine(installRoot, "tools"));
            RemoveLegacyStartupEntry();
            CreateStartupShortcut(startupShortcutPath, helperExePath, installRoot);
            StartHelper(helperExePath, installRoot);
            TryDeleteDirectory(extractionRoot);

            MessageBox.Show(
                "YoutubeClipHelper installed successfully.\n\nOutput folder: " +
                Path.Combine(installRoot, "output"),
                "Youtube Clip Helper Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Youtube Clip Helper Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            return 1;
        }
    }

    private static void StopRunningHelper(string helperExePath, string helperDllPath)
    {
        foreach (Process process in Process.GetProcessesByName("LocalClipHelper"))
        {
            try
            {
                ProcessModule mainModule = process.MainModule;
                string runningPath = mainModule == null ? string.Empty : mainModule.FileName;
                if (!string.Equals(runningPath, helperExePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill();
                process.WaitForExit(5000);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'dotnet.exe'"))
            {
                foreach (ManagementObject processObject in searcher.Get())
                {
                    try
                    {
                        object commandLineValue = processObject["CommandLine"];
                        string commandLine = commandLineValue == null ? string.Empty : commandLineValue.ToString();
                        if (commandLine.IndexOf(helperDllPath, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        object processIdValue = processObject["ProcessId"];
                        if (processIdValue == null)
                        {
                            continue;
                        }

                        using (Process dotnetProcess = Process.GetProcessById(Convert.ToInt32(processIdValue)))
                        {
                            dotnetProcess.Kill();
                            dotnetProcess.WaitForExit(5000);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        System.Threading.Thread.Sleep(1200);
    }

    private static void CopyDirectoryContents(string sourceRoot, string destinationRoot)
    {
        foreach (string directoryPath in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = directoryPath.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relativePath));
        }

        foreach (string filePath in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(filePath);
            if (string.Equals(fileName, "payload.zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relativePath = filePath.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar);
            string destinationPath = Path.Combine(destinationRoot, relativePath);
            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            CopyFileWithRetry(filePath, destinationPath);
        }
    }

    private static void CopyFileWithRetry(string sourcePath, string destinationPath)
    {
        Exception lastError = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException exception)
            {
                lastError = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                lastError = exception;
            }

            System.Threading.Thread.Sleep(700);
        }

        throw new InvalidOperationException(
            "The installer could not update one of the helper files because it is still in use. Close Youtube Clip Helper and try again.",
            lastError);
    }

    private static void RemoveLegacyStartupEntry()
    {
        RegistryKey runKey = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true);
        try
        {
            if (runKey != null)
            {
                runKey.DeleteValue(AppName, false);
            }
        }
        finally
        {
            if (runKey != null)
            {
                runKey.Dispose();
            }
        }
    }

    private static void CreateStartupShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            throw new InvalidOperationException("Windows Script Host is unavailable on this system.");
        }

        object shell = Activator.CreateInstance(shellType);
        if (shell == null)
        {
            throw new InvalidOperationException("Could not create the shortcut shell object.");
        }

        object shortcut = shellType.InvokeMember(
            "CreateShortcut",
            BindingFlags.InvokeMethod,
            binder: null,
            target: shell,
            args: new object[] { shortcutPath });

        Type shortcutType = shortcut.GetType();
        shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
        shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
        shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Starts Youtube Clip Helper when Windows signs in." });
        shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, new object[0]);
    }

    private static void StartHelper(string helperExePath, string workingDirectory)
    {
        if (!File.Exists(helperExePath))
        {
            throw new FileNotFoundException("LocalClipHelper.exe was not found in the installer payload.");
        }

        Process process = new Process();
        try
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = helperExePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
            };

            process.Start();
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }
}
