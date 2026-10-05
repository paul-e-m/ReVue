// Manual integration harness: node tests/browser-cache-harness.js /path/to/test.mp4
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");

const videoPath = process.argv[2];
if (!videoPath) throw new Error("Pass an MP4 fixture path");
const videoSize = fs.statSync(videoPath).size;
const workerPath = path.join(__dirname, "../wwwroot/video-cache-sw.js");
let videoBytesSent = 0;

const server = http.createServer((request, response) => {
    const url = new URL(request.url, "http://127.0.0.1:18087");
    if (url.pathname === "/video-cache-sw.js") {
        response.setHeader("Content-Type", "text/javascript");
        fs.createReadStream(workerPath).pipe(response);
        return;
    }
    if (url.pathname === "/api/sessions/AA1001/videos") {
        response.setHeader("Content-Type", "application/json");
        response.end(JSON.stringify([{ id: "test", sizeBytes: videoSize }]));
        return;
    }
    if (url.pathname === "/api/sessions/AA1001/videos/test/content") {
        const match = /^bytes=(\d+)-(\d*)$/.exec(request.headers.range || "");
        if (!match) { response.writeHead(400); response.end(); return; }
        const start = Number(match[1]);
        const end = Math.min(match[2] ? Number(match[2]) : videoSize - 1, videoSize - 1);
        response.writeHead(206, {
            "Content-Type": "video/mp4",
            "Content-Range": `bytes ${start}-${end}/${videoSize}`,
            "Content-Length": end - start + 1,
            "Accept-Ranges": "bytes",
            "Cache-Control": "no-store"
        });
        fs.createReadStream(videoPath, { start, end })
            .on("data", chunk => { videoBytesSent += chunk.length; })
            .pipe(response);
        return;
    }
    if (url.pathname === "/stats") {
        response.setHeader("Content-Type", "application/json");
        response.end(JSON.stringify({ videoBytesSent }));
        return;
    }
    if (url.pathname !== "/") { response.writeHead(404); response.end(); return; }
    response.setHeader("Content-Type", "text/html");
    response.end(`<!doctype html><meta charset="utf-8"><title>ReVue cache test</title>
<video id="video" width="640" height="360" muted controls playsinline></video><pre id="stats">Starting…</pre>
<script>
const video = document.getElementById("video");
const stats = document.getElementById("stats");
const source = "/api/sessions/AA1001/videos/test/content?viewer=browser-test";
const prepare = new URLSearchParams(location.search).has("prepare");
let active = true;
let phase = prepare ? "ready" : "recording";
let recordAtMs = 0;
let recordStartDelayMs = null;
function ahead() {
    for (let i = 0; i < video.buffered.length; i++) {
        if (video.buffered.start(i) <= video.currentTime + .05 && video.buffered.end(i) >= video.currentTime)
            return video.buffered.end(i) - video.currentTime;
    }
    return 0;
}
function report() {
    navigator.serviceWorker.controller?.postMessage({
        type: "video-buffer", url: new URL(source, location.href).href,
        duration: Number.isFinite(video.duration) ? video.duration : 0,
        position: video.currentTime, bufferedAhead: ahead(), active, mode: phase,
        recordedEnd: null
    });
}
(async () => {
    if (new URLSearchParams(location.search).has("clear"))
        await caches.delete("revue-video-chunks-v3");
    await navigator.serviceWorker.register("/video-cache-sw.js?v=20261002-4", { scope: "/" });
    if (!navigator.serviceWorker.controller) await new Promise(resolve =>
        navigator.serviceWorker.addEventListener("controllerchange", resolve, { once: true }));
    video.preload = "auto";
    video.src = source;
    video.addEventListener("loadedmetadata", report);
    video.addEventListener("seeked", report);
    video.addEventListener("playing", () => {
        if (recordAtMs && recordStartDelayMs === null)
            recordStartDelayMs = Math.round(performance.now() - recordAtMs);
    });
    setInterval(report, 100);
    setInterval(async () => {
        const {videoBytesSent} = await (await fetch("/stats", {cache:"no-store"})).json();
        const cache = await caches.open("revue-video-chunks-v3");
        const keys = await cache.keys();
        const cachedBytes = keys.reduce((n, key) => {
            const u = new URL(key.url);
            return n + (u.searchParams.has("revue_chunk") ? Number(u.searchParams.get("bytes")) : 0);
        }, 0);
        stats.textContent = JSON.stringify({position: video.currentTime.toFixed(2),
            duration: video.duration.toFixed(2), bufferedAhead: ahead().toFixed(2),
            videoBytesSent, cachedBytes, paused: video.paused, phase,
            recordStartDelayMs}, null, 2);
    }, 500);
    if (prepare) {
        await new Promise(resolve => video.addEventListener("loadedmetadata", resolve, {once:true}));
        phase = "preparing";
        active = true;
        video.currentTime = 5;
        report();
        await new Promise(resolve => setTimeout(resolve,
            new URLSearchParams(location.search).get("prepare") === "long" ? 10000 : 2500));
        phase = "recording";
        recordAtMs = performance.now();
        await video.play();
    } else {
        recordAtMs = performance.now();
        await video.play();
    }
})().catch(error => { stats.textContent = String(error); });
</script>`);
});
server.listen(18087, "127.0.0.1", () => console.log("http://127.0.0.1:18087/"));
