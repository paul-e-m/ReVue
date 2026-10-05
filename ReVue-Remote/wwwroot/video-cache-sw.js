// Keep small, stable ranges so a seek after recording reuses the exact bytes
// downloaded during recording. The media element reports its time buffer.
const CACHE_NAME = "revue-video-chunks-v3";
const LIVE_CACHE_NAME = "revue-live-events-v1";
const CHUNK_BYTES = 64 * 1024;
const READY_AHEAD_SECONDS = 5;
const RECORDING_AHEAD_SECONDS = 9;
const BOOTSTRAP_BYTES = 4 * 1024 * 1024;
const pendingChunks = new Map();
const cacheGenerations = new Map();
const bufferStates = new Map();
const bufferWaiters = new Map();
const bootstrapUsage = new Map();

self.addEventListener("install", event => event.waitUntil(self.skipWaiting()));
self.addEventListener("activate", event => event.waitUntil((async () => {
    await caches.delete("revue-video-chunks-v1");
    await caches.delete("revue-video-chunks-v2");
    await self.clients.claim();
})()));

self.addEventListener("message", event => {
    if (event.data?.type === "purge-live-event" && event.ports?.[0]) {
        event.waitUntil((async () => {
            try {
                const prefix = String(event.data.prefix || "");
                if (!/^\/api\/sessions\/[A-Z0-9]{6}\/live\/events\/[a-f0-9]{32}\/$/.test(prefix))
                    throw new Error("Invalid live event cache target");
                const cache = await caches.open(LIVE_CACHE_NAME);
                await Promise.all((await cache.keys()).filter(request =>
                    new URL(request.url).pathname.startsWith(prefix)).map(request => cache.delete(request)));
                event.ports[0].postMessage({ ok: true });
            } catch { event.ports[0].postMessage({ ok: false }); }
        })());
        return;
    }
    if (event.data?.type === "video-buffer") {
        const url = new URL(event.data.url);
        if (url.origin !== self.location.origin ||
            !/^\/api\/sessions\/[^/]+\/videos\/[^/]+\/content$/.test(url.pathname)) return;
        const key = `${event.source?.id || ""}:${url.pathname}`;
        const previous = bufferStates.get(key);
        bufferStates.set(key, {
            duration: Math.max(0, Number(event.data.duration) || 0),
            bufferedAhead: Math.max(0, Number(event.data.bufferedAhead) || 0),
            position: Math.max(0, Number(event.data.position) || 0),
            recordedEnd: event.data.recordedEnd != null && Number.isFinite(Number(event.data.recordedEnd))
                ? Math.max(0, Number(event.data.recordedEnd)) : null,
            mode: String(event.data.mode || ""),
            active: event.data.active === true,
            usedBytes: 0,
            revision: (previous?.revision || 0) + 1
        });
        for (const wake of bufferWaiters.get(key) || []) wake();
        bufferWaiters.delete(key);
        return;
    }
    if (event.data?.type !== "purge-video" || !event.ports?.[0]) return;
    event.waitUntil((async () => {
        try {
            const url = new URL(event.data.url);
            if (url.origin !== self.location.origin ||
                !/^\/api\/sessions\/[^/]+\/videos\/[^/]+\/content$/.test(url.pathname)) {
                throw new Error("Invalid cache target");
            }
            cacheGenerations.set(url.pathname, (cacheGenerations.get(url.pathname) || 0) + 1);
            for (const key of pendingChunks.keys()) {
                if (new URL(key).pathname === url.pathname) pendingChunks.delete(key);
            }
            for (const [key, waiters] of bufferWaiters) {
                if (key.endsWith(`:${url.pathname}`)) {
                    for (const wake of waiters) wake();
                    bufferWaiters.delete(key);
                }
            }
            const cache = await caches.open(CACHE_NAME);
            const keys = await cache.keys();
            await Promise.all(keys.filter(key => new URL(key.url).pathname === url.pathname)
                .map(key => cache.delete(key)));
            for (const key of bufferStates.keys()) {
                if (key.endsWith(`:${url.pathname}`)) bufferStates.delete(key);
            }
            for (const key of bootstrapUsage.keys()) {
                if (key.endsWith(`:${url.pathname}`)) bootstrapUsage.delete(key);
            }
            event.ports[0].postMessage({ ok: true });
        } catch {
            event.ports[0].postMessage({ ok: false });
        }
    })());
});

