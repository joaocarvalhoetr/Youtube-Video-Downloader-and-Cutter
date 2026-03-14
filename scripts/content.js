function getYoutubeVideoInfo() {
    const video = document.querySelector("video");
    const titleElement = document.querySelector("h1.ytd-watch-metadata yt-formatted-string")
        || document.querySelector("h1.title yt-formatted-string")
        || document.querySelector("title");

    if (!video) {
        return {
            ok: false,
            error: "No HTML video element found on this page.",
        };
    }

    if (!Number.isFinite(video.duration) || video.duration <= 0) {
        return {
            ok: false,
            error: "Video duration is not ready yet.",
        };
    }

    return {
        ok: true,
        title: titleElement?.textContent?.trim() || document.title.replace(" - YouTube", "").trim(),
        duration: Math.floor(video.duration),
        currentTime: Math.floor(video.currentTime || 0),
    };
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (message?.type !== "GET_VIDEO_INFO") {
        return false;
    }

    sendResponse(getYoutubeVideoInfo());
    return false;
});