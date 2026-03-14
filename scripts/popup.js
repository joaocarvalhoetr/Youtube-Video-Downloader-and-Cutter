const HELPER_BASE_URL = "http://127.0.0.1:48721";
const INSTALLER_ASSET_PATH = "desktop-helper/installer/build/YoutubeClipHelperSetup.exe";
const INSTALLER_FILENAME = "YoutubeClipHelperSetup.exe";
const ACTIVE_JOB_STORAGE_KEY = "youtubeClipHelper.activeJobId";

const state = {
    videoLength: 0,
    startTime: 0,
    endTime: 0,
    title: "Loading current video...",
    pageUrl: "",
    helperOnline: false,
    toolingReady: false,
    currentJobId: null,
    isBusy: false,
};

const elements = {
    title: document.getElementById("video-title"),
    helperStatus: document.getElementById("helper-status"),
    installHelperButton: document.getElementById("install-helper-button"),
    downloadClipButton: document.getElementById("download-clip-button"),
    cancelJobButton: document.getElementById("cancel-job-button"),
    timelineBar: document.getElementById("timeline-bar"),
    timelineSelection: document.getElementById("timeline-selection"),
    startHandle: document.getElementById("start-handle"),
    endHandle: document.getElementById("end-handle"),
    startInput: document.getElementById("start-time"),
    endInput: document.getElementById("end-time"),
    startLabel: document.getElementById("current-start-label"),
    endLabel: document.getElementById("current-end-label"),
    videoLengthLabel: document.getElementById("video-length-label"),
    selectedRangeLabel: document.getElementById("selected-range-label"),
    jobProgressLabel: document.getElementById("job-progress-label"),
    jobLogOutput: document.getElementById("job-log-output"),
    statusMessage: document.getElementById("status-message"),
};

let activeHandle = null;
let jobPollTimeoutId = null;

function clamp(value, min, max) {
    return Math.min(Math.max(value, min), max);
}

function formatTime(totalSeconds) {
    const safeSeconds = Math.max(0, Math.floor(totalSeconds));
    const minutes = Math.floor(safeSeconds / 60);
    const seconds = safeSeconds % 60;

    return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}

function parseTimeInput(value) {
    const trimmedValue = String(value).trim();

    if (!trimmedValue) {
        return Number.NaN;
    }

    const segments = trimmedValue.split(":");

    if (segments.length === 1) {
        const secondsOnly = Number(segments[0]);
        return Number.isFinite(secondsOnly) ? secondsOnly : Number.NaN;
    }

    if (segments.length !== 2) {
        return Number.NaN;
    }

    const minutes = Number(segments[0]);
    const seconds = Number(segments[1]);

    if (!Number.isFinite(minutes) || !Number.isFinite(seconds) || seconds < 0 || seconds >= 60) {
        return Number.NaN;
    }

    return (minutes * 60) + seconds;
}

function secondsToPercent(seconds) {
    if (state.videoLength <= 0) {
        return 0;
    }

    return (seconds / state.videoLength) * 100;
}

function setBusy(isBusy) {
    state.isBusy = isBusy;
    elements.downloadClipButton.disabled = isBusy;
    elements.cancelJobButton.disabled = !isBusy || !state.currentJobId;
    elements.downloadClipButton.classList.toggle("opacity-60", isBusy);
    elements.downloadClipButton.classList.toggle("cursor-not-allowed", isBusy);
    elements.cancelJobButton.classList.toggle("opacity-60", !isBusy || !state.currentJobId);
    elements.cancelJobButton.classList.toggle("cursor-not-allowed", !isBusy || !state.currentJobId);
}

function persistActiveJobId() {
    if (state.currentJobId) {
        localStorage.setItem(ACTIVE_JOB_STORAGE_KEY, state.currentJobId);
    } else {
        localStorage.removeItem(ACTIVE_JOB_STORAGE_KEY);
    }
}

function renderJobLogs(lines = [], fallbackMessage = "No job logs yet.") {
    if (!Array.isArray(lines) || lines.length === 0) {
        elements.jobLogOutput.textContent = fallbackMessage;
        return;
    }

    elements.jobLogOutput.textContent = lines.join("\n");
    elements.jobLogOutput.scrollTop = elements.jobLogOutput.scrollHeight;
}

function getSuggestedDownloadFilename(job) {
    const outputFilePath = job?.outputFilePath || "";
    const pathSegments = outputFilePath.split(/[/\\]/);
    return pathSegments[pathSegments.length - 1] || "youtube-clip.mp4";
}

function setVideoData({ title, duration, pageUrl = "" }) {
    const safeDuration = Math.max(1, Math.floor(duration || 0));

    state.title = title || "Untitled video";
    state.videoLength = safeDuration;
    state.startTime = 0;
    state.endTime = safeDuration;
    state.pageUrl = pageUrl;
    render();
}

