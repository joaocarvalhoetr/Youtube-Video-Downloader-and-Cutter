# Youtube Clip Helper

Chrome extension + local Windows helper for selecting a clip range from the current YouTube video, downloading the video through the local helper, trimming the selected section, and handing the final file off to Chrome's native downloads flow.

## Overview

Youtube Clip Helper is a Chrome extension backed by a local Windows helper application.

It lets you:

- read the current YouTube video's title and duration
- select a clip range from the popup
- download and trim the video through the local helper
- receive the final clip through Chrome's normal download flow

## Main components

- Chrome extension popup UI
- content script for reading the current YouTube video title and duration
- local Windows helper built with `.NET`
- installer builder for the helper
- tray icon support, job cancellation, and job logs

## Install on Chrome

### 1. Load the extension in Chrome

1. Open `chrome://extensions`
2. Turn on `Developer mode`
3. Click `Load unpacked`
4. Select this project root folder

### 2. Build the local helper installer

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\desktop-helper\installer\build-installer.ps1
```

This generates:

```text
desktop-helper/installer/build/YoutubeClipHelperSetup.exe
```

### 3. Install the local helper

1. Run `desktop-helper/installer/build/YoutubeClipHelperSetup.exe`
2. Let the installer copy the helper into `%LocalAppData%\YoutubeClipHelper`
3. Wait for the helper tray icon to appear in Windows

### 4. Use the extension

1. Open a YouTube video page
2. Open the extension popup from Chrome
3. Select the start and end time
4. Click `Download Selected Clip`
5. When the job finishes, Chrome will download the final clip normally

## Development

If you change the desktop helper code, rebuild the installer with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\desktop-helper\installer\build-installer.ps1
```

## Notes

- The helper installs itself into `%LocalAppData%\YoutubeClipHelper`
- Final clips are delivered through Chrome downloads
- After delivery, the helper deletes its own local copy of the exported clip
