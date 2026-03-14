using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

internal sealed class TrayHost : IDisposable
{
    private readonly HelperPaths _helperPaths;
    private readonly ConcurrentDictionary<string, ClipJob> _jobs;
    private readonly Func<string, Task> _cancelJobAsync;
    private readonly Func<Task> _exitHelperAsync;
    private readonly ManualResetEventSlim _initialized = new(false);
    private readonly Thread _thread;

    private Icon? _appIcon;
    private ApplicationContext? _applicationContext;
    private Form? _messageLoopForm;
    private NotifyIcon? _notifyIcon;

    public TrayHost(
        HelperPaths helperPaths,
        ConcurrentDictionary<string, ClipJob> jobs,
        Func<string, Task> cancelJobAsync,
        Func<Task> exitHelperAsync)
    {
        _helperPaths = helperPaths;
        _jobs = jobs;
        _cancelJobAsync = cancelJobAsync;
        _exitHelperAsync = exitHelperAsync;

        _thread = new Thread(RunTrayThread)
        {
            IsBackground = true,
            Name = "LocalClipHelper.TrayHost",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _initialized.Wait();
    }

    public void Dispose()
    {
        if (_applicationContext is null)
        {
            return;
        }

        try
        {
            _messageLoopForm?.BeginInvoke(new Action(CloseTray));
        }
        catch
        {
            CloseTray();
        }
    }

    private void RunTrayThread()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Open Output Folder", null, (_, _) => OpenFolder(_helperPaths.OutputDirectory));
        contextMenu.Items.Add("Open Logs Folder", null, (_, _) => OpenFolder(_helperPaths.LogsDirectory));
        contextMenu.Items.Add("Cancel Active Jobs", null, async (_, _) => await CancelActiveJobsAsync());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit Helper", null, async (_, _) => await ExitAsync());

        _appIcon = LoadApplicationIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Visible = true,
            Text = "Youtube Clip Helper",
            ContextMenuStrip = contextMenu,
        };

        _notifyIcon.DoubleClick += (_, _) => OpenFolder(_helperPaths.OutputDirectory);

        _messageLoopForm = new Form
        {
            ShowInTaskbar = false,
            WindowState = FormWindowState.Minimized,
            Opacity = 0,
            Icon = _appIcon,
        };

        _applicationContext = new ApplicationContext(_messageLoopForm);
        _initialized.Set();
        Application.Run(_applicationContext);
    }

    private async Task CancelActiveJobsAsync()
    {
        var cancellableJobs = _jobs.Values
            .Where(job => job.Status is not ("completed" or "failed" or "cancelled"))
            .Select(job => job.JobId)
            .ToArray();

        foreach (var jobId in cancellableJobs)
        {
            await _cancelJobAsync(jobId);
        }
    }

    private async Task ExitAsync()
    {
        await CancelActiveJobsAsync();
        await _exitHelperAsync();
        CloseTray();
    }

    private void CloseTray()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_appIcon is not null)
        {
            _appIcon.Dispose();
            _appIcon = null;
        }

        _applicationContext?.ExitThread();
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }
}