function render() {
    const startPercent = secondsToPercent(state.startTime);
    const endPercent = secondsToPercent(state.endTime);

    elements.title.textContent = state.title;
    elements.startInput.value = formatTime(state.startTime);
    elements.endInput.value = formatTime(state.endTime);
    elements.startLabel.textContent = formatTime(state.startTime);
    elements.endLabel.textContent = formatTime(state.endTime);
    elements.videoLengthLabel.textContent = formatTime(state.videoLength);
    elements.selectedRangeLabel.textContent = `${formatTime(state.startTime)} - ${formatTime(state.endTime)}`;
    elements.helperStatus.textContent = state.helperOnline
        ? (state.toolingReady ? "Connected and ready" : "Connected, tools will download on first job")
        : "Not installed or not running";

    elements.timelineSelection.style.left = `${startPercent}%`;
    elements.timelineSelection.style.width = `${Math.max(endPercent - startPercent, 0)}%`;
    elements.startHandle.style.left = `${startPercent}%`;
    elements.endHandle.style.left = `${endPercent}%`;
}

function updateTime(whichHandle, rawSeconds) {
    const safeSeconds = clamp(Math.round(rawSeconds), 0, state.videoLength);

    if (whichHandle === "start") {
        state.startTime = Math.min(safeSeconds, state.endTime);
    } else {
        state.endTime = Math.max(safeSeconds, state.startTime);
    }

    render();
}

function updateFromPointer(clientX) {
    if (!activeHandle || state.videoLength <= 0) {
        return;
    }

    const rect = elements.timelineBar.getBoundingClientRect();
    const ratio = clamp((clientX - rect.left) / rect.width, 0, 1);
    updateTime(activeHandle, ratio * state.videoLength);
}

function handleManualInput(whichHandle, value) {
    const parsedValue = parseTimeInput(value);

    if (Number.isNaN(parsedValue)) {
        render();
        return;
    }

    updateTime(whichHandle, parsedValue);
}

async function fetchJson(path, options = {}, timeoutMs = 3000) {
    const controller = new AbortController();
    const timeoutId = window.setTimeout(() => controller.abort(), timeoutMs);

    try {
        const response = await fetch(`${HELPER_BASE_URL}${path}`, {
            ...options,
            signal: controller.signal,
        });

        if (!response.ok) {
            const errorText = await response.text();
            throw new Error(errorText || `Request failed with status ${response.status}.`);
        }

        return await response.json();
    } finally {
        window.clearTimeout(timeoutId);
    }
}

async function refreshHelperStatus() {
    try {
        const health = await fetchJson("/health");
        state.helperOnline = Boolean(health.ok);
        state.toolingReady = Boolean(health.toolingReady);
    } catch {
        state.helperOnline = false;
        state.toolingReady = false;
    }

    render();
}

async function downloadHelperInstaller() {
    try {
        const downloadUrl = chrome.runtime.getURL(INSTALLER_ASSET_PATH);
        await chrome.downloads.download({
            url: downloadUrl,
            filename: INSTALLER_FILENAME,
            saveAs: true,
        });

        elements.statusMessage.textContent = "Installer downloaded. Run the EXE once, then reopen the popup.";
    } catch (error) {
        elements.statusMessage.textContent = "Could not download the installer from the extension package.";
        console.error(error);
    }
}

async function getCurrentTabVideoData() {
    try {
        const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });

        if (!tab?.id || !tab.url) {
            throw new Error("No active tab found.");
        }

        const response = await chrome.tabs.sendMessage(tab.id, { type: "GET_VIDEO_INFO" });

        if (!response?.ok) {
            throw new Error(response?.error || "Could not read the video from this tab.");
        }

        setVideoData({
            title: response.title,
            duration: response.duration,
            pageUrl: tab.url,
        });

        elements.statusMessage.textContent = "Video loaded. Drag the handles or type the times as mm:ss.";
    } catch (error) {
        setVideoData({
            title: "No YouTube video detected",
            duration: 600,
        });

        elements.statusMessage.textContent = "Open a YouTube watch page and reopen the popup to load the real video duration.";
        console.error(error);
    }
}

async function submitClipJob() {
    if (!state.helperOnline) {
        elements.statusMessage.textContent = "Install and launch the desktop helper before starting a download.";
        return;
    }

    if (!state.pageUrl) {
        elements.statusMessage.textContent = "No YouTube watch page detected in the active tab.";
        return;
    }

    setBusy(true);
    elements.jobProgressLabel.textContent = "Submitting job...";

    try {
        const job = await fetchJson("/jobs", {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
            },
            body: JSON.stringify({
                sourcePageUrl: state.pageUrl,
                videoTitle: state.title,
                startTimeSeconds: state.startTime,
                endTimeSeconds: state.endTime,
                outputFormat: "mp4",
            }),
        }, 10000);

        state.currentJobId = job.jobId;
        persistActiveJobId();
        setBusy(true);
        elements.statusMessage.textContent = "Job started in the desktop helper.";
        pollJobUntilFinished(job.jobId);
    } catch (error) {
        setBusy(false);
        elements.jobProgressLabel.textContent = "Failed to start";
        elements.statusMessage.textContent = "Could not create the local download job.";
        renderJobLogs([], "The helper did not return job logs.");
        console.error(error);
    }
}

