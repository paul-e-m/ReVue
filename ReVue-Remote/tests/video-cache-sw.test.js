const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const chunkBytes = 64 * 1024;
const video = new Uint8Array(1024 * 1024 + 37);
for (let index = 0; index < video.length; index++) video[index] = index % 251;
const origin = "https://revue.example";
const url = `${origin}/api/sessions/AA1001/videos/video-1/content?viewer=test-viewer`;

test("replay serves retained chunks, fetches gaps, and Next purges the video", async () => {
    const handlers = {};
    const entries = new Map();
    let serverBytes = 0;
    let rangeRequests = 0;
    let failWrites = false;
    const cache = {
        async match(key) { return entries.get(String(key))?.clone(); },
        async put(key, response) {
            if (failWrites) throw new Error("Quota exceeded");
            entries.set(String(key), response.clone());
        },
        async keys() { return [...entries.keys()].map(key => new Request(key)); },
        async delete(key) { return entries.delete(key.url || String(key)); }
    };
    const worker = {
        location: { origin },
        clients: { claim: async () => {} },
        skipWaiting: async () => {},
        addEventListener(type, handler) { handlers[type] = handler; }
    };
    const fetchOrigin = async (requestUrl, options = {}) => {
        if (String(requestUrl).endsWith("/videos")) {
            return Response.json([{ id: "video-1", sizeBytes: video.length }]);
        }
        const match = /^bytes=(\d+)-(\d+)$/.exec(options.headers?.Range || "");
        assert.ok(match, "server video request must have an explicit range");
        const start = Number(match[1]);
        const end = Math.min(Number(match[2]), video.length - 1);
        const bytes = video.slice(start, end + 1);
        serverBytes += bytes.length;
        rangeRequests++;
        return new Response(bytes, {
            status: 206,
            headers: { "Content-Range": `bytes ${start}-${end}/${video.length}` }
        });
    };
    const script = fs.readFileSync(path.join(__dirname, "../wwwroot/video-cache-sw.js"), "utf8");
    vm.runInNewContext(script, {
        self: worker,
        caches: { open: async () => cache },
        fetch: fetchOrigin,
        URL, Request, Response, ReadableStream, Uint8Array, Map, Promise
    });
    async function read(start, end) {
        let response;
        handlers.fetch({
            request: new Request(url, { headers: { Range: `bytes=${start}-${end}` } }),
            clientId: "client-1",
            respondWith(promise) { response = promise; }
        });
        const result = await response;
        assert.equal(result.status, 206);
        assert.equal(result.headers.get("Content-Range"), `bytes ${start}-${end}/${video.length}`);
        return new Uint8Array(await result.arrayBuffer());
    }
    function report(bufferedAhead, active = true, mode = "recording", recordedEnd = null) {
        handlers.message({
            source: { id: "client-1" },
            data: { type: "video-buffer", url, duration: 2, position: 0,
                bufferedAhead, active, mode, recordedEnd }
        });
    }

    report(0, true, "ready");
    const firstPlayback = read(0, 99);
    assert.deepEqual(await firstPlayback, video.slice(0, 100));
    assert.equal(serverBytes, chunkBytes, "selection preloads and retains the first range");
    assert.deepEqual(await read(100, 199), video.slice(100, 200));
    assert.equal(serverBytes, chunkBytes, "replay of an available chunk must not hit the server");
    report(0);
    assert.deepEqual(await read(chunkBytes, chunkBytes + 4), video.slice(chunkBytes, chunkBytes + 5));
    assert.equal(serverBytes, 2 * chunkBytes, "only the next missing chunk is fetched");
    assert.equal(rangeRequests, 2);
    assert.deepEqual(await read(chunkBytes - 4, chunkBytes + 4), video.slice(chunkBytes - 4, chunkBytes + 5));
    assert.equal(serverBytes, 2 * chunkBytes, "a cross-chunk replay range stays local");

    report(5, true, "ready");
    assert.deepEqual(await read(100, 199), video.slice(100, 200));
    assert.equal(serverBytes, 2 * chunkBytes, "cached replay still works with a full forward buffer");
    const gated = read(2 * chunkBytes, 2 * chunkBytes + 4);
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(serverBytes, 2 * chunkBytes, "five seconds buffered prevents another download");
    report(1);
    assert.deepEqual(await gated, video.slice(2 * chunkBytes, 2 * chunkBytes + 5));
    assert.equal(serverBytes, 3 * chunkBytes);

    report(9);
    const abandoned = read(3 * chunkBytes, 3 * chunkBytes + 4);
    await new Promise(resolve => setTimeout(resolve, 10));
    let purge;
    let reply;
    handlers.message({
        data: { type: "purge-video", url },
        ports: [{ postMessage(value) { reply = value; } }],
        waitUntil(promise) { purge = promise; }
    });
    await purge;
    await assert.rejects(abandoned, /Video selection cleared/);
    assert.equal(reply.ok, true);
    assert.equal(entries.size, 0);
    report(0);
    await read(0, 99);
    assert.equal(serverBytes, 4 * chunkBytes, "after Next the old bytes are fetched again");
    failWrites = true;
    await assert.rejects(read(chunkBytes, chunkBytes + 4), /Video cache storage is unavailable/);
});
