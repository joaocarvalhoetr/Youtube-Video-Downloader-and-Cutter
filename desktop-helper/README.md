# LocalClipHelper

`LocalClipHelper` is a Windows desktop companion for the Chrome extension.

## Responsibilities

- Listen on `http://127.0.0.1:48721`
- Accept clip jobs from the extension
- Download the source video with `yt-dlp`
- Trim the selected range with `ffmpeg`
- Save the exported clip into the user's local output folder

## HTTP API

### `GET /health`

Returns helper status and tool availability.

Example response:

```json
{
  "ok": true,
  "version": "1.0.0",
  "helperOnline": true,
  "toolingReady": true,
  "activeJobCount": 1,
  "outputDirectory": "C:\\Users\\Name\\AppData\\Local\\YoutubeClipHelper\\output"
}
```

### `POST /jobs`

Creates a new download + trim job.

Request body:

```json
{
  "sourcePageUrl": "https://www.youtube.com/watch?v=example",
  "videoTitle": "Sample video",
  "startTimeSeconds": 30,
  "endTimeSeconds": 90,
  "outputFormat": "mp4"
}
```

Response:

```json
{
  "jobId": "0eae61fd14d24450b7420e9a93b7df7e",
  "status": "queued",
  "progress": 0
}
```

### `GET /jobs/{jobId}`

Returns the current job state.

Example response:

```json
{
  "jobId": "0eae61fd14d24450b7420e9a93b7df7e",
  "status": "completed",
  "progress": 100,
  "phase": "completed",
  "message": "Clip ready.",
  "recentLogLines": [
    "[12:03:22] Downloading source from https://www.youtube.com/watch?v=example",
    "[12:03:45] [download] 100% of 12.30MiB",
    "[12:03:50] Clip ready at C:\\Users\\Name\\AppData\\Local\\YoutubeClipHelper\\output\\Sample.mp4"
  ],
  "outputFilePath": "C:\\Users\\Name\\AppData\\Local\\YoutubeClipHelper\\output\\Sample video 00-30 to 01-30.mp4",
  "error": null
}
```

### `GET /jobs/{jobId}/log`

Returns the full plain-text log for a single job.

### `GET /jobs/{jobId}/download`

Streams the final clip to Chrome's download manager and deletes the helper's local copy after delivery.

## Installer flow

The extension downloads `YoutubeClipHelperSetup.exe`.

When the user runs it, the installer:

- copies the helper into `%LocalAppData%\\YoutubeClipHelper`
- publishes the helper as a self-contained `win-x64` app, so the setup does not need to fetch .NET during install
- creates a Startup shortcut for the helper instead of launching it through a hidden script host
- starts the helper immediately

## Notes

- The helper binds only to `127.0.0.1`
- The Chrome extension never handles the heavy video processing directly
- `yt-dlp.exe` and `ffmpeg.exe` are downloaded on demand by the helper if they are missing
- The helper creates a Windows tray icon with shortcuts to open the output folder, open logs, cancel active jobs and exit the helper
- Completed clips are handed off to Chrome downloads and then removed from the helper's local output folder
