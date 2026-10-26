// Real SRT/RTMP -> MediaMTX -> ReVue HLS/recording integration test.
// Requires a Debug build, Node 22+, FFmpeg/ffprobe, MediaMTX and srt-live-transmit.
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import dgram from 'node:dgram';
import { fileURLToPath } from 'node:url';
import { randomBytes, pbkdf2Sync } from 'node:crypto';
import assert from 'node:assert/strict';
import settings from '../wwwroot/publish-settings.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'revue-srt-ingest-'));
const dotnet = process.env.DOTNET_BINARY || 'dotnet';
const ffmpeg = process.env.FFMPEG_BINARY || 'ffmpeg';
const ffprobe = process.env.FFPROBE_BINARY || 'ffprobe';
const mediamtx = process.env.MEDIAMTX_BINARY || 'mediamtx';
const srt = process.env.SRT_BINARY || 'srt-live-transmit';
const chrome = process.env.CHROME_BINARY || (process.platform === 'darwin'
    ? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome' : 'chromium');
const remote = 'http://127.0.0.1:15018';
const mediaApi = 'http://127.0.0.1:19918';
const token = randomBytes(32).toString('hex');
const password = 'Shared:password,with%25&+#=été';
const adminPassword = 'Integration-admin-password';
const children = [];
const exec = promisify(execFile);
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
let cookie = '', heartbeat, management;

function hash(value) {
    const salt = randomBytes(16);
    return `pbkdf2-sha256$210000$${salt.toString('base64')}$${pbkdf2Sync(value, salt, 210000, 32, 'sha256').toString('base64')}`;
}
function launch(binary, args, name, options = {}) {
    const child = spawn(binary, args, {
        stdio: ['ignore', fs.openSync(`${tmp}/${name}.log`, 'w'), fs.openSync(`${tmp}/${name}.err`, 'w')],
        ...options
    });
    child.on('error', error => console.error(name, error.message));
    children.push(child);
    return child;
}
async function until(check, message, timeout = 25000) {
    const end = Date.now() + timeout;
    while (Date.now() < end) {
        try { const result = await check(); if (result) return result; } catch {}
        await sleep(150);
    }
    throw new Error(`${message}; logs: ${tmp}`);
}
async function api(url, method = 'GET', body) {
    const response = await fetch(remote + url, {
        method, headers: { 'Content-Type': 'application/json', Cookie: cookie },
        body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(15000)
    });
    assert(response.ok, `${method} ${url}: ${response.status} ${await response.clone().text()}`);
    return response;
}
async function ready(code) {
    const response = await fetch(`${mediaApi}/v3/paths/list`, { signal: AbortSignal.timeout(3000) });
    const { items } = await response.json();
    return items.find(item => item.name === code)?.ready === true;
}
function publisher(protocol, code, name, secret = password) {
    const args = ['-hide_banner', '-loglevel', 'error', '-re', '-f', 'lavfi', '-i',
        'testsrc2=size=320x180:rate=60000/1001', '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000',
        '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-preset', 'ultrafast', '-g', '60', '-bf', '0',
        '-sc_threshold', '0', '-c:a', 'aac'];
    if (protocol === 'rtmp') return [launch(ffmpeg, [...args, '-f', 'flv',
        `rtmp://127.0.0.1:11918/${code}?user=publisher&pass=${encodeURIComponent(secret)}`], name)];
    const connection = settings.srtConnection('127.0.0.1', code, secret);
    // The SRT CLI accepts a URL, so escape the enclosing Stream ID exactly as
    // the management page does. Hardware with a separate field uses streamId.
    const target = connection.url.replace(':8890?', ':18890?');
    const transport = launch(srt, ['-auto:no', '-timeout:15', '-c:1316', 'file://con', target], name + '-srt',
        { stdio: ['pipe', fs.openSync(`${tmp}/${name}-srt.log`, 'w'), fs.openSync(`${tmp}/${name}-srt.err`, 'w')] });
    transport.stdin.on('error', () => {});
    const encoder = launch(ffmpeg, [...args, '-f', 'mpegts', 'pipe:1'], name,
        { stdio: ['ignore', 'pipe', fs.openSync(`${tmp}/${name}.err`, 'w')] });
    encoder.stdout.pipe(transport.stdin);
    return [transport, encoder];
}
function stop(processes) {
    for (const child of processes) child.kill('SIGTERM');
}
async function codecs(url) {
    const { stdout } = await exec(ffprobe, ['-v', 'error', '-analyzeduration', '2000000',
        '-show_entries', 'stream=codec_name', '-of', 'json', url], { timeout: 30000 });
    const names = JSON.parse(stdout).streams.map(stream => stream.codec_name);
    assert(names.includes('h264') && names.includes('aac'), 'HLS must retain H.264 video and AAC audio');
}

async function managementUi() {
    const profile = tmp + '/chrome';
    launch(chrome, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
        '--no-first-run', 'about:blank'], 'chrome');
    const port = await until(() => fs.existsSync(profile + '/DevToolsActivePort') &&
        fs.readFileSync(profile + '/DevToolsActivePort', 'utf8').split('\n')[0], 'Chrome did not start');
    const target = await (await fetch(`http://127.0.0.1:${port}/json/new?${encodeURIComponent(remote + '/config')}`,
        { method: 'PUT' })).json();
    const ws = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise(resolve => ws.addEventListener('open', resolve, { once: true }));
    let id = 0;
    const pending = new Map();
    ws.addEventListener('message', event => {
        const message = JSON.parse(event.data);
        if (!message.id) return;
        const request = pending.get(message.id); pending.delete(message.id);
        message.error ? request.reject(message.error) : request.resolve(message.result);
    });
    const send = (method, params = {}) => new Promise((resolve, reject) => {
        pending.set(++id, { resolve, reject }); ws.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async expression => {
        const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
        if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
        return result.result.value;
    };
    let keepOpen = false;
    try {
        await until(() => evaluate(`!!document.getElementById('loginForm')`), 'Management page did not load');
        await evaluate(`document.getElementById('loginEmail').value='integration@example.test';
            document.getElementById('loginPassword').value=${JSON.stringify(adminPassword)};
            document.getElementById('loginForm').requestSubmit()`);
        await until(() => evaluate(`!!document.querySelector('[data-media-key="LIVE01"]')`), 'Management login failed');
        await evaluate(`document.querySelector('[data-view="keys"]').click();
            document.querySelector('[data-media-key="LIVE01"]').click()`);
        await until(() => evaluate(`document.getElementById('keyMediaDialog').open`), 'Stream settings did not open');
        assert(await evaluate(`!document.getElementById('copyKeySrtStreamId').disabled`));
        const connection = settings.srtConnection('127.0.0.1', 'LIVE01', password);
        assert(await evaluate(`document.getElementById('keySrtStreamId').textContent.endsWith('s=••••••••')`));
        await evaluate(`Object.defineProperty(navigator,'clipboard',{value:{writeText:async text=>{window.copiedSettings=text}}});
            document.getElementById('copyKeySrtStreamId').click()`);
        await until(() => evaluate('window.copiedSettings'), 'SRT Stream ID copy failed');
        assert.equal(await evaluate('window.copiedSettings'), connection.streamId);
        await evaluate(`window.copiedSettings='';document.getElementById('copyKeySrtUrl').click()`);
        await until(() => evaluate('window.copiedSettings'), 'SRT URL copy failed');
        assert.equal(await evaluate('window.copiedSettings'), connection.url);
        await evaluate(`document.getElementById('toggleKeyPublishPassword').click()`);
        assert.equal(await evaluate(`document.getElementById('keySrtStreamId').textContent`), connection.streamId);
        await evaluate(`document.getElementById('toggleKeyPublishPassword').click()`);
        await send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: false });
        assert(await evaluate(`document.getElementById('keyMediaDialog').scrollWidth <=
            document.getElementById('keyMediaDialog').clientWidth + 1`), 'Stream settings overflow on a narrow screen');
        const screenshot = await send('Page.captureScreenshot', { format: 'png' });
        fs.writeFileSync(tmp + '/stream-settings.png', Buffer.from(screenshot.data, 'base64'));
        await evaluate(`document.getElementById('cancelKeyMedia').click();
            document.querySelector('[data-media-key="LIVE03"]').click()`);
        await until(() => evaluate(`document.getElementById('keyMediaDialog').open &&
            document.getElementById('keyMediaForm').dataset.code==='LIVE03'`), 'Legacy stream settings did not open');
        assert(await evaluate(`document.getElementById('copyKeySrtStreamId').disabled &&
            document.getElementById('copyKeySrtUrl').disabled`), 'Legacy unrevealed passwords must not produce incomplete credentials');
        await evaluate(`document.getElementById('cancelKeyMedia').click()`);
        console.log('PASS: management UI copies SRT settings, masks/reveals the password, handles legacy keys and fits a narrow screen');
        keepOpen = true;
        return { evaluate, ws };
    } finally { if (!keepOpen) ws.close(); }
}

