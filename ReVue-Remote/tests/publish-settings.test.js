const test = require("node:test");
const assert = require("node:assert/strict");
const { srtConnection } = require("../wwwroot/publish-settings.js");

test("SRT URLs preserve the complete hardware Stream ID and shared password", () => {
    for (const password of ["rink-password", "commas,colons:equals=&hash# plus+percent%", "literal%2C%25", "été,日本"]) {
        const settings = srtConnection("revue.example", "LIVE01", password);
        const url = new URL(settings.url);
        assert.equal(url.port, "8890");
        assert.equal(url.searchParams.get("mode"), "caller");
        assert.equal(url.searchParams.get("pkt_size"), "1316");
        assert.equal(url.searchParams.get("streamid"), settings.streamId);
        assert.equal(url.hash, "", "the standard syntax marker must not become a URL fragment");
        const fields = Object.fromEntries(settings.streamId.slice(4).split(",").map(field => {
            const at = field.indexOf("=");
            return [field.slice(0, at), field.slice(at + 1)];
        }));
        assert.equal(fields.m, "publish");
        assert.equal(fields.r, "LIVE01");
        assert.equal(fields.u, "publisher");
        assert.equal(decodeURIComponent(fields.s), password);
        assert.equal(url.searchParams.has("passphrase"), false);
    }
});

test("SRT accepts IPv6 hosts and reports unsupported credentials clearly", () => {
    assert.equal(new URL(srtConnection("[::1]", "LIVE01", "password").url).hostname, "[::1]");
    assert.throws(() => srtConnection("revue.example", "LIVE01", ""), /Save a publish password/);
    assert.throws(() => srtConnection("revue.example", "LIVE01", ",".repeat(256)), /Stream ID limit/);
    assert.throws(() => srtConnection("revue.example", "../bad", "password"), /valid Live Rink ID/);
});
