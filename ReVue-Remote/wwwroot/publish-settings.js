// Shared by the management page and its connection-format regression tests.
(() => {
    function srtConnection(hostname, rinkId, password) {
        if (!/^[A-Z0-9]{6}$/.test(rinkId)) throw new Error("Choose a valid Live Rink ID.");
        if (!password) throw new Error("Save a publish password to copy the SRT connection settings.");
        // Use the standard syntax accepted by hardware encoders. Encode the
        // credential separately from the enclosing URL; ReVue's SRT auth
        // callback decodes it once after checking for literal credentials.
        const streamId = `#!::m=publish,r=${rinkId},u=publisher,s=${encodeURIComponent(password)}`;
        if (streamId.length > 512)
            throw new Error("This password exceeds SRT's Stream ID limit. Use a shorter shared publish password for SRT.");
        const serverUrl = `srt://${hostname}:8890`;
        const query = new URLSearchParams({ mode: "caller", streamid: streamId, pkt_size: "1316" });
        return { serverUrl, streamId, url: `${serverUrl}?${query}` };
    }

    const settings = { srtConnection };
    if (typeof module !== "undefined" && module.exports) module.exports = settings;
    else globalThis.ReVuePublishSettings = settings;
})();