async function cancelCurrentJob() {
    if (!state.currentJobId) {
        return;
    }

    try {
        const job = await fetchJson(`/jobs/${state.currentJobId}`, {
            method: "DELETE",
        }, 5000);

        window.clearTimeout(jobPollTimeoutId);
        state.currentJobId = null;
        persistActiveJobId();
        setBusy(false);
        elements.jobProgressLabel.textContent = `${job.phase || job.status} (${job.progress}%)`;
        elements.statusMessage.textContent = "Job cancelled.";
        renderJobLogs(job.recentLogLines, "Job cancelled before any logs were produced.");
    } catch (error) {
        elements.statusMessage.textContent = "Could not cancel the running job.";
        console.error(error);
    }
}

async function startChromeDownload(job) {
    const downloadUrl = `${HELPER_BASE_URL}/jobs/${job.jobId}/download`;
    const suggestedFilename = getSuggestedDownloadFilename(job);

    await chrome.downloads.download({
        url: downloadUrl,
        filename: suggestedFilename,
        saveAs: true,
    });
}

async function pollJobUntilFinished(jobId) {
    window.clearTimeout(jobPollTimeoutId);

    try {
        const job = await fetchJson(`/jobs/${jobId}`, {}, 5000);
        elements.jobProgressLabel.textContent = `${job.phase || job.status} (${job.progress}%)`;
        elements.statusMessage.textContent = job.message || "Job running in the desktop helper.";
        renderJobLogs(job.recentLogLines, "Waiting for the helper to emit logs.");

        if (job.status === "completed") {
            state.currentJobId = null;
            persistActiveJobId();
            setBusy(false);
            elements.jobProgressLabel.textContent = "Starting Chrome download...";
            elements.statusMessage.textContent = "Sending the clip to Chrome downloads.";

            try {
                await startChromeDownload(job);
                elements.statusMessage.textContent = "Chrome download started. The helper copy will be deleted automatically after delivery.";
            } catch (error) {
                elements.statusMessage.textContent = "The clip is ready, but Chrome could not start the download.";
                console.error(error);
            }
            return;
        }

        if (job.status === "failed" || job.status === "cancelled") {
            state.currentJobId = null;
            persistActiveJobId();
            setBusy(false);
            elements.statusMessage.textContent = job.status === "cancelled"
                ? "Job cancelled."
                : (job.error || "The local helper failed to export the clip.");
            return;
        }

        jobPollTimeoutId = window.setTimeout(() => {
            pollJobUntilFinished(jobId);
        }, 1500);
    } catch (error) {
        setBusy(false);
        elements.jobProgressLabel.textContent = "Connection lost";
        elements.statusMessage.textContent = "Lost connection to the desktop helper while polling the job.";
        console.error(error);
    }
}

function resumeExistingJobIfNeeded() {
    const savedJobId = localStorage.getItem(ACTIVE_JOB_STORAGE_KEY);
    if (!savedJobId) {
        return;
    }

    state.currentJobId = savedJobId;
    setBusy(true);
    elements.jobProgressLabel.textContent = "Reconnecting...";
    elements.statusMessage.textContent = "Resuming the current backend job.";
    renderJobLogs([], "Reconnecting to the current backend job...");
    pollJobUntilFinished(savedJobId);
}

elements.installHelperButton.addEventListener("click", () => {
    downloadHelperInstaller();
});

elements.downloadClipButton.addEventListener("click", () => {
    submitClipJob();
});

elements.cancelJobButton.addEventListener("click", () => {
    cancelCurrentJob();
});

elements.startHandle.addEventListener("pointerdown", () => {
    activeHandle = "start";
});

elements.endHandle.addEventListener("pointerdown", () => {
    activeHandle = "end";
});

window.addEventListener("pointermove", (event) => {
    updateFromPointer(event.clientX);
});

window.addEventListener("pointerup", () => {
    activeHandle = null;
});

elements.timelineBar.addEventListener("click", (event) => {
    const rect = elements.timelineBar.getBoundingClientRect();
    const clickRatio = clamp((event.clientX - rect.left) / rect.width, 0, 1);
    const clickedSeconds = clickRatio * state.videoLength;
    const distanceToStart = Math.abs(clickedSeconds - state.startTime);
    const distanceToEnd = Math.abs(clickedSeconds - state.endTime);

    updateTime(distanceToStart <= distanceToEnd ? "start" : "end", clickedSeconds);
});

elements.startInput.addEventListener("change", (event) => {
    handleManualInput("start", event.target.value);
});

elements.endInput.addEventListener("change", (event) => {
    handleManualInput("end", event.target.value);
});

setVideoData({
    title: "Loading current video...",
    duration: 600,
});

render();
setBusy(false);
renderJobLogs();
refreshHelperStatus();
getCurrentTabVideoData();
resumeExistingJobIfNeeded();