self.addEventListener("fetch", event => {
    const url = new URL(event.request.url);
    if (event.request.method === "GET" && url.origin === self.location.origin &&
        /^\/api\/sessions\/[A-Z0-9]{6}\/live\/events\/[a-f0-9]{32}\/(?:init\.mp4|seg_[0-9]{6}\.m4s)$/.test(url.pathname)) {
        event.respondWith((async () => {
            const cache = await caches.open(LIVE_CACHE_NAME);
            // A reload gets a new per-page transfer ID. Event media is the
            // same for every viewer, so keep its cached bytes across reloads.
            const cached = await cache.match(event.request, { ignoreSearch: true });
            if (cached) return cached;
            const response = await fetch(event.request);
            if (response.ok) {
                try { await cache.put(`${url.origin}${url.pathname}`, response.clone()); } catch { }
            }
            return response;
        })());
        return;
    }
    if (event.request.method !== "GET" || url.origin !== self.location.origin ||
        !/^\/api\/sessions\/[^/]+\/videos\/[^/]+\/content$/.test(url.pathname)) return;
    event.respondWith(serveVideo(event.request, event.clientId));
});

function waitForBufferReport(key, signal) {
    return new Promise(resolve => {
        const waiters = bufferWaiters.get(key) || new Set();
        const wake = () => {
            waiters.delete(wake);
            signal?.removeEventListener("abort", wake);
            resolve();
        };
        waiters.add(wake);
        bufferWaiters.set(key, waiters);
        signal?.addEventListener("abort", wake, { once: true });
    });
}

async function waitForAllowance(url, clientId, total, length, signal, generation) {
    const key = `${clientId || ""}:${url.pathname}`;
    while (true) {
        if (signal?.aborted) throw new Error("Media request cancelled");
        if ((cacheGenerations.get(url.pathname) || 0) !== generation)
            throw new Error("Video selection cleared");
        const state = bufferStates.get(key);
        if (state?.duration > 0) {
            const aheadLimit = state.mode === "recording"
                ? RECORDING_AHEAD_SECONDS : READY_AHEAD_SECONDS;
            const beforeRecordedEnd = state.recordedEnd === null ||
                state.position + state.bufferedAhead < state.recordedEnd - 0.05;
            // Allow a short burst toward the target. One tenth of a second per
            // report would make the initial five-second preload take five
            // seconds even on a fast connection.
            const secondsToFetch = Math.min(0.5, Math.max(0.1, aheadLimit - state.bufferedAhead));
            const budget = Math.max(length, Math.min(512 * 1024, total / state.duration * secondsToFetch));
            if (state.active && beforeRecordedEnd && state.bufferedAhead < aheadLimit &&
                state.usedBytes + length <= budget) {
                state.usedBytes += length;
                return;
            }
        } else if (state?.active) {
            const spent = bootstrapUsage.get(key) || 0;
            if (spent + length <= BOOTSTRAP_BYTES) {
                bootstrapUsage.set(key, spent + length);
                return;
            }
        }
        await waitForBufferReport(key, signal);
    }
}

function cacheKey(url, kind, start = 0, length = 0) {
    return `${url.origin}${url.pathname}?revue_${kind}=${start}&bytes=${length}`;
}

async function videoLength(url, cache) {
    const saved = await cache.match(cacheKey(url, "meta"));
    if (saved) {
        const length = Number(await saved.text());
        if (Number.isSafeInteger(length) && length > 0) return length;
    }

    const generation = cacheGenerations.get(url.pathname) || 0;
    const segments = url.pathname.split("/");
    const sessionCode = segments[3];
    const videoId = segments[5];
    const listUrl = `${url.origin}/api/sessions/${sessionCode}/videos`;
    const response = await fetch(listUrl, { cache: "no-store" });
    if (!response.ok) throw new Error("Video metadata unavailable");
    const videos = await response.json();
    const length = Number(videos.find(video => video.id === videoId)?.sizeBytes);
    if (!Number.isSafeInteger(length) || length <= 0) throw new Error("Invalid video length");
    try {
        if ((cacheGenerations.get(url.pathname) || 0) === generation) {
            const key = cacheKey(url, "meta");
            await cache.put(key, new Response(String(length)));
            if ((cacheGenerations.get(url.pathname) || 0) !== generation)
                await cache.delete(key);
        }
    } catch {
        // Metadata can still be used for this request when storage is full.
    }
    return length;
}

