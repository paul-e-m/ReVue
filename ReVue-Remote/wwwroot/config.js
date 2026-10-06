(() => {
    const $ = id => document.getElementById(id);
    let me = null;
    let keys = [];
    let savedPublishPassword = "";
    let savedPublicRtspPassword = "";
    let savedPublicRtspHasPassword = false;
    const liveSamples = new Map();
    const expandedLiveStreams = new Set();
    let liveStatusLoading = false;
    let liveStatusReloadPending = false;
    let livePreviewHls = null;
    let currentVideos = [];
    let currentVideosCode = "";
    let draggedVideoId = "";
    let videoLoadSequence = 0;
    const uploads = new Map();
    const uploadQueue = [];
    const MAX_CONCURRENT_VIDEO_UPLOADS = 2;
    let activeVideoUploads = 0;
    const transcodes = new Map();
    const watchingJobs = new Set();
    let conversionPromptQueue = Promise.resolve();
    const MAX_VIDEO_SIZE_BYTES = 10 * 1024 ** 3;
    const SUPPORTED_VIDEO_EXTENSIONS = new Set(["mp4", "m4v", "mov", "mkv", "ts", "m2ts"]);

    async function api(path, options = {}) {
        const response = await fetch(path, {
            ...options,
            headers: {
                ...(options.body instanceof FormData ? {} : { "Content-Type": "application/json" }),
                ...(options.headers || {})
            }
        });
        const text = await response.text();
        let data = null;
        try { data = text ? JSON.parse(text) : null; } catch { data = text; }
        if (!response.ok) {
            const retry = data?.retryAfterSeconds ? ` Try again in ${data.retryAfterSeconds} seconds.` : "";
            throw new Error((data?.error || data?.detail || data || `Request failed (${response.status})`) + retry);
        }
        return data;
    }

    function msg(text, ok = false, target = "appMessage") {
        const element = $(target);
        element.textContent = text || "";
        element.classList.toggle("ok", ok);
    }

    async function copyText(text) {
        if (navigator.clipboard?.writeText) {
            await navigator.clipboard.writeText(text);
            return;
        }
        const input = document.createElement("textarea");
        input.value = text;
        input.style.position = "fixed";
        input.style.opacity = "0";
        document.body.append(input);
        input.select();
        const copied = document.execCommand("copy");
        input.remove();
        if (!copied) throw new Error("Clipboard unavailable");
    }

    function show(view) {
        const headings = {
            videos: ["Videos", "Upload and manage videos for your Rink IDs."],
            keys: ["Rink IDs", "Manage the folders available to your remote sessions."],
            liveStreams: ["Live Streams", "Monitor and control your Remote Live feeds."],
            profile: ["My account", "Update your details and password."],
            users: ["Users", "Manage access to ReVue Remote."]
        };
        for (const id of ["videos", "keys", "liveStreams", "profile", "users"])
            $(`${id}View`).classList.toggle("hidden", id !== view);
        document.querySelectorAll("nav [data-view]").forEach(button => {
            if (button.dataset.view === view) button.setAttribute("aria-current", "page");
            else button.removeAttribute("aria-current");
        });
        $("pageTitle").textContent = headings[view]?.[0] || "Videos";
        $("pageDescription").textContent = headings[view]?.[1] || "";
        if (view === "videos") void loadStorageStatus();
        if (view === "liveStreams") void loadLiveStatuses();
    }

    function esc(value) {
        const element = document.createElement("div");
        element.textContent = String(value ?? "");
        return element.innerHTML;
    }

    async function start() {
        try {
            me = await api("/api/auth/me");
            await enter();
        } catch {
            $("loginView").classList.remove("hidden");
        }
    }

    async function enter() {
        $("loginView").classList.add("hidden");
        $("appView").classList.remove("hidden");
        $("signedInAs").textContent = `${me.firstName || ""} ${me.lastName || ""} · ${me.email} · ${me.role}`.trim();
        $("usersTab").classList.toggle("hidden", me.role !== "Admin");
        $("keyListHeading").textContent = me.role === "Admin" ? "Rink IDs" : "My Rink IDs";
        $("profileEmail").value = me.email;
        $("profileFirst").value = me.firstName || "";
        $("profileLast").value = me.lastName || "";
        if (me.mustChangePassword) {
            $("forcePassword").showModal();
            return;
        }
        await loadKeys();
        if (me.role === "Admin") await loadUsers();
    }

    async function loadKeys(preferredCode = $("videoKey").value) {
        keys = await api("/api/manage/keys");
        const recordedKeys = keys.filter(key => key.mode !== "Live");
        $("videoKey").innerHTML = recordedKeys.map(key =>
            `<option value="${esc(key.code)}">${esc(key.code)}</option>`).join("");
        if (recordedKeys.some(key => key.code === preferredCode)) $("videoKey").value = preferredCode;
        $("keyList").innerHTML = keys.map(key => {
            const created = new Date(key.createdAtUtc).toLocaleString();
            const detail = me.role === "Admin"
                ? `Created by ${esc(key.createdByEmail)}, ${created}`
                : `Created ${created}`;
            const media = key.mode === "Live" ? "Live · RTMP publisher" : "Recorded";
            const streamSettings = key.mode === "Live"
                ? `<button class="small" data-media-key="${esc(key.code)}">Stream settings</button>` +
                  `<button class="small secondary" data-encoder-settings="${esc(key.code)}">Encoder settings</button>` : "";
            return `<div class="row"><div><b>${esc(key.code)}</b><div class="muted">${esc(media)} · ${detail}</div></div>` +
            `<div class="rowActions">${streamSettings}<button class="small" data-rename-key="${esc(key.code)}">Rename</button>` +
            `<button class="danger small" data-delete-key="${esc(key.code)}">Delete Rink ID</button></div></div>`
        }).join("") || `<p>${me.role === "Admin" ? "No Rink IDs have been created." : "You have not created any Rink IDs."}</p>`;
        const hasLive = keys.some(key => key.mode === "Live");
        for (const code of liveSamples.keys())
            if (!keys.some(key => key.mode === "Live" && key.code === code)) liveSamples.delete(code);
        if (hasLive && !$("liveStreamsView").classList.contains("hidden")) void loadLiveStatuses();
        await loadVideos();
    }

    function liveStatusLabel(stream, available, now) {
        if (stream.active === false) {
            liveSamples.delete(stream.code);
            return { text: "Paused", detail: "Live stream ignored", style: "paused", rate: null };
        }
        if (!available || !stream.ready) {
            liveSamples.delete(stream.code);
            return { text: "Ready", detail: "Ready, waiting for feed", style: "ready", rate: null };
        }
        const bytes = Number(stream.totalBytes);
        const prior = liveSamples.get(stream.code);
        const started = stream.startedAtUtc || "";
        const unchanged = prior && prior.started === started && bytes === prior.bytes;
        const lastChange = unchanged ? prior.lastChange : now;
        const rate = prior && prior.started === started && bytes >= prior.bytes && now > prior.at
            ? 8 * (bytes - prior.bytes) / ((now - prior.at) / 1000) / 1_000_000 : null;
        liveSamples.set(stream.code, { bytes, started, at: now, lastChange });
        if (!(bytes > 0) || now - lastChange >= 8000)
            return { text: "Ready", detail: "Ready, waiting for feed", style: "ready", rate };
        return { text: "Active", detail: "Receiving data", style: "active", rate };
    }

    const remoteRoleAbbreviations = {
        "technical-controller": "TC",
        "technical-specialist-1": "TS1",
        "technical-specialist-2": "TS2",
        judging: "J",
        referee: "Ref",
        "data-specialist": "ET",
        announcer: "Ann",
        "data-input-operator": "DIO",
        "video-replay-operator": "VRO",
        unknown: "Other"
    };

    function remoteClientSummary(stream) {
        const total = Number(stream.remoteClientCount) || 0;
        const roles = (stream.roles || [])
            .filter(role => Number(role.count) > 0)
            .map(role => `${Number(role.count)} ${remoteRoleAbbreviations[role.role] || "Other"}`);
        return roles.length ? `${total} · ${roles.join(" · ")}` : String(total);
    }

    function rtspViewerSummary(stream) {
        const viewers = stream.rtspViewers || [];
        return viewers.length
            ? viewers.map(viewer => `${esc(viewer.user || "Unknown")} · ${esc(viewer.ipAddress || "Unknown IP")}`).join("<br>")
            : "None";
    }

    async function loadLiveStatuses(forceAfterCurrent = false) {
        if (liveStatusLoading) {
            if (forceAfterCurrent) liveStatusReloadPending = true;
            return;
        }
        if ($("liveStreamsView").classList.contains("hidden")) return;
        liveStatusLoading = true;
        try {
            const snapshot = await api("/api/manage/live-streams");
            const now = Date.now();
            const streams = snapshot.streams || [];
            const streamCodes = new Set(streams.map(stream => stream.code));
            for (const code of expandedLiveStreams)
                if (!streamCodes.has(code)) expandedLiveStreams.delete(code);
            const statusCounts = { active: 0, ready: 0, paused: 0 };
            $("liveStatusList").innerHTML = streams.map(stream => {
                const state = liveStatusLabel(stream, snapshot.available, now);
                statusCounts[state.style]++;
                const started = stream.startedAtUtc
                    ? new Date(stream.startedAtUtc).toLocaleString() : "—";
                const resolution = stream.width && stream.height
                    ? `${stream.width} × ${stream.height}` : "—";
                const codecs = stream.codecs?.length ? stream.codecs.join(" · ") : "—";
                const rate = stream.active === false || !stream.ready || !snapshot.available ? "—"
                    : state.rate == null ? "Measuring…" : `${state.rate.toFixed(2)} Mbps`;
                const received = stream.totalBytes == null ? "—" : formatStorageSize(stream.totalBytes);
                const feedActive = stream.active !== false;
                const publicRtspEnabled = stream.publicRtspEnabled === true;
                const expanded = expandedLiveStreams.has(stream.code);
                const activity = state.style === "active"
                    ? `<p class="liveReceivingIndicator"><span class="liveReceivingLight" aria-hidden="true"></span>Receiving data</p>`
                    : `<p class="liveStreamDescription">${esc(state.detail)}</p>`;
                const startedSummary = stream.startedAtUtc
                    ? `<p class="liveStreamStarted"><span>Started</span><time datetime="${esc(stream.startedAtUtc)}">${esc(started)}</time></p>`
                    : "";
                return `<article class="liveStreamCard"><div class="liveStreamCardHeader"><div class="liveStreamIdentity"><span>Rink ID</span><strong>${esc(stream.code)}</strong></div>` +
                    `<span class="liveStatusBadge ${state.style}">${esc(state.text)}</span></div>` +
                    activity + startedSummary +
                    `<button type="button" class="small secondary livePreviewButton" data-live-preview="${esc(stream.code)}" ${feedActive && stream.ready && snapshot.available ? "" : "disabled"}>Preview</button>` +
                    `<details class="liveStreamDisclosure" data-live-details="${esc(stream.code)}" ${expanded ? "open" : ""}><summary><span class="showDetailsLabel">Show details</span><span class="hideDetailsLabel">Hide details</span></summary>` +
                    `<div class="liveStreamDetails"><dl class="liveStreamFacts"><dt>VRO</dt><dd class="${stream.vroConnected ? "connectionUp" : "connectionDown"}">${stream.vroConnected ? "Connected" : "Disconnected"}</dd>` +
                    `<dt>Clients</dt><dd>${esc(remoteClientSummary(stream))}</dd>` +
                    `<dt>RTSP viewers</dt><dd>${rtspViewerSummary(stream)}</dd>` +
                    `<dt>Resolution</dt><dd>${esc(resolution)}</dd><dt>Codecs</dt><dd>${esc(codecs)}</dd>` +
                    `<dt>Receive rate</dt><dd>${esc(rate)}</dd><dt>Total received</dt><dd>${esc(received)}</dd></dl>` +
                    `<div class="liveStreamActions"><button type="button" class="small secondary liveFeedToggle" data-live-code="${esc(stream.code)}" data-live-active="${feedActive}">${feedActive ? "Pause feed" : "Resume feed"}</button>` +
                    `<div class="publicRtspCardControl"><label class="publicRtspToggle"><input type="checkbox" data-public-rtsp-toggle="${esc(stream.code)}" data-public-rtsp-enabled="${publicRtspEnabled}" ${publicRtspEnabled ? "checked" : ""}>RTSP Relay</label>` +
                    `<button type="button" class="small secondary" data-public-rtsp-settings="${esc(stream.code)}">Settings</button></div></div></div></details></article>`;
            }).join("") || '<p class="emptyState">No Live Rink IDs have been created.</p>';
            $("liveStatusSummary").textContent = streams.length === 0
                ? "Create a Live Rink ID to begin monitoring a stream."
                : snapshot.available
                ? `${statusCounts.active} Active · ${statusCounts.ready} Ready · ${statusCounts.paused} Paused`
                : "Stream monitoring is unavailable. Check the MediaMTX control API.";
            $("liveStatusUpdated").textContent = `Updated ${new Date(now).toLocaleTimeString()}`;
        } catch {
            $("liveStatusSummary").textContent = "Stream status could not be loaded.";
            $("liveStatusUpdated").textContent = "";
        } finally {
            liveStatusLoading = false;
            if (liveStatusReloadPending) {
                liveStatusReloadPending = false;
                void loadLiveStatuses();
            }
        }
    }

    function stopLivePreview() {
        livePreviewHls?.destroy();
        livePreviewHls = null;
        const player = $("livePreviewVideo");
        player.pause();
        player.removeAttribute("src");
        player.load();
    }

    function openLivePreview(code) {
        stopLivePreview();
        $("livePreviewTitle").textContent = `${code} live preview`;
        $("livePreviewMessage").textContent = "The preview follows the live feed with a short delay. It starts muted; use the player controls for sound.";
        $("livePreviewDialog").showModal();
        const player = $("livePreviewVideo");
        player.muted = true;
        const url = `/api/sessions/${encodeURIComponent(code)}/live/preview/index.m3u8`;
        if (player.canPlayType("application/vnd.apple.mpegurl")) {
            player.src = url;
            void player.play().catch(() => {});
        } else if (window.Hls?.isSupported()) {
            livePreviewHls = new window.Hls({ liveSyncDurationCount: 3 });
            livePreviewHls.loadSource(url);
            livePreviewHls.attachMedia(player);
            livePreviewHls.on(window.Hls.Events.MANIFEST_PARSED, () => {
                void player.play().catch(() => {
                    $("livePreviewMessage").textContent = "Press Play to start the preview.";
                });
            });
            livePreviewHls.on(window.Hls.Events.ERROR, (_, detail) => {
                if (detail.fatal)
                    $("livePreviewMessage").textContent = "The live preview could not load. The publisher may have disconnected.";
            });
        } else {
            $("livePreviewMessage").textContent = "This browser does not support the live preview format.";
        }
    }

    async function openPublicRtspSettings(code, requestedEnabled = null) {
        try {
            const settings = await api(`/api/manage/keys/${encodeURIComponent(code)}/media`);
            const canReveal = settings.publicRtspPasswordCanBeRevealed !== false;
            savedPublicRtspPassword = canReveal && typeof settings.publicRtspPassword === "string"
                ? settings.publicRtspPassword : "";
            savedPublicRtspHasPassword = settings.hasPublicRtspPassword === true;
            const enabled = requestedEnabled ?? settings.publicRtspEnabled === true;
            const passwordInput = $("publicRtspPassword");
            $("publicRtspForm").dataset.code = code;
            $("publicRtspTitle").textContent = `${code} RTSP Relay`;
            $("publicRtspEnabled").checked = enabled;
            $("publicRtspUrl").textContent = `rtsp://${location.hostname}/${code}`;
            passwordInput.value = savedPublicRtspPassword;
            passwordInput.placeholder = savedPublicRtspHasPassword && !savedPublicRtspPassword
                ? "••••••••" : "Required when enabled";
            passwordInput.required = enabled && !savedPublicRtspHasPassword;
            setPublicRtspPasswordVisibility(false);
            $("copyPublicRtspUrl").disabled = !settings.publicRtspEnabled || !savedPublicRtspPassword;
            $("publicRtspCopyFeedback").textContent = "";
            msg("", false, "publicRtspMessage");
            $("publicRtspDialog").showModal();
        } catch (error) {
            msg(error.message);
        }
    }

    function setKeyPasswordVisibility(visible) {
        setPasswordVisibility("keyPublishPassword", "toggleKeyPublishPassword", visible);
    }

    function setPublicRtspPasswordVisibility(visible) {
        setPasswordVisibility("publicRtspPassword", "togglePublicRtspPassword", visible);
    }

    function setPasswordVisibility(inputId, buttonId, visible) {
        const input = $(inputId);
        const button = $(buttonId);
        input.type = visible ? "text" : "password";
        button.setAttribute("aria-label", visible ? "Hide password" : "Show password");
        button.title = visible ? "Hide password" : "Show password";
        button.querySelector(".passwordVisibleIcon").classList.toggle("hidden", visible);
        button.querySelector(".passwordHiddenIcon").classList.toggle("hidden", !visible);
    }

    function prepareKeyPasswordEditor(key, settings = {}) {
        const input = $("keyPublishPassword");
        const hasPassword = settings.hasPublishPassword ?? key.hasPublishPassword;
        const canReveal = settings.passwordCanBeRevealed !== false;
        savedPublishPassword = canReveal && typeof settings.publishPassword === "string"
            ? settings.publishPassword : "";
        input.value = savedPublishPassword;
        input.placeholder = hasPassword && !input.value ? "••••••••" : "Required";
        setKeyPasswordVisibility(false);
        $("keyMediaForm").dataset.passwordCanBeRevealed = String(canReveal);
        $("toggleKeyPublishPassword").disabled = hasPassword && !canReveal && !input.value;
        $("copyKeyRtmpUrl").disabled = !savedPublishPassword;
    }

    function renderVideos() {
        const selectedCode = $("videoKey").value;
        $("deleteAllVideos").disabled = currentVideos.length === 0 ||
            !selectedCode || selectedCode !== currentVideosCode;
        $("videoList").innerHTML = currentVideos.map((video, index) => {
            const height = Number(video.height);
            const fps = Number(video.framesPerSecond);
            const bitrate = Number(video.videoBitrateBitsPerSecond);
            const scan = ["tt", "bb", "tb", "bt"].includes(String(video.fieldOrder || "").toLowerCase()) ? "i" : "p";
            const resolution = height === 720 || height === 1080 ? `${height}${scan}` : "Unavailable";
            const frameRate = fps > 0 ? `${fps.toFixed(2).replace(/\.?0+$/, "")} fps` : "Unavailable";
            const videoBitrate = bitrate > 0 ? `${(bitrate / 1_000_000).toFixed(2)} Mbps` : "Unavailable";
            const gop = video.gop;
            const minGopFrames = Number(gop?.minFrames);
            const maxGopFrames = Number(gop?.maxFrames);
            const hasGopFrames = minGopFrames > 0 && maxGopFrames >= minGopFrames;
            const gopCaption = !hasGopFrames ? "GOP unavailable"
                : minGopFrames === maxGopFrames ? `GOP ${minGopFrames}`
                    : `GOP min ${minGopFrames} · max ${maxGopFrames}`;
            const gopDetails = !hasGopFrames ? "GOP frame interval unavailable."
                : `Shortest measured keyframe gap: ${minGopFrames} frames. Longest: ${maxGopFrames} frames.`;
            const transcode = transcodes.get(video.id);
            const transcodeStatus = transcode
                ? `<div class="transcodeStatus"><progress max="100" value="${Number(transcode.percent) || 0}"></progress><span>${esc(transcode.label)}</span></div>`
                : "";
            return `<div class="row videoRow" draggable="true" data-video-id="${esc(video.id)}">` +
                `<span class="dragHandle" title="Drag to reorder" aria-hidden="true">⋮⋮</span>` +
                `<button type="button" class="videoPreview" data-preview-video="${esc(video.id)}" aria-label="Preview video ${index + 1}" title="Preview video" ${transcode ? "disabled" : ""}>` +
                `<img src="/api/sessions/${encodeURIComponent(selectedCode)}/videos/${encodeURIComponent(video.id)}/thumbnail?at=28&amp;v=${encodeURIComponent(video.uploadedAtUtc || "")}" alt="" loading="lazy">` +
                `<span class="videoNumber" aria-hidden="true">${index + 1}</span><span class="videoPlayIcon" aria-hidden="true"><svg viewBox="0 0 40 40"><circle cx="20" cy="20" r="18"/><path d="m16 12 12 8-12 8z"/></svg></span></button>` +
                `<div class="videoInfo"><div class="videoName"><b>${esc(video.fileName)}</b></div><div class="muted">${(video.sizeBytes / 1048576).toFixed(1)} MB · ${new Date(video.uploadedAtUtc).toLocaleString()}</div>` +
                `<div class="videoMetadata"><button type="button" class="metaButton" data-convert-resolution="${esc(video.id)}" title="${height === 1080 ? "Convert to 720p" : "Already 720p"}" ${height === 1080 && !transcode ? "" : "disabled"}>${resolution}</button>` +
                `<button type="button" class="metaButton" data-convert-fps="${esc(video.id)}" title="${fps > 30 ? "Convert to 29.97 fps" : "Frame rate is 30 fps or lower"}" ${fps > 30 && !transcode ? "" : "disabled"}>${frameRate}</button>` +
                `<button type="button" class="metaButton" disabled title="${esc(gopDetails)}">${gopCaption}</button>` +
                `<span>Bitrate: ${videoBitrate}</span></div>${transcodeStatus}</div>` +
                `<div class="rowActions"><button class="small" data-rename-video="${esc(video.id)}" ${transcode ? "disabled" : ""}>Rename</button>` +
                `<button class="danger small" data-delete-video="${esc(video.id)}" ${transcode ? "disabled" : ""}>Delete</button></div></div>`;
        }).join("") || "<p class=\"emptyState\">No videos uploaded for this Rink ID.</p>";
    }

    async function loadVideos() {
        void loadStorageStatus();
        const sequence = ++videoLoadSequence;
        const code = $("videoKey").value;
        if (code !== currentVideosCode) {
            currentVideos = [];
            currentVideosCode = code;
            renderVideos();
            if (code) $("videoList").innerHTML = "<p class=\"emptyState\">Loading videos…</p>";
        }
        if (!code) {
            currentVideos = [];
            currentVideosCode = "";
            renderVideos();
            $("videoList").innerHTML = "<p>Create or select a Recorded Rink ID before uploading videos.</p>";
            return;
        }
        const [videos, activeJobs] = await Promise.all([
            api(`/api/sessions/${encodeURIComponent(code)}/videos`),
            api(`/api/sessions/${encodeURIComponent(code)}/transcodes`)
        ]);
        if (sequence !== videoLoadSequence || code !== $("videoKey").value) return;
        currentVideos = videos;
        currentVideosCode = code;
        for (const job of activeJobs) {
            transcodes.set(job.sourceVideoId, {
                jobId: job.uploadId,
                percent: Number(job.transcodePercent) || 0,
                label: job.transcodePercent == null ? "Queued for transcoding…" : `Transcoding ${Number(job.transcodePercent).toFixed(1)}%`
            });
            const video = videos.find(item => item.id === job.sourceVideoId);
            if (video) void watchVideoConversion(code, video, job.uploadId);
        }
        renderVideos();
    }

    async function loadUsers() {
        const users = await api("/api/admin/users");
        $("userList").innerHTML = users.map(user =>
            `<div class="row"><div><b>${esc(user.email)}</b><div class="muted">${esc(user.firstName)} ${esc(user.lastName)} · ${user.role}${user.isLocked ? " · LOCKED" : ""}${user.mustChangePassword ? " · password change required" : ""}</div></div>` +
            `<div class="rowActions"><select data-role="${user.id}"><option ${user.role === "Regular" ? "selected" : ""}>Regular</option><option ${user.role === "Admin" ? "selected" : ""}>Admin</option></select>` +
            `<input data-reset="${user.id}" type="password" minlength="8" placeholder="New temporary password"><button class="small" data-save-user="${user.id}">Save / unlock</button>` +
            `<button class="danger small" data-delete-user="${user.id}" ${user.id === me.id ? "disabled" : ""}>Delete</button></div></div>`
        ).join("");
    }

    function formatBytesPerSecond(bytesPerSecond) {
        if (!Number.isFinite(bytesPerSecond) || bytesPerSecond <= 0) return "calculating…";
        if (bytesPerSecond >= 1048576) return `${(bytesPerSecond / 1048576).toFixed(1)} MB/s`;
        return `${Math.max(1, Math.round(bytesPerSecond / 1024))} KB/s`;
    }

    function formatRemaining(seconds) {
        if (!Number.isFinite(seconds) || seconds < 0) return "";
        if (seconds < 60) return `${Math.ceil(seconds)}s remaining`;
        if (seconds < 3600) return `${Math.ceil(seconds / 60)}m remaining`;
        const hours = Math.floor(seconds / 3600);
        const minutes = Math.ceil((seconds % 3600) / 60);
        return `${hours}h ${minutes}m remaining`;
    }

    function formatStorageSize(bytes) {
        if (bytes >= 1_000_000_000_000) return `${(bytes / 1_000_000_000_000).toFixed(1)} TB`;
        if (bytes >= 1_000_000_000) return `${(bytes / 1_000_000_000).toFixed(1)} GB`;
        return `${(bytes / 1_000_000).toFixed(1)} MB`;
    }

    async function loadStorageStatus() {
        try {
            const status = await api("/api/manage/storage");
            const total = Number(status?.totalBytes);
            const available = Number(status?.availableBytes);
            const revueUsed = Number(status?.revueUsedBytes);
            const capacity = Number(status?.maximumStorageBytes);
            const remaining = Number(status?.availableWithinLimitBytes);
            if (!Number.isFinite(total) || total <= 0 ||
                !Number.isFinite(available) || available < 0 || available > total ||
                !Number.isFinite(revueUsed) || revueUsed < 0 ||
                !Number.isFinite(capacity) || capacity <= 0 || capacity > total ||
                !Number.isFinite(remaining) || remaining < 0 || remaining > capacity)
                throw new Error("Storage capacity unavailable.");
            const usedPercent = revueUsed / capacity * 100;
            $("storageUsed").value = Math.min(100, usedPercent);
            $("storageUsed").setAttribute("aria-valuetext", `${usedPercent.toFixed(1)}% of the 90% storage limit used`);
            $("storagePercent").textContent = `${usedPercent.toFixed(1)}% used`;
            $("storageDetails").textContent = `${formatStorageSize(remaining)} available of ${formatStorageSize(capacity)}`;
            $("storageStatus").classList.toggle("low", remaining / capacity < 0.2);
        } catch {
            $("storageUsed").removeAttribute("value");
            $("storageUsed").removeAttribute("aria-valuetext");
            $("storagePercent").textContent = "Unavailable";
            $("storageDetails").textContent = "Server storage could not be measured.";
            $("storageStatus").classList.remove("low");
        }
    }

    function delay(milliseconds) {
        return new Promise(resolve => window.setTimeout(resolve, milliseconds));
    }

    function confirmVideoConversion(video, mode) {
        const choice = conversionPromptQueue.then(() => {
            return new Promise(resolve => {
                const dialog = $("conversionDialog");
                const resolution = mode === "resolution";
                $("conversionDialogTitle").textContent = resolution
                    ? "Convert to lower resolution?" : "Reduce frame rate?";
                $("conversionDialogDescription").textContent = resolution
                    ? `${video.fileName} is ${video.width} × ${video.height}. Converting it to 1280 × 720 reduces the data viewers need to download and may improve playback on slower internet connections.`
                    : `${video.fileName} is ${Number(video.framesPerSecond).toFixed(2)} fps. Converting it to 29.97 fps reduces the data viewers need to download.`;
                $("confirmConversion").textContent = resolution ? "Convert to 720p" : "Convert to 29.97 fps";
                const finish = confirmed => {
                    dialog.close();
                    resolve(confirmed);
                };
                $("cancelConversion").onclick = () => finish(false);
                $("confirmConversion").onclick = () => finish(true);
                dialog.showModal();
            });
        });
        conversionPromptQueue = choice.then(() => {}, () => {});
        return choice;
    }

    function parseUploadError(xhr, fallback) {
        try {
            const data = JSON.parse(xhr.responseText || "{}");
            return data.error || data.detail || fallback;
        } catch { return xhr.responseText || fallback; }
    }

    function sendUploadChunk(job, blob, offset, onProgress) {
        return new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            let settled = false;
            const finish = callback => {
                if (settled) return;
                settled = true;
                job.xhrs.delete(xhr);
                callback();
            };
            job.xhrs.add(xhr);
            xhr.open("PUT", `/api/sessions/${encodeURIComponent(job.code)}/uploads/${encodeURIComponent(job.uploadId)}?offset=${offset}`);
            xhr.setRequestHeader("Content-Type", "application/octet-stream");
            xhr.setRequestHeader("X-Upload-Chunk-Length", String(blob.size));
            xhr.timeout = 10 * 60 * 1000;
            xhr.upload.onprogress = event => {
                if (event.lengthComputable) onProgress(event.loaded);
            };
            xhr.onload = () => {
                if (xhr.status >= 200 && xhr.status < 300) {
                    try {
                        const result = JSON.parse(xhr.responseText);
                        finish(() => resolve(result));
                    }
                    catch { finish(() => reject(new Error("The server returned an invalid upload response."))); }
                    return;
                }
                finish(() => reject(new Error(parseUploadError(xhr, `Chunk upload failed (${xhr.status}).`))));
            };
            xhr.onerror = () => finish(() => reject(new Error("The network connection was interrupted.")));
            xhr.ontimeout = () => finish(() => reject(new Error("The upload stopped responding.")));
            xhr.onabort = () => finish(() => reject(new DOMException("Upload cancelled.", "AbortError")));
            xhr.send(blob);
        });
    }

    function pumpUploadQueue() {
        while (activeVideoUploads < MAX_CONCURRENT_VIDEO_UPLOADS && uploadQueue.length > 0) {
            const job = uploadQueue.shift();
            if (job.cancelled || !uploads.has(job.id)) continue;
            activeVideoUploads++;
            job.slotActive = true;
            job.start();
        }
    }

    async function createUpload(file, code) {
        const extension = file.name.includes(".") ? file.name.split(".").pop().toLowerCase() : "";
        if (!SUPPORTED_VIDEO_EXTENSIONS.has(extension)) {
            msg(`${file.name} is not a supported video type. Choose MP4, M4V, MOV, MKV, TS, or M2TS.`);
            return;
        }
        if (file.size > MAX_VIDEO_SIZE_BYTES) {
            msg(`${file.name} exceeds the 10 GiB video size limit.`);
            return;
        }
        const fingerprint = `${code}:${file.name}:${file.size}:${file.lastModified}`;
        const existing = [...uploads.values()].find(upload => upload.fingerprint === fingerprint && !upload.cancelled);
        if (existing) {
            if (!existing.paused) {
                msg(`${file.name} is already being uploaded.`);
                return;
            }
            uploads.delete(existing.id);
            existing.row.remove();
        }

        const id = crypto.randomUUID();
        const row = document.createElement("div");
        row.className = "progressRow";
        row.innerHTML = `<div class="uploadName">${esc(file.name)}</div><progress max="100" value="0"></progress><span class="uploadStatus">Queued for upload…</span><button class="danger small">Cancel</button>`;
        $("uploadProgressList").appendChild(row);

        const progress = row.querySelector("progress");
        const status = row.querySelector("span");
        const job = {
            id, fingerprint, file, code, row, progress, status,
            uploadId: "", xhrs: new Set(), cancelled: false, paused: false,
            slotActive: false, samples: []
        };
        uploads.set(id, job);

        function releaseUploadSlot() {
            if (!job.slotActive) return;
            job.slotActive = false;
            activeVideoUploads--;
            pumpUploadQueue();
        }

        function updateProgress(bytes, label = "") {
            const now = performance.now();
            job.samples.push({ now, bytes });
            job.samples = job.samples.filter(sample => now - sample.now <= 15000);
            const first = job.samples[0];
            const elapsed = first && now > first.now ? (now - first.now) / 1000 : 0;
            const speed = elapsed > 0 ? Math.max(0, bytes - first.bytes) / elapsed : 0;
            const percent = file.size > 0 ? Math.min(100, bytes / file.size * 100) : 0;
            progress.value = percent;
            const estimate = speed > 0 ? formatRemaining((file.size - bytes) / speed) : "";
            status.textContent = label || `${percent.toFixed(1)}% · ${formatBytesPerSecond(speed)}${estimate ? ` · ${estimate}` : ""}`;
        }

        async function cancel() {
            if (job.cancelled) return;
            job.cancelled = true;
            const queuedIndex = uploadQueue.indexOf(job);
            if (queuedIndex >= 0) {
                uploadQueue.splice(queuedIndex, 1);
                job.start();
            }
            for (const xhr of [...job.xhrs]) xhr.abort();
            try {
                if (job.uploadId) {
                    await api(`/api/sessions/${encodeURIComponent(code)}/uploads/${encodeURIComponent(job.uploadId)}`, { method: "DELETE" });
                }
            } catch { }
            uploads.delete(id);
            row.remove();
            msg(`${file.name} upload cancelled.`);
        }
        job.cancel = cancel;
        row.querySelector("button").onclick = cancel;

        await new Promise(resolve => {
            job.start = resolve;
            uploadQueue.push(job);
            pumpUploadQueue();
        });
        if (job.cancelled) {
            releaseUploadSlot();
            return;
        }

        try {
            status.textContent = "Preparing…";
            const started = await api(`/api/sessions/${encodeURIComponent(code)}/uploads`, {
                method: "POST",
                body: JSON.stringify({
                    fileName: file.name,
                    sizeBytes: file.size,
                    lastModifiedUnixMs: file.lastModified
                })
            });
            job.uploadId = started.uploadId;
            if (job.cancelled) {
                try {
                    await api(`/api/sessions/${encodeURIComponent(code)}/uploads/${encodeURIComponent(job.uploadId)}`, { method: "DELETE" });
                } catch { }
                return;
            }
            const chunkSize = Number(started.chunkSizeBytes) || 8 * 1024 * 1024;
            const chunkCount = Math.ceil(file.size / chunkSize);
            const completed = new Set((started.completedChunkIndexes || []).map(Number));
            const activeBytes = new Map();
            let nextChunkIndex = 0;

            const chunkLength = index => Math.min(chunkSize, file.size - index * chunkSize);
            const completedByteCount = () => [...completed]
                .reduce((total, index) => total + chunkLength(index), 0);
            const aggregateBytes = () => completedByteCount() + [...activeBytes.entries()]
                .reduce((total, [index, bytes]) => total + (completed.has(index) ? 0 : bytes), 0);
            const mergeServerStatus = serverStatus => {
                for (const index of serverStatus.completedChunkIndexes || []) completed.add(Number(index));
            };
            const takeNextChunk = () => {
                while (nextChunkIndex < chunkCount && completed.has(nextChunkIndex)) nextChunkIndex++;
                return nextChunkIndex < chunkCount ? nextChunkIndex++ : null;
            };

            const resumedBytes = completedByteCount();
            updateProgress(
                resumedBytes,
                resumedBytes > 0 ? `Resuming at ${(resumedBytes / file.size * 100).toFixed(1)}% with 3 connections…` : "Starting 3 connections…");

            async function uploadWorker() {
                while (!job.cancelled && !job.paused) {
                    const chunkIndex = takeNextChunk();
                    if (chunkIndex === null) return;
                    const offset = chunkIndex * chunkSize;
                    const end = Math.min(file.size, offset + chunkSize);
                    let uploaded = completed.has(chunkIndex);
                    let lastError = null;
                    activeBytes.set(chunkIndex, 0);

                    for (let attempt = 0; attempt < 6 && !uploaded; attempt++) {
                        if (job.cancelled || job.paused) return;
                        if (attempt > 0) {
                            const waitSeconds = Math.min(15, 2 ** (attempt - 1));
                            updateProgress(aggregateBytes(), `Connection interrupted—retrying chunk ${chunkIndex + 1} in ${waitSeconds}s…`);
                            await delay(waitSeconds * 1000);
                            if (job.cancelled || job.paused) return;
                            try {
                                const serverStatus = await api(`/api/sessions/${encodeURIComponent(code)}/uploads/${encodeURIComponent(job.uploadId)}`);
                                mergeServerStatus(serverStatus);
                                if (completed.has(chunkIndex)) { uploaded = true; break; }
                            } catch { }
                        }
                        try {
                            const result = await sendUploadChunk(
                                job,
                                file.slice(offset, end),
                                offset,
                                loaded => {
                                    activeBytes.set(chunkIndex, loaded);
                                    updateProgress(aggregateBytes());
                                });
                            mergeServerStatus(result);
                            completed.add(chunkIndex);
                            uploaded = true;
                        } catch (error) {
                            if (job.cancelled || job.paused || error.name === "AbortError") return;
                            lastError = error;
                        }
                    }
                    activeBytes.delete(chunkIndex);
                    updateProgress(aggregateBytes());
                    if (!uploaded) throw lastError || new Error(`Chunk ${chunkIndex + 1} could not continue after repeated retries.`);
                }
            }

            const remainingChunks = chunkCount - completed.size;
            const workers = Array.from(
                { length: Math.min(3, Math.max(0, remainingChunks)) },
                () => uploadWorker());
            await Promise.all(workers);
            if (completed.size !== chunkCount)
                throw new Error("Not every video chunk reached the server.");

            updateProgress(file.size, "Preparing video…");
            let finalized = false;
            let finalizeError = null;
            for (let attempt = 0; attempt < 4 && !finalized; attempt++) {
                if (attempt > 0) await delay(Math.min(8000, 1000 * 2 ** (attempt - 1)));
                try {
                    await api(`/api/sessions/${encodeURIComponent(code)}/uploads/${encodeURIComponent(job.uploadId)}/complete`, { method: "POST" });
                    finalized = true;
                } catch (error) { finalizeError = error; }
            }
            if (!finalized) throw finalizeError || new Error("The server could not finalize the upload.");

            releaseUploadSlot();

            const cancelButton = row.querySelector("button");
            cancelButton.disabled = true;
            updateProgress(file.size, "Inspecting video…");
            let processingStatus;
            while (!job.cancelled) {
                try {
                    processingStatus = await api(`/api/sessions/${encodeURIComponent(code)}/uploads/${encodeURIComponent(job.uploadId)}`);
                } catch {
                    await delay(2000);
                    continue;
                }
                const stage = String(processingStatus?.stage || "").toLowerCase();
                if (stage === "completed") break;
                if (stage === "failed")
                    throw new Error(processingStatus.errorMessage || "The server could not process this video.");
                progress.value = 100;
                status.textContent = "Processing video…";
                await delay(1000);
            }
            if (job.cancelled) return;
            uploads.delete(id);
            progress.value = 100;
            status.textContent = "Complete";
            row.classList.add("uploadComplete");
            let refreshError = "";
            if ($("videoKey").value === code) {
                try { await loadVideos(); }
                catch (error) { refreshError = ` The video library could not refresh: ${error.message}`; }
            }
            msg(`${file.name} upload complete.${refreshError}`, !refreshError);
            window.setTimeout(() => row.remove(), 3500);
        } catch (error) {
            if (job.cancelled) return;
            job.paused = true;
            for (const xhr of [...job.xhrs]) xhr.abort();
            row.classList.add("uploadFailed");
            status.textContent = `Failed: ${error.message}`;
            msg(`${file.name} upload failed: ${error.message}`);
            if (error.message.includes("storage capacity")) void loadStorageStatus();
        } finally { releaseUploadSlot(); }
    }

    async function watchVideoConversion(code, video, jobId) {
        if (watchingJobs.has(jobId)) return;
        watchingJobs.add(jobId);
        try {
            while (transcodes.get(video.id)?.jobId === jobId) {
                let status;
                try {
                    status = await api(`/api/sessions/${encodeURIComponent(code)}/uploads/${encodeURIComponent(jobId)}`);
                } catch {
                    await delay(2000);
                    continue;
                }
                if (status.stage === "completed") {
                    transcodes.delete(video.id);
                    if ($("videoKey").value === code) await loadVideos();
                    msg(`${video.fileName} conversion complete.`, true);
                    return;
                }
                if (status.stage === "failed")
                    throw new Error(status.errorMessage || "Video conversion failed.");
                const percent = status.transcodePercent;
                transcodes.set(video.id, {
                    jobId,
                    percent: percent == null ? 0 : Math.min(100, Math.max(0, Number(percent) || 0)),
                    label: percent == null ? "Queued for transcoding…" : `Transcoding ${Number(percent).toFixed(1)}%`
                });
                if ($("videoKey").value === code) renderVideos();
                await delay(1000);
            }
        } catch (error) {
            transcodes.delete(video.id);
            if ($("videoKey").value === code) renderVideos();
            msg(`${video.fileName} conversion failed: ${error.message}`);
        } finally {
            watchingJobs.delete(jobId);
        }
    }

    async function startVideoConversion(video, mode) {
        const code = $("videoKey").value;
        if (transcodes.has(video.id) || !await confirmVideoConversion(video, mode) ||
            code !== $("videoKey").value) return;
        try {
            const job = await api(
                `/api/sessions/${encodeURIComponent(code)}/videos/${encodeURIComponent(video.id)}/transcode`, {
                    method: "POST", body: JSON.stringify({ mode })
                });
            transcodes.set(video.id, { jobId: job.uploadId, percent: 0, label: "Queued for transcoding…" });
            if ($("videoKey").value === code) renderVideos();
            void watchVideoConversion(code, video, job.uploadId);
        } catch (error) {
            msg(`${video.fileName} conversion could not start: ${error.message}`);
        }
    }

    async function cancelUploadsFor(code) {
        await Promise.all([...uploads.values()]
            .filter(upload => upload.code === code)
            .map(upload => upload.cancel()));
    }

    $("loginForm").onsubmit = async event => {
        event.preventDefault();
        try {
            me = await api("/api/auth/login", { method: "POST", body: JSON.stringify({ email: $("loginEmail").value, password: $("loginPassword").value }) });
            msg("", false, "loginMessage");
            await enter();
        } catch (error) { msg(error.message, false, "loginMessage"); }
    };

    $("logoutBtn").onclick = async () => { await api("/api/auth/logout", { method: "POST" }); location.reload(); };
    document.querySelectorAll("[data-view]").forEach(button => button.onclick = () => show(button.dataset.view));

    $("newKeyMode").onchange = () => {
        const live = $("newKeyMode").value === "Live";
        $("newKeyPasswordRow").classList.toggle("hidden", !live);
        $("newKeyPassword").required = live;
        if (!live) $("newKeyPassword").value = "";
    };
    $("keyForm").onsubmit = async event => {
        event.preventDefault();
        try {
            const mode = $("newKeyMode").value;
            await api("/api/manage/keys", {
                method: "POST",
                body: JSON.stringify({
                    code: $("newKey").value.toUpperCase(),
                    mode,
                    publishPassword: mode === "Live" ? $("newKeyPassword").value : ""
                })
            });
            $("keyForm").reset();
            $("newKeyPassword").required = false;
            $("newKeyPasswordRow").classList.add("hidden");
            await loadKeys();
            msg("Rink ID created.", true);
        } catch (error) { msg(error.message); }
    };

    $("keyList").onclick = async event => {
        const encoderCode = event.target.closest("[data-encoder-settings]")?.dataset.encoderSettings;
        if (encoderCode) {
            $("encoderSettingsRinkId").textContent = encoderCode;
            $("encoderSettingsDialog").showModal();
            return;
        }
        const mediaCode = event.target.dataset.mediaKey;
        if (mediaCode) {
            const key = keys.find(item => item.code === mediaCode);
            if (!key || key.mode !== "Live") return;
            try {
                const settings = await api(`/api/manage/keys/${encodeURIComponent(mediaCode)}/media`);
                $("keyMediaForm").dataset.code = mediaCode;
                $("keyMediaTitle").textContent = `${mediaCode} live stream`;
                $("keyRtmpUrl").textContent = `rtmp://${location.hostname}/${mediaCode}`;
                prepareKeyPasswordEditor(key, settings);
                $("keyCopyFeedback").textContent = "";
                msg("", false, "keyMediaMessage");
                $("keyMediaDialog").showModal();
            } catch (error) { msg(error.message); }
            return;
        }
        const renameCode = event.target.dataset.renameKey;
        if (renameCode) {
            $("renameKeyForm").dataset.oldCode = renameCode;
            $("renameKeyCode").value = renameCode;
            msg("", false, "renameKeyMessage");
            $("renameKeyDialog").showModal();
            $("renameKeyCode").focus();
            $("renameKeyCode").select();
            return;
        }
        const code = event.target.dataset.deleteKey;
        if (!code || !confirm(`Delete Rink ID ${code}? All videos stored under this Rink ID will be permanently deleted.`)) return;
        try {
            await cancelUploadsFor(code);
            await api(`/api/manage/keys/${encodeURIComponent(code)}`, { method: "DELETE" });
            transcodes.clear();
            await loadKeys();
            msg(`Rink ID ${code} and all of its videos were deleted.`, true);
        } catch (error) { msg(error.message); }
    };

    $("liveStatusList").addEventListener("toggle", event => {
        const details = event.target.closest("[data-live-details]");
        if (!details) return;
        if (details.open) expandedLiveStreams.add(details.dataset.liveDetails);
        else expandedLiveStreams.delete(details.dataset.liveDetails);
    }, true);

    $("liveStatusList").onclick = async event => {
        const publicRtspToggle = event.target.closest("[data-public-rtsp-toggle]");
        if (publicRtspToggle) {
            const code = publicRtspToggle.dataset.publicRtspToggle;
            const requestedEnabled = publicRtspToggle.checked;
            publicRtspToggle.checked = publicRtspToggle.dataset.publicRtspEnabled === "true";
            if (code) await openPublicRtspSettings(code, requestedEnabled);
            return;
        }
        const publicRtspSettings = event.target.closest("[data-public-rtsp-settings]");
        if (publicRtspSettings) {
            await openPublicRtspSettings(publicRtspSettings.dataset.publicRtspSettings);
            return;
        }
        const toggle = event.target.closest("[data-live-active]");
        if (toggle) {
            const code = toggle.dataset.liveCode;
            if (!code) return;
            const active = toggle.dataset.liveActive !== "true";
            toggle.disabled = true;
            try {
                await api(`/api/manage/keys/${encodeURIComponent(code)}/live-feed`, {
                    method: "PUT", body: JSON.stringify({ active })
                });
                const key = keys.find(item => item.code === code);
                if (key) key.liveFeedActive = active;
                await loadLiveStatuses(true);
                msg(`${code} live feed ${active ? "activated" : "paused"}.`, true);
            } catch (error) {
                msg(error.message);
                toggle.disabled = false;
            }
            return;
        }
        const code = event.target.closest("[data-live-preview]")?.dataset.livePreview;
        if (code && keys.some(key => key.mode === "Live" && key.code === code))
            openLivePreview(code);
    };
    $("closeLivePreview").onclick = () => $("livePreviewDialog").close();
    $("livePreviewDialog").addEventListener("close", stopLivePreview);
    $("closePublicRtsp").onclick = () => $("publicRtspDialog").close();
    $("togglePublicRtspPassword").onclick = () =>
        setPublicRtspPasswordVisibility($("publicRtspPassword").type === "password");
    $("publicRtspEnabled").onchange = () => {
        $("publicRtspPassword").required = $("publicRtspEnabled").checked && !savedPublicRtspHasPassword;
    };
    $("copyPublicRtspUrl").onclick = async () => {
        if (!savedPublicRtspPassword) return;
        const code = $("publicRtspForm").dataset.code;
        const url = `rtsp://viewer:${encodeURIComponent(savedPublicRtspPassword)}@${location.hostname}/${encodeURIComponent(code)}`;
        try {
            await copyText(url);
            $("publicRtspCopyFeedback").textContent = "Copied to clipboard (including the password).";
        } catch {
            $("publicRtspCopyFeedback").textContent = "Could not copy the URL. Select it and copy it manually.";
        }
    };
    $("publicRtspForm").onsubmit = async event => {
        event.preventDefault();
        const code = $("publicRtspForm").dataset.code;
        const enabled = $("publicRtspEnabled").checked;
        const password = $("publicRtspPassword").value;
        if (enabled && !password && !savedPublicRtspHasPassword) {
            $("publicRtspPassword").reportValidity();
            return;
        }
        try {
            await api(`/api/manage/keys/${encodeURIComponent(code)}/media`, {
                method: "PUT",
                body: JSON.stringify({
                    publicRtspEnabled: enabled,
                    publicRtspPassword: password && password !== savedPublicRtspPassword ? password : null
                })
            });
            if (password) savedPublicRtspPassword = password;
            savedPublicRtspHasPassword = savedPublicRtspHasPassword || Boolean(password);
            $("publicRtspPassword").required = enabled && !savedPublicRtspHasPassword;
            $("copyPublicRtspUrl").disabled = !enabled || !savedPublicRtspPassword;
            $("publicRtspCopyFeedback").textContent = "";
            await loadLiveStatuses(true);
            msg(`RTSP Relay ${enabled ? "enabled" : "disabled"}.`, true, "publicRtspMessage");
        } catch (error) {
            msg(error.message, false, "publicRtspMessage");
        }
    };
    $("closeEncoderSettings").onclick = () => $("encoderSettingsDialog").close();
    $("copyEncoderSettings").onclick = async () => {
        const settings = [
            "ReVue Remote encoder settings",
            "Protocol: RTMP",
            "Resolution: 1280 x 720 progressive",
            "Frame rate: 59.94 fps constant (60000/1001)",
            "Video: H.264/AVC, 8-bit 4:2:0 (yuv420p)",
            "Rate control: CBR",
            "Video bitrate: 6000 kbps recommended",
            "Keyframe interval: 1 second (GOP 60)",
            "B-frames: 0",
            "Audio: AAC-LC, 48 kHz, stereo, 128 kbps"
        ].join("\n");
        try {
            await copyText(settings);
            $("copyEncoderSettingsLabel").textContent = "Copied";
            window.setTimeout(() => $("copyEncoderSettingsLabel").textContent = "Copy settings", 1600);
        } catch {
            msg("The encoder settings could not be copied. Select and copy them manually.");
        }
    };

    $("cancelRenameKey").onclick = () => $("renameKeyDialog").close();
    $("cancelKeyMedia").onclick = () => $("keyMediaDialog").close();
    $("toggleKeyPublishPassword").onclick = () =>
        setKeyPasswordVisibility($("keyPublishPassword").type === "password");
    $("keyPublishPassword").oninput = () => {
        const legacyPassword = $("keyMediaForm").dataset.passwordCanBeRevealed === "false";
        $("toggleKeyPublishPassword").disabled = legacyPassword && !$("keyPublishPassword").value;
    };
    $("copyKeyRtmpUrl").onclick = async () => {
        if (!savedPublishPassword) return;
        const url = `${$("keyRtmpUrl").textContent}?pass=${encodeURIComponent(savedPublishPassword)}`;
        try {
            await copyText(url);
            $("keyCopyFeedback").textContent = "Copied to clipboard (including the password).";
        } catch {
            $("keyCopyFeedback").textContent = "Could not copy the URL. Select it and copy it manually.";
        }
    };
    $("keyMediaForm").onsubmit = async event => {
        event.preventDefault();
        const code = $("keyMediaForm").dataset.code;
        const enteredPassword = $("keyPublishPassword").value;
        const body = { publishPassword: enteredPassword };
        try {
            await api(`/api/manage/keys/${encodeURIComponent(code)}/media`, {
                method: "PUT", body: JSON.stringify(body)
            });
            $("keyMediaDialog").close();
            await loadKeys();
            msg(`${code} stream settings saved.`, true);
        } catch (error) { msg(error.message, false, "keyMediaMessage"); }
    };
    $("renameKeyForm").onsubmit = async event => {
        event.preventDefault();
        const oldCode = $("renameKeyForm").dataset.oldCode;
        const newCode = $("renameKeyCode").value.trim().toUpperCase();
        try {
            await api(`/api/manage/keys/${encodeURIComponent(oldCode)}`, {
                method: "PUT", body: JSON.stringify({ code: newCode })
            });
            $("renameKeyDialog").close();
            await loadKeys($("videoKey").value === oldCode ? newCode : $("videoKey").value);
            msg(`Rink ID ${oldCode} was renamed to ${newCode}.`, true);
        } catch (error) { msg(error.message, false, "renameKeyMessage"); }
    };

    $("videoKey").onchange = loadVideos;
    $("videoFiles").onchange = () => {
        const count = $("videoFiles").files.length;
        $("selectedFilesLabel").textContent = count === 0 ? "No files selected"
            : count === 1 ? $("videoFiles").files[0].name : `${count} videos selected`;
    };
    $("uploadBtn").onclick = () => {
        const files = [...$("videoFiles").files];
        const code = $("videoKey").value;
        if (!code || files.length === 0) return msg("Choose a Rink ID and at least one MP4 video.");
        for (const file of files) void createUpload(file, code);
        $("videoFiles").value = "";
        $("selectedFilesLabel").textContent = "No files selected";
    };

    $("deleteAllVideos").onclick = async () => {
        const code = $("videoKey").value;
        if (!code || code !== currentVideosCode || currentVideos.length === 0 ||
            !confirm(`Delete all videos stored under Rink ID ${code}? This cannot be undone.`)) return;
        try {
            await cancelUploadsFor(code);
            await api(`/api/sessions/${encodeURIComponent(code)}/videos`, { method: "DELETE" });
            transcodes.clear();
            await loadVideos();
            msg(`All videos under Rink ID ${code} were deleted.`, true);
        } catch (error) { msg(error.message); }
    };

    $("videoList").onclick = async event => {
        const previewId = event.target.closest("[data-preview-video]")?.dataset.previewVideo;
        if (previewId) {
            const video = currentVideos.find(item => item.id === previewId);
            const code = $("videoKey").value;
            if (!video || !code) return;
            $("previewTitle").textContent = video.fileName;
            $("previewDialog").showModal();
            const player = $("previewVideo");
            player.src = `/api/sessions/${encodeURIComponent(code)}/videos/${encodeURIComponent(video.id)}/preview`;
            player.load();
            return;
        }
        const resolutionId = event.target.dataset.convertResolution;
        const fpsId = event.target.dataset.convertFps;
        if (resolutionId || fpsId) {
            const video = currentVideos.find(item => item.id === (resolutionId || fpsId));
            if (video) void startVideoConversion(video, resolutionId ? "resolution" : "frame_rate");
            return;
        }
        const renameId = event.target.dataset.renameVideo;
        if (renameId) {
            const video = currentVideos.find(item => item.id === renameId);
            if (!video) return;
            $("renameVideoForm").dataset.videoId = renameId;
            $("renameVideoName").value = video.fileName;
            msg("", false, "renameVideoMessage");
            $("renameVideoDialog").showModal();
            $("renameVideoName").focus();
            $("renameVideoName").select();
            return;
        }
        const id = event.target.dataset.deleteVideo;
        if (!id || !confirm("Delete this video from the cloud?")) return;
        try {
            await api(`/api/sessions/${encodeURIComponent($("videoKey").value)}/videos/${encodeURIComponent(id)}`, { method: "DELETE" });
            await loadVideos();
        } catch (error) { msg(error.message); }
    };

    $("cancelRenameVideo").onclick = () => $("renameVideoDialog").close();
    $("renameVideoForm").onsubmit = async event => {
        event.preventDefault();
        const videoId = event.target.dataset.videoId;
        const code = $("videoKey").value;
        if (!videoId || !code) return;
        try {
            await api(`/api/sessions/${encodeURIComponent(code)}/videos/${encodeURIComponent(videoId)}/name`, {
                method: "PUT",
                body: JSON.stringify({ fileName: $("renameVideoName").value })
            });
            $("renameVideoDialog").close();
            await loadVideos();
            msg("Video renamed.", true);
        } catch (error) { msg(error.message, false, "renameVideoMessage"); }
    };

    $("videoList").ondragstart = event => {
        if (event.target.closest("button")) return event.preventDefault();
        const row = event.target.closest("[data-video-id]");
        if (!row) return;
        draggedVideoId = row.dataset.videoId;
        row.classList.add("dragging");
        event.dataTransfer.effectAllowed = "move";
        event.dataTransfer.setData("text/plain", draggedVideoId);
    };
    $("videoList").ondragover = event => {
        const row = event.target.closest("[data-video-id]");
        if (!row || row.dataset.videoId === draggedVideoId) return;
        event.preventDefault();
        $("videoList").querySelectorAll(".dragOver").forEach(item => item.classList.remove("dragOver"));
        row.classList.add("dragOver");
    };
    $("videoList").ondragend = () => {
        draggedVideoId = "";
        $("videoList").querySelectorAll(".dragging,.dragOver").forEach(item => item.classList.remove("dragging", "dragOver"));
    };
    $("videoList").ondrop = async event => {
        event.preventDefault();
        const target = event.target.closest("[data-video-id]");
        const sourceId = draggedVideoId || event.dataTransfer.getData("text/plain");
        if (!target || !sourceId || target.dataset.videoId === sourceId) return;
        const from = currentVideos.findIndex(video => video.id === sourceId);
        const to = currentVideos.findIndex(video => video.id === target.dataset.videoId);
        if (from < 0 || to < 0) return;
        const reordered = [...currentVideos];
        const [moved] = reordered.splice(from, 1);
        reordered.splice(to, 0, moved);
        currentVideos = reordered;
        renderVideos();
        try {
            currentVideos = await api(`/api/sessions/${encodeURIComponent($("videoKey").value)}/videos/order`, {
                method: "PUT",
                body: JSON.stringify({ videoIds: currentVideos.map(video => video.id) })
            });
            renderVideos();
            msg("Video order saved.", true);
        } catch (error) {
            msg(error.message);
            await loadVideos();
        }
    };

    $("profileForm").onsubmit = async event => {
        event.preventDefault();
        try {
            me = await api("/api/account/profile", { method: "PUT", body: JSON.stringify({ email: $("profileEmail").value, firstName: $("profileFirst").value, lastName: $("profileLast").value }) });
            await enter();
            msg("Profile saved.", true);
        } catch (error) { msg(error.message); }
    };

    async function changePassword(current, newPassword, confirmation, target) {
        if (newPassword !== confirmation) throw new Error("The new passwords do not match.");
        await api("/api/account/password", { method: "PUT", body: JSON.stringify({ currentPassword: current, newPassword }) });
        me.mustChangePassword = false;
        if ($("forcePassword").open) {
            $("forcePassword").close();
            await loadKeys();
            if (me.role === "Admin") await loadUsers();
        }
        msg("Password changed.", true, target);
    }

    $("passwordForm").onsubmit = async event => {
        event.preventDefault();
        try {
            await changePassword($("currentPassword").value, $("newPassword").value, $("confirmPassword").value, "appMessage");
            event.target.reset();
        } catch (error) { msg(error.message); }
    };
    $("forcePasswordForm").onsubmit = async event => {
        event.preventDefault();
        try { await changePassword($("forceCurrent").value, $("forceNew").value, $("forceConfirm").value, "forceMessage"); }
        catch (error) { msg(error.message, false, "forceMessage"); }
    };

    $("createUserForm").onsubmit = async event => {
        event.preventDefault();
        try {
            await api("/api/admin/users", { method: "POST", body: JSON.stringify({ email: $("userEmail").value, firstName: $("userFirst").value, lastName: $("userLast").value, role: $("userRole").value, password: $("userPassword").value }) });
            event.target.reset();
            await loadUsers();
            msg("User created; they must change their password at first login.", true);
        } catch (error) { msg(error.message); }
    };

    $("userList").onclick = async event => {
        const save = event.target.dataset.saveUser;
        const remove = event.target.dataset.deleteUser;
        try {
            if (save) {
                const role = document.querySelector(`[data-role="${save}"]`).value;
                const newPassword = document.querySelector(`[data-reset="${save}"]`).value;
                await api(`/api/admin/users/${save}`, { method: "PUT", body: JSON.stringify({ role, newPassword, unlock: true }) });
                await loadUsers();
            }
            if (remove && confirm("Delete this user?")) {
                await api(`/api/admin/users/${remove}`, { method: "DELETE" });
                await loadUsers();
            }
        } catch (error) { msg(error.message); }
    };

    $("forcePassword").addEventListener("cancel", event => event.preventDefault());
    $("conversionDialog").addEventListener("cancel", event => event.preventDefault());
    $("closePreview").onclick = () => $("previewDialog").close();
    $("previewDialog").addEventListener("close", () => {
        const player = $("previewVideo");
        player.pause();
        player.removeAttribute("src");
        player.load();
    });
    window.setInterval(() => {
        if (!document.hidden && !$("appView").classList.contains("hidden") &&
            !$("videosView").classList.contains("hidden"))
            void loadStorageStatus();
    }, 30000);
    window.setInterval(() => {
        if (!document.hidden && !$("liveStreamsView").classList.contains("hidden"))
            void loadLiveStatuses();
    }, 3000);
    start();
})();