try {
    for (const port of [15018, 19918, 18818, 11918]) {
        const probe = http.createServer();
        await new Promise((resolve, reject) => probe.once('error', reject).listen(port, '127.0.0.1', resolve));
        await new Promise(resolve => probe.close(resolve));
    }
    const udp = dgram.createSocket('udp4');
    await new Promise((resolve, reject) => udp.once('error', reject).bind(18890, '127.0.0.1', resolve));
    await new Promise(resolve => udp.close(resolve));
    console.log('Test artifacts:', tmp);
    fs.mkdirSync(tmp + '/data/_system', { recursive: true });
    fs.writeFileSync(tmp + '/data/_system/users.json', JSON.stringify([{
        id: 'integration', email: 'integration@example.test', role: 'Admin', passwordHash: hash(adminPassword),
        mustChangePassword: false, createdAtUtc: new Date().toISOString()
    }]));
    fs.writeFileSync(tmp + '/data/_system/keys.json', JSON.stringify(['LIVE01', 'LIVE02', 'LIVE03', 'PAUS01', 'RECR01'].map(code => ({
        code, mode: code === 'RECR01' ? 'Recorded' : 'Live', createdByUserId: 'integration',
        liveFeedActive: code !== 'PAUS01', publishPasswordHash: hash(password), createdAtUtc: new Date().toISOString()
    }))));
    fs.writeFileSync(tmp + '/mediamtx.yml', `logLevel: debug
authMethod: http
authHTTPAddress: ${remote}/api/internal/mediamtx-auth?token=${token}
authHTTPExclude:
  - action: api
api: yes
apiAddress: 127.0.0.1:19918
rtmp: yes
rtmpAddress: 127.0.0.1:11918
srt: yes
srtAddress: 127.0.0.1:18890
rtsp: no
webrtc: no
hls: yes
hlsAddress: 127.0.0.1:18818
hlsVariant: fmp4
hlsSegmentDuration: 1s
hlsSegmentCount: 20
hlsAlwaysRemux: yes
paths:
  all_others:
    source: publisher
`);
    launch(dotnet, [root + '/ReVue-Remote/bin/Debug/net10.0/ReVue-Remote.dll'], 'remote', {
        cwd: root + '/ReVue-Remote', env: { ...process.env, ASPNETCORE_URLS: remote,
            ReVueRemote__StorageRoot: tmp + '/data', ReVueRemote__MediaAuthToken: token,
            ReVueRemote__MediaMtxHlsBase: 'http://127.0.0.1:18818', ReVueRemote__MediaMtxApiBase: mediaApi,
            ReVueRemote__FFmpegPath: ffmpeg }
    });
    await until(async () => (await fetch(remote + '/api/health')).ok, 'ReVue did not start');
    launch(mediamtx, [tmp + '/mediamtx.yml'], 'mediamtx');
    await until(async () => (await fetch(mediaApi + '/v3/paths/list')).ok, 'MediaMTX did not start');
    for (const protocol of ['rtmp', 'srt']) {
        for (const [code, secret, expected] of [['LIVE01', password, 200], ['LIVE01', 'incorrect', 401],
            ['PAUS01', password, 401], ['RECR01', password, 401], ['NONE01', password, 401]]) {
            const response = await fetch(`${remote}/api/internal/mediamtx-auth?token=${token}`, {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ action: 'publish', path: code, protocol, password: secret })
            });
            assert.equal(response.status, expected, `${protocol} authentication for ${code}`);
        }
    }
    for (const [action, protocol, secret, authToken] of [['read', 'srt', password, token],
        ['publish', 'srt', password, 'wrong-token'], ['publish', 'webrtc', password, token]]) {
        const response = await fetch(`${remote}/api/internal/mediamtx-auth?token=${authToken}`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ action, path: 'LIVE01', protocol, password: secret })
        });
        assert.equal(response.status, 401);
    }
    console.log('PASS: shared credentials, wrong passwords, paused/recorded/unknown rinks, callback token and protocol restrictions');
    const login = await api('/api/auth/login', 'POST', { email: 'integration@example.test', password: adminPassword });
    cookie = login.headers.getSetCookie().map(value => value.split(';')[0]).join('; ');
    management = await managementUi();

    const other = publisher('rtmp', 'LIVE02', 'other-rink');
    await until(() => ready('LIVE02'), 'Other rink RTMP stream did not start');
    const source = publisher('srt', 'LIVE01', 'hardware-caller', password);
    await until(() => ready('LIVE01'), 'SRT hardware Stream ID did not authenticate');
    await until(async () => (await fetch(`${remote}/api/sessions/LIVE01/live/preview/index.m3u8`)).ok,
        'SRT preview playlist did not reach ReVue');
    await codecs(`${remote}/api/sessions/LIVE01/live/preview/index.m3u8`);
    console.log('PASS: real authenticated SRT Caller with punctuation/Unicode password delivers H.264/AAC HLS through ReVue');
    await management.evaluate(`document.querySelector('[data-view="liveStreams"]').click()`);
    await until(() => management.evaluate(`!!document.querySelector('[data-live-preview="LIVE01"]:not(:disabled)')`),
        'SRT feed did not appear in Live Streams');
    await management.evaluate(`document.querySelector('[data-live-preview="LIVE01"]').click()`);
    await until(() => management.evaluate(`document.getElementById('livePreviewVideo').readyState >= 2 &&
        !document.getElementById('livePreviewVideo').paused`), 'Browser did not play the SRT preview');
    const frameTime = await management.evaluate(`document.getElementById('livePreviewVideo').currentTime`);
    await until(async () => await management.evaluate(`document.getElementById('livePreviewVideo').currentTime`) > frameTime + 1,
        'SRT preview froze in the browser');
    assert(await management.evaluate(`document.getElementById('livePreviewVideo').getVideoPlaybackQuality().totalVideoFrames > 0`),
        'Browser did not decode SRT preview frames');
    await management.evaluate(`document.getElementById('closeLivePreview').click()`);
    console.log('PASS: production Live Streams page plays advancing SRT video frames in Chrome');

    const eventId = randomBytes(16).toString('hex'), operatorInstanceId = randomBytes(16).toString('hex');
    let sequence = 0, mode = 'preparing';
    const publish = (overrides = {}) => api('/api/sessions/LIVE01/playback', 'POST', {
        sourceType: 'Live', videoId: eventId, mode, isPlaying: mode === 'recording',
        positionSeconds: 0, timelinePositionSeconds: 0, timelineDurationSeconds: 5,
        playbackRate: 1, liveDelaySeconds: 5, operatorInstanceId,
        operatorGeneration: 1, operatorSequence: ++sequence, ...overrides
    });
    await publish();
    heartbeat = setInterval(() => publish().catch(console.error), 2000);
    mode = 'recording'; await publish();
    const eventUrl = `${remote}/api/sessions/LIVE01/live/events/${eventId}/index.m3u8`;
    await until(async () => {
        const response = await fetch(eventUrl);
        return response.ok && (await response.text()).split('#EXTINF:').length >= 5;
    }, 'SRT event recording did not produce replay segments');
    await codecs(eventUrl);
    clearInterval(heartbeat); heartbeat = undefined;
    mode = 'replay'; await publish();
    const playlist = await (await fetch(eventUrl)).text();
    assert(playlist.includes('#EXT-X-ENDLIST'), 'Stop must finalize the SRT event recording');
    console.log('PASS: SRT Record/Stop produces playable H.264/AAC replay and a finalized event playlist');
    mode = 'ready';
    await publish({ videoId: '', timelineDurationSeconds: 0 });
    assert.equal((await fetch(eventUrl)).status, 404, 'Next Competitor must remove the previous SRT event');
    assert(await ready('LIVE01'), 'Next Competitor must preserve the continuous SRT feed');
    console.log('PASS: Next Competitor removes the SRT event and keeps the live feed online');

    await api('/api/manage/keys/LIVE01/live-feed', 'PUT', { active: false });
    await until(async () => !await ready('LIVE01'), 'Pause did not disconnect the SRT publisher');
    assert(await ready('LIVE02'), 'Pausing SRT must not interrupt another rink');
    stop(source);
    const rejected = publisher('srt', 'LIVE01', 'paused-caller', password);
    await until(() => rejected[0].exitCode !== null, 'Paused SRT publisher was not rejected', 18000);
    assert(!await ready('LIVE01'));
    stop(rejected);
    await api('/api/manage/keys/LIVE01/live-feed', 'PUT', { active: true });
    const rtmp = publisher('rtmp', 'LIVE01', 'same-password-rtmp');
    await until(() => ready('LIVE01'), 'Existing RTMP ingest no longer works with the shared password');
    await api('/api/manage/keys/LIVE01/live-feed', 'PUT', { active: false });
    await until(async () => !await ready('LIVE01'), 'Pause did not disconnect the RTMP publisher');
    assert(await ready('LIVE02'));
    stop(rtmp); stop(other);
    console.log('PASS: pause revokes SRT, rejects reconnection, preserves other rinks, and RTMP remains compatible');
    fs.writeFileSync(tmp + '/result.json', JSON.stringify({ passed: true, tested: ['authentication', 'SRT/HLS', 'record/replay', 'pause', 'RTMP'] }));
} finally {
    clearInterval(heartbeat);
    management?.ws.close();
    for (const child of children.reverse()) child.kill('SIGTERM');
}