async function videoChunk(url, start, total, size, cache, clientId, signal) {
    const length = Math.min(size, total - start);
    const key = cacheKey(url, "chunk", start, length);
    const saved = await cache.match(key);
    if (saved) return new Uint8Array(await saved.arrayBuffer());
    if (pendingChunks.has(key)) return pendingChunks.get(key);

    const generation = cacheGenerations.get(url.pathname) || 0;
    const pending = (async () => {
        await waitForAllowance(url, clientId, total, length, signal, generation);
        const end = start + length - 1;
        const response = await fetch(url.toString(), {
            cache: "no-store",
            headers: { Range: `bytes=${start}-${end}` }
        });
        if (response.status !== 206 ||
            response.headers.get("Content-Range") !== `bytes ${start}-${end}/${total}`) {
            throw new Error("Video server did not return the requested range");
        }
        const bytes = new Uint8Array(await response.arrayBuffer());
        if (bytes.byteLength !== length) throw new Error("Incomplete video range");
        if ((cacheGenerations.get(url.pathname) || 0) === generation) {
            try {
                await cache.put(key, new Response(bytes, { headers: { "Content-Type": "application/octet-stream" } }));
                if ((cacheGenerations.get(url.pathname) || 0) !== generation)
                    await cache.delete(key);
            } catch {
                throw new Error("Video cache storage is unavailable");
            }
        }
        return bytes;
    })();
    pendingChunks.set(key, pending);
    try {
        return await pending;
    } finally {
        if (pendingChunks.get(key) === pending) pendingChunks.delete(key);
    }
}

function requestedRange(header, total) {
    if (!header) return { start: 0, end: total - 1, partial: false };
    const match = /^bytes=(\d*)-(\d*)$/.exec(header);
    if (!match || (!match[1] && !match[2])) throw new Error("Unsupported range");
    let start = match[1] ? Number(match[1]) : Math.max(0, total - Number(match[2]));
    let end = match[2] && match[1] ? Number(match[2]) : total - 1;
    if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end)) throw new Error("Invalid range");
    if (start >= total || end < start) return null;
    return { start, end: Math.min(end, total - 1), partial: true };
}

async function serveVideo(request, clientId) {
    const url = new URL(request.url);
    const cache = await caches.open(CACHE_NAME);
    const total = await videoLength(url, cache);
    const range = requestedRange(request.headers.get("Range"), total);
    if (!range) return new Response(null, {
        status: 416,
        headers: { "Content-Range": `bytes */${total}`, "Accept-Ranges": "bytes" }
    });

    let position = range.start;
    const body = new ReadableStream({
        async pull(controller) {
            if (position > range.end) {
                controller.close();
                return;
            }
            try {
                const chunkStart = Math.floor(position / CHUNK_BYTES) * CHUNK_BYTES;
                const chunk = await videoChunk(url, chunkStart, total, CHUNK_BYTES, cache, clientId, request.signal);
                const begin = position - chunkStart;
                const end = Math.min(chunk.byteLength, range.end - chunkStart + 1);
                controller.enqueue(chunk.subarray(begin, end));
                position = chunkStart + end;
            } catch (error) {
                controller.error(error);
            }
        }
    });
    const headers = {
        "Content-Type": "video/mp4",
        "Content-Length": String(range.end - range.start + 1),
        "Accept-Ranges": "bytes"
    };
    if (range.partial) headers["Content-Range"] = `bytes ${range.start}-${range.end}/${total}`;
    return new Response(body, { status: range.partial ? 206 : 200, headers });
}
