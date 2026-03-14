using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.UseUrls("http://127.0.0.1:48721");
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();
app.UseCors();

var helperPaths = HelperPaths.Create();
var jobs = new ConcurrentDictionary<string, ClipJob>();
var jobCancellationTokens = new ConcurrentDictionary<string, CancellationTokenSource>();
var toolingLock = new SemaphoreSlim(1, 1);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
};

app.MapGet("/health", () =>
{
    var response = new HealthResponse(
        Ok: true,
        Version: "1.0.0",
        HelperOnline: true,
        ToolingReady: helperPaths.ToolingReady,
        ActiveJobCount: jobs.Values.Count(job => job.Status is not ("completed" or "failed" or "cancelled")),
        OutputDirectory: helperPaths.OutputDirectory);

    return Results.Json(response, jsonOptions);
});

app.MapPost("/jobs", async (CreateJobRequest request) =>
{
    if (!Uri.IsWellFormedUriString(request.SourcePageUrl, UriKind.Absolute))
    {
        return Results.BadRequest(new { error = "The sourcePageUrl must be a valid absolute URL." });
    }

    if (request.StartTimeSeconds < 0 || request.EndTimeSeconds <= request.StartTimeSeconds)
    {
        return Results.BadRequest(new { error = "The selected time range is invalid." });
    }

    var job = new ClipJob
    {
        JobId = Guid.NewGuid().ToString("N"),
        SourcePageUrl = request.SourcePageUrl,
        VideoTitle = string.IsNullOrWhiteSpace(request.VideoTitle) ? "YouTube Clip" : request.VideoTitle.Trim(),
        StartTimeSeconds = request.StartTimeSeconds,
        EndTimeSeconds = request.EndTimeSeconds,
        OutputFormat = string.IsNullOrWhiteSpace(request.OutputFormat) ? "mp4" : request.OutputFormat.Trim().ToLowerInvariant(),
        Status = "queued",
        Phase = "queued",
        Progress = 0,
        Message = "Job queued.",
        LogFilePath = Path.Combine(helperPaths.LogsDirectory, $"{Guid.NewGuid():N}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.log"),
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    AppendJobLog(helperPaths, job, "Job created.");
    jobs[job.JobId] = job;
    await PersistJobAsync(job, helperPaths, jsonOptions);

    var cancellationTokenSource = new CancellationTokenSource();
    jobCancellationTokens[job.JobId] = cancellationTokenSource;

    _ = Task.Run(() => ProcessJobAsync(
        job.JobId,
        jobs,
        jobCancellationTokens,
        helperPaths,
        toolingLock,
        jsonOptions,
        app.Logger,
        cancellationTokenSource.Token));

    return Results.Json(new JobAcceptedResponse(job.JobId, job.Status, job.Progress), jsonOptions);
});

app.MapGet("/jobs/{jobId}", async (string jobId) =>
{
    if (jobs.TryGetValue(jobId, out var liveJob))
    {
        return Results.Json(liveJob, jsonOptions);
    }

    var persistedJob = await TryLoadJobAsync(jobId, helperPaths, jsonOptions);
    return persistedJob is null ? Results.NotFound(new { error = "Job not found." }) : Results.Json(persistedJob, jsonOptions);
});

app.MapDelete("/jobs/{jobId}", async (string jobId) =>
{
    var job = await CancelJobAsync(jobId);
    return job is null ? Results.NotFound(new { error = "Job not found." }) : Results.Json(job, jsonOptions);
});

app.MapGet("/jobs/{jobId}/log", async (string jobId) =>
{
    ClipJob? job = jobs.TryGetValue(jobId, out var liveJob)
        ? liveJob
        : await TryLoadJobAsync(jobId, helperPaths, jsonOptions);

    if (job is null || string.IsNullOrWhiteSpace(job.LogFilePath) || !File.Exists(job.LogFilePath))
    {
        return Results.NotFound(new { error = "Log not found." });
    }

    return Results.Text(await File.ReadAllTextAsync(job.LogFilePath), "text/plain");
});

app.MapGet("/jobs/{jobId}/download", async (string jobId, HttpContext httpContext) =>
{
    ClipJob? job = jobs.TryGetValue(jobId, out var liveJob)
        ? liveJob
        : await TryLoadJobAsync(jobId, helperPaths, jsonOptions);

    if (job is null)
    {
        return Results.NotFound(new { error = "Job not found." });
    }

    if (job.Status != "completed" || string.IsNullOrWhiteSpace(job.OutputFilePath) || !File.Exists(job.OutputFilePath))
    {
        return Results.BadRequest(new { error = "The clip file is not available for download." });
    }

    var outputPath = job.OutputFilePath;
    var downloadFileName = Path.GetFileName(outputPath);

    httpContext.Response.StatusCode = StatusCodes.Status200OK;
    httpContext.Response.ContentType = "video/mp4";
    httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{downloadFileName}\"";

    try
    {
        await using var fileStream = File.OpenRead(outputPath);
        await fileStream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
        await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
    }
    finally
    {
        AppendJobLog(helperPaths, job, $"Chrome download served for {downloadFileName}. Deleting local helper copy.");
        TryDeleteFile(outputPath);
        job.OutputFilePath = null;
        job.Message = "Chrome download completed. Local helper copy deleted.";
        job.UpdatedAtUtc = DateTimeOffset.UtcNow;
        jobs[job.JobId] = job;
        await PersistJobAsync(job, helperPaths, jsonOptions);
    }

    return Results.Empty;
});

using var trayHost = new TrayHost(
    helperPaths,
    jobs,
    CancelJobAsync,
    async () =>
    {
        foreach (var activeJobId in jobCancellationTokens.Keys.ToArray())
        {
            await CancelJobAsync(activeJobId);
        }

        await app.StopAsync();
    });

await app.RunAsync();

async Task<ClipJob?> CancelJobAsync(string jobId)
{
    if (!jobs.TryGetValue(jobId, out var job))
    {
        return await TryLoadJobAsync(jobId, helperPaths, jsonOptions);
    }

    if (job.Status is "completed" or "failed" or "cancelled")
    {
        return job;
    }

    AppendJobLog(helperPaths, job, "Cancellation requested.");

    if (jobCancellationTokens.TryGetValue(jobId, out var cancellationTokenSource))
    {
        cancellationTokenSource.Cancel();
    }

    await UpdateJobAsync(job, jobs, helperPaths, jsonOptions, "cancelled", job.Progress, "Job cancelled by user.", phase: "cancelled");
    return job;
}

static async Task ProcessJobAsync(
    string jobId,
    ConcurrentDictionary<string, ClipJob> jobs,
    ConcurrentDictionary<string, CancellationTokenSource> jobCancellationTokens,
    HelperPaths helperPaths,
    SemaphoreSlim toolingLock,
    JsonSerializerOptions jsonOptions,
    ILogger logger,
    CancellationToken cancellationToken)
{
    if (!jobs.TryGetValue(jobId, out var job))
    {
        return;
    }

    try
    {
        await UpdateJobAsync(job, jobs, helperPaths, jsonOptions, "preparing", 5, "Preparing tools.", phase: "preparing-tools");
        AppendJobLog(helperPaths, job, "Preparing tools.");
        await EnsureToolingAsync(helperPaths, toolingLock, logger, cancellationToken);

        var tempSourceFile = Path.Combine(helperPaths.TempDirectory, $"{job.JobId}.source.mp4");
        var outputFile = BuildOutputPath(job, helperPaths);

        if (File.Exists(tempSourceFile))
        {
            File.Delete(tempSourceFile);
        }

        await UpdateJobAsync(job, jobs, helperPaths, jsonOptions, "downloading", 15, "Downloading video source.", phase: "downloading-video");
        AppendJobLog(helperPaths, job, $"Downloading source from {job.SourcePageUrl}");

        var downloadResult = await RunProcessAsync(
            helperPaths.YtDlpPath,
            $"-f \"bv*+ba/b\" --merge-output-format mp4 --no-playlist -o \"{tempSourceFile}\" \"{job.SourcePageUrl}\"",
            helperPaths.RootDirectory,
            cancellationToken,
            line => HandleYtDlpOutput(helperPaths, job, line));

        if (downloadResult.ExitCode != 0 || !File.Exists(tempSourceFile))
        {
            throw new InvalidOperationException(BuildProcessError("yt-dlp", downloadResult));
        }

        await UpdateJobAsync(job, jobs, helperPaths, jsonOptions, "trimming", 75, "Cutting the selected clip.", phase: "trimming-video");
        AppendJobLog(helperPaths, job, "Source download finished.");

        var ffmpegResult = await RunProcessAsync(
            helperPaths.FfmpegPath,
            $"-y -ss {FormatTimestamp(job.StartTimeSeconds)} -to {FormatTimestamp(job.EndTimeSeconds)} -i \"{tempSourceFile}\" -c:v libx264 -preset veryfast -c:a aac -movflags +faststart \"{outputFile}\"",
            helperPaths.RootDirectory,
            cancellationToken,
            line => HandleFfmpegOutput(helperPaths, job, line));

        if (ffmpegResult.ExitCode != 0 || !File.Exists(outputFile))
        {
            throw new InvalidOperationException(BuildProcessError("ffmpeg", ffmpegResult));
        }

        AppendJobLog(helperPaths, job, $"Clip ready at {outputFile}");
        await UpdateJobAsync(job, jobs, helperPaths, jsonOptions, "completed", 100, "Clip ready.", outputFilePath: outputFile, phase: "completed");

        if (File.Exists(tempSourceFile))
        {
            File.Delete(tempSourceFile);
        }
    }
    catch (OperationCanceledException)
    {
        logger.LogInformation("Clip job {JobId} was cancelled.", job.JobId);
        AppendJobLog(helperPaths, job, "Job cancelled by user.");
        await UpdateJobAsync(job, jobs, helperPaths, jsonOptions, "cancelled", job.Progress, "Job cancelled by user.", phase: "cancelled");
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Clip job {JobId} failed.", job.JobId);
        AppendJobLog(helperPaths, job, $"Job failed: {exception.Message}");
        await UpdateJobAsync(job, jobs, helperPaths, jsonOptions, "failed", job.Progress, "Job failed.", error: exception.Message, phase: "failed");
    }
    finally
    {
        if (jobCancellationTokens.TryRemove(jobId, out var cancellationTokenSource))
        {
            cancellationTokenSource.Dispose();
        }
    }
}

static async Task EnsureToolingAsync(HelperPaths helperPaths, SemaphoreSlim toolingLock, ILogger logger, CancellationToken cancellationToken)
{
    Directory.CreateDirectory(helperPaths.ToolsDirectory);

    await toolingLock.WaitAsync(cancellationToken);
    try
    {
        if (!File.Exists(helperPaths.YtDlpPath))
        {
            logger.LogInformation("Downloading yt-dlp.");
            await DownloadFileAsync(
                new Uri("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"),
                helperPaths.YtDlpPath,
                cancellationToken);
        }

        if (!File.Exists(helperPaths.FfmpegPath))
        {
            logger.LogInformation("Downloading ffmpeg.");
            var zipPath = Path.Combine(helperPaths.TempDirectory, $"ffmpeg-{Guid.NewGuid():N}.zip");
            var extractDirectory = Path.Combine(helperPaths.TempDirectory, $"ffmpeg-{Guid.NewGuid():N}");

            try
            {
                await DownloadFileAsync(
                    new Uri("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"),
                    zipPath,
                    cancellationToken);

                Directory.CreateDirectory(extractDirectory);
                ZipFile.ExtractToDirectory(zipPath, extractDirectory, overwriteFiles: true);

                var extractedFfmpeg = Directory.EnumerateFiles(extractDirectory, "ffmpeg.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(extractedFfmpeg))
                {
                    throw new FileNotFoundException("The ffmpeg archive was downloaded, but ffmpeg.exe was not found.");
                }

                File.Copy(extractedFfmpeg, helperPaths.FfmpegPath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(zipPath);
                TryDeleteDirectory(extractDirectory);
            }
        }
    }
    finally
    {
        toolingLock.Release();
    }
}

static async Task DownloadFileAsync(Uri url, string destinationPath, CancellationToken cancellationToken)
{
    using var httpClient = new HttpClient();
    using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    response.EnsureSuccessStatusCode();

    await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
    await using var destinationStream = File.Create(destinationPath);
    await sourceStream.CopyToAsync(destinationStream, cancellationToken);
}

static string BuildOutputPath(ClipJob job, HelperPaths helperPaths)
{
    var safeTitle = SanitizeFileName(job.VideoTitle);
    var clipRange = $"{FormatFileTimestamp(job.StartTimeSeconds)} to {FormatFileTimestamp(job.EndTimeSeconds)}";
    var fileName = $"{safeTitle} {clipRange}.{job.OutputFormat}";
    return Path.Combine(helperPaths.OutputDirectory, fileName);
}

static string FormatFileTimestamp(int totalSeconds)
{
    return FormatTimestamp(totalSeconds).Replace(':', '-');
}

static string FormatTimestamp(int totalSeconds)
{
    var clampedSeconds = Math.Max(0, totalSeconds);
    var time = TimeSpan.FromSeconds(clampedSeconds);
    return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
}

static string SanitizeFileName(string value)
{
    var invalidCharsPattern = $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]";
    var sanitized = Regex.Replace(value, invalidCharsPattern, " ");
    sanitized = Regex.Replace(sanitized, "\\s+", " ").Trim();
    return string.IsNullOrWhiteSpace(sanitized) ? "YouTube Clip" : sanitized;
}

static string BuildProcessError(string processName, ProcessResult result)
{
    var builder = new StringBuilder();
    builder.AppendLine($"{processName} exited with code {result.ExitCode}.");

    if (!string.IsNullOrWhiteSpace(result.StandardError))
    {
        builder.AppendLine(result.StandardError.Trim());
    }
    else if (!string.IsNullOrWhiteSpace(result.StandardOutput))
    {
        builder.AppendLine(result.StandardOutput.Trim());
    }

    return builder.ToString().Trim();
}

static async Task<ProcessResult> RunProcessAsync(
    string fileName,
    string arguments,
    string workingDirectory,
    CancellationToken cancellationToken,
    Action<string>? onOutput = null)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    using var process = new Process { StartInfo = startInfo };
    var standardOutput = new StringBuilder();
    var standardError = new StringBuilder();

    process.OutputDataReceived += (_, eventArgs) =>
    {
        if (eventArgs.Data is not null)
        {
            standardOutput.AppendLine(eventArgs.Data);
            onOutput?.Invoke(eventArgs.Data);
        }
    };

    process.ErrorDataReceived += (_, eventArgs) =>
    {
        if (eventArgs.Data is not null)
        {
            standardError.AppendLine(eventArgs.Data);
            onOutput?.Invoke(eventArgs.Data);
        }
    };

    if (!process.Start())
    {
        throw new InvalidOperationException($"Could not start process '{fileName}'.");
    }

    using var cancellationRegistration = cancellationToken.Register(() =>
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    });

    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    await process.WaitForExitAsync(cancellationToken);

    return new ProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
}

static void HandleYtDlpOutput(HelperPaths helperPaths, ClipJob job, string line)
{
    AppendJobLog(helperPaths, job, line);

    var match = Regex.Match(line, @"\[download\]\s+(?<percent>\d{1,3}(?:\.\d+)?)%");
    if (!match.Success || !double.TryParse(match.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
    {
        return;
    }

    var mappedProgress = 15 + (int)Math.Round((percent / 100d) * 55d);
    UpdateLiveJobSnapshot(job, "downloading", mappedProgress, $"Downloading video ({percent:0.0}%)", "downloading-video");
}

static void HandleFfmpegOutput(HelperPaths helperPaths, ClipJob job, string line)
{
    AppendJobLog(helperPaths, job, line);

    var match = Regex.Match(line, @"time=(?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?)");
    if (!match.Success || !TimeSpan.TryParse(match.Groups["time"].Value, CultureInfo.InvariantCulture, out var currentTime))
    {
        return;
    }

    var totalClipDurationSeconds = Math.Max(1, job.EndTimeSeconds - job.StartTimeSeconds);
    var trimRatio = Math.Min(currentTime.TotalSeconds / totalClipDurationSeconds, 1);
    var mappedProgress = 75 + (int)Math.Round(trimRatio * 23d);
    UpdateLiveJobSnapshot(job, "trimming", mappedProgress, $"Trimming clip ({trimRatio * 100:0.0}%)", "trimming-video");
}

static void UpdateLiveJobSnapshot(ClipJob job, string status, int progress, string message, string phase)
{
    lock (job)
    {
        if (progress < job.Progress)
        {
            return;
        }

        job.Status = status;
        job.Progress = progress;
        job.Message = message;
        job.Phase = phase;
        job.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}

static void TryDeleteFile(string path)
{
    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    catch
    {
    }
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch
    {
    }
}

static async Task UpdateJobAsync(
    ClipJob job,
    ConcurrentDictionary<string, ClipJob> jobs,
    HelperPaths helperPaths,
    JsonSerializerOptions jsonOptions,
    string status,
    int progress,
    string message,
    string? outputFilePath = null,
    string? error = null,
    string? phase = null)
{
    job.Status = status;
    job.Progress = progress;
    job.Message = message;
    job.OutputFilePath = outputFilePath;
    job.Error = error;
    job.Phase = phase ?? job.Phase;
    job.UpdatedAtUtc = DateTimeOffset.UtcNow;

    if (status is "completed" or "failed")
    {
        job.CompletedAtUtc = DateTimeOffset.UtcNow;
    }

    jobs[job.JobId] = job;
    await PersistJobAsync(job, helperPaths, jsonOptions);
}

static async Task PersistJobAsync(ClipJob job, HelperPaths helperPaths, JsonSerializerOptions jsonOptions)
{
    Directory.CreateDirectory(helperPaths.JobsDirectory);

    var jobPath = Path.Combine(helperPaths.JobsDirectory, $"{job.JobId}.json");
    await using var stream = File.Create(jobPath);
    await JsonSerializer.SerializeAsync(stream, job, jsonOptions);
}

static async Task<ClipJob?> TryLoadJobAsync(string jobId, HelperPaths helperPaths, JsonSerializerOptions jsonOptions)
{
    var jobPath = Path.Combine(helperPaths.JobsDirectory, $"{jobId}.json");
    if (!File.Exists(jobPath))
    {
        return null;
    }

    await using var stream = File.OpenRead(jobPath);
    return await JsonSerializer.DeserializeAsync<ClipJob>(stream, jsonOptions);
}

static void AppendJobLog(HelperPaths helperPaths, ClipJob job, string message)
{
    Directory.CreateDirectory(helperPaths.LogsDirectory);

    var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}".TrimEnd();
    lock (job)
    {
        job.RecentLogLines.Add(line);
        if (job.RecentLogLines.Count > 18)
        {
            job.RecentLogLines.RemoveAt(0);
        }
    }

    if (!string.IsNullOrWhiteSpace(job.LogFilePath))
    {
        File.AppendAllText(job.LogFilePath, line + Environment.NewLine);
    }
}

sealed class HelperPaths
{
    private HelperPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        JobsDirectory = Path.Combine(rootDirectory, "jobs");
        LogsDirectory = Path.Combine(rootDirectory, "logs");
        OutputDirectory = Path.Combine(rootDirectory, "output");
        TempDirectory = Path.Combine(rootDirectory, "temp");
        ToolsDirectory = Path.Combine(rootDirectory, "tools");
        YtDlpPath = Path.Combine(ToolsDirectory, "yt-dlp.exe");
        FfmpegPath = Path.Combine(ToolsDirectory, "ffmpeg.exe");

        Directory.CreateDirectory(JobsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(OutputDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(ToolsDirectory);
    }

    public string RootDirectory { get; }
    public string JobsDirectory { get; }
    public string LogsDirectory { get; }
    public string OutputDirectory { get; }
    public string TempDirectory { get; }
    public string ToolsDirectory { get; }
    public string YtDlpPath { get; }
    public string FfmpegPath { get; }
    public bool ToolingReady => File.Exists(YtDlpPath) && File.Exists(FfmpegPath);

    public static HelperPaths Create()
    {
        return new HelperPaths(AppContext.BaseDirectory);
    }
}

sealed class ClipJob
{
    public string JobId { get; set; } = string.Empty;
    public string SourcePageUrl { get; set; } = string.Empty;
    public string VideoTitle { get; set; } = string.Empty;
    public int StartTimeSeconds { get; set; }
    public int EndTimeSeconds { get; set; }
    public string OutputFormat { get; set; } = "mp4";
    public string Status { get; set; } = "queued";
    public string Phase { get; set; } = "queued";
    public int Progress { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? OutputFilePath { get; set; }
    public string? LogFilePath { get; set; }
    public List<string> RecentLogLines { get; set; } = [];
    public string? Error { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

sealed record CreateJobRequest(
    string SourcePageUrl,
    string? VideoTitle,
    int StartTimeSeconds,
    int EndTimeSeconds,
    string? OutputFormat);

sealed record JobAcceptedResponse(string JobId, string Status, int Progress);

sealed record HealthResponse(
    bool Ok,
    string Version,
    bool HelperOnline,
    bool ToolingReady,
    int ActiveJobCount,
    string OutputDirectory);

sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
