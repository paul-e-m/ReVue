(() => {
    const sessionCodeInput = document.getElementById("sessionCode");
    const panelTypeInput = document.getElementById("panelType");
    const panelTypeButton = document.getElementById("panelTypeButton");
    const panelTypeLabel = document.getElementById("panelTypeLabel");
    const panelTypeMenu = document.getElementById("panelTypeMenu");
    const languageSelect = document.getElementById("language");
    const languageButton = document.getElementById("languageButton");
    const languageValueLabel = document.getElementById("languageValueLabel");
    const languageMenu = document.getElementById("languageMenu");
    const volumeInput = document.getElementById("volume");
    const muteInput = document.getElementById("mute");
    const controls = document.getElementById("controls");
    const video = document.getElementById("video");
    const emptyStateMessage = document.getElementById("emptyStateMessage");
    const playbackStatus = document.getElementById("playbackStatus");
    const networkIndicator = document.getElementById("networkIndicator");
    const networkLatency = document.getElementById("networkLatency");
    const emptyState = document.getElementById("emptyState");
    const timelineCanvas = document.getElementById("timelineCanvas");
    const timelineControlRow = document.getElementById("timelineControlRow");
    const videoFileName = document.getElementById("videoFileName");
    const judgingTransport = document.getElementById("judgingTransport");
    const judgingPlayPause = document.getElementById("judgingPlayPause");
    const refereeStopwatch = document.getElementById("refereeStopwatch");
    const refereeTimer = document.getElementById("refereeTimer");
    const replayProgramTimeIndicator = document.getElementById("replayProgramTimeIndicator");
    const reviewIndicator = document.getElementById("reviewIndicator");
    const reviewIndicatorText = document.getElementById("reviewIndicatorText");

    const translations = {
        en: {
            language: "Language", waitingForRinkId: "Waiting for a Rink ID.", volume: "Volume", mute: "Mute",
            videoVolume: "Video volume", rinkId: "Rink ID", rinkIdInput: "Six-character Rink ID", role: "Role",
            viewerRole: "Viewer role", technicalPanel: "Technical", judge: "Judge", referee: "Referee",
            remotePlayer: "Passive remote replay player", waitingForVideo: "Stand by for video...", play: "Play", pause: "Pause",
            playVideo: "Play video", pauseVideo: "Pause video", setStopwatchZero: "Set stopwatch zero",
            clearStopwatch: "Clear stopwatch", videoTimeline: "Video timeline", videoPlaybackPosition: "Video playback position",
            checking: "CHECKING…", disconnected: "DISCONNECTED", checkingServer: "Checking server connectivity", serverDisconnected: "Server disconnected",
            serverLatency: "Server latency {message}", live: "LIVE", review: "Review:", normalSpeed: "normal speed",
            speed: "{speed}x speed", playbackPaused: "Playback paused{zoom}", playingAt: "Playing at {speed}{zoom}",
            zoom: " · Zoom {percent}%", autoplayMuted: "{message} · Muted by browser; uncheck Mute for sound",
            secondsCount: "{count} second{suffix}", blockedEmpty: "This IP address has been blocked for 24 hours.",
            invalidRinkEmpty: "This Rink ID is not valid.", finalAttempt: "Final attempt: another invalid Rink ID will block this IP address for 24 hours.",
            enterRink: "Enter a six-character Rink ID.", ipBlocked: "This IP address is blocked. Access resumes in {time}.",
            finalAttemptAvailable: "Final attempt available in {time}. Another invalid Rink ID will block this IP address for 24 hours.",
            invalidRinkDelay: "Invalid Rink ID. Try again in {time}.", connectedWaitingVideo: "Connected to {code}. Waiting for a video.",
            preparingVideo: "Connected to {code}. Preparing selected video.", reverseUnavailable: "Reverse playback is not shown on the passive player.",
            waitForward: "Connected to {code}. Waiting for forward playback.", adjustVolume: "Connected to {code}. Adjust Volume to enable playback with sound.",
            connectingEmpty: "Connecting to ReVue VRO…", connecting: "Connecting to {code}…", rinkNotCreated: "Rink ID {code} has not been created.",
            serverNotReached: "The ReVue-Remote server could not be reached.", playbackUnreadable: "The playback update could not be read.",
            connectedWaitingVro: "Connected to {code}. Waiting for ReVue VRO.", connectionInterrupted: "Connection to {code} was interrupted. Reconnecting…",
            soundEnabled: "Connected to {code}. Sound enabled; waiting for ReVue VRO."
        },
        fr: {
            language: "Langue", waitingForRinkId: "En attente d’un ID de patinoire.", volume: "Volume", mute: "Muet",
            videoVolume: "Volume de la vidéo", rinkId: "ID de patinoire", rinkIdInput: "ID de patinoire à six caractères", role: "Rôle",
            viewerRole: "Rôle du spectateur", technicalPanel: "Technique", judge: "Juge", referee: "Arbitre",
            remotePlayer: "Lecteur de reprise à distance", waitingForVideo: "En attente de la vidéo…", play: "Lire", pause: "Pause",
            playVideo: "Lire la vidéo", pauseVideo: "Mettre la vidéo en pause", setStopwatchZero: "Remettre le chronomètre à zéro",
            clearStopwatch: "Effacer le chronomètre", videoTimeline: "Chronologie de la vidéo", videoPlaybackPosition: "Position de lecture de la vidéo",
            checking: "VÉRIFICATION…", disconnected: "DÉCONNECTÉ", checkingServer: "Vérification de la connexion au serveur", serverDisconnected: "Serveur déconnecté",
            serverLatency: "Latence du serveur : {message}", live: "DIRECT", review: "Révision :", normalSpeed: "vitesse normale",
            speed: "vitesse {speed}x", playbackPaused: "Lecture en pause{zoom}", playingAt: "Lecture à {speed}{zoom}",
            zoom: " · Zoom {percent}%", autoplayMuted: "{message} · Son coupé par le navigateur; décochez Muet pour activer le son",
            secondsCount: "{count} seconde{suffix}", blockedEmpty: "Cette adresse IP a été bloquée pendant 24 heures.",
            invalidRinkEmpty: "Cet ID de patinoire n’est pas valide.", finalAttempt: "Dernière tentative : un autre ID de patinoire invalide bloquera cette adresse IP pendant 24 heures.",
            enterRink: "Saisissez un ID de patinoire à six caractères.", ipBlocked: "Cette adresse IP est bloquée. L’accès reprendra dans {time}.",
            finalAttemptAvailable: "Dernière tentative disponible dans {time}. Un autre ID de patinoire invalide bloquera cette adresse IP pendant 24 heures.",
            invalidRinkDelay: "ID de patinoire invalide. Réessayez dans {time}.", connectedWaitingVideo: "Connecté à {code}. En attente d’une vidéo.",
            preparingVideo: "Connecté à {code}. Préparation de la vidéo sélectionnée.", reverseUnavailable: "La lecture arrière n’est pas affichée dans le lecteur passif.",
            waitForward: "Connecté à {code}. En attente de la lecture avant.", adjustVolume: "Connecté à {code}. Réglez le volume pour activer la lecture avec le son.",
            connectingEmpty: "Connexion à ReVue VRO…", connecting: "Connexion à {code}…", rinkNotCreated: "L’ID de patinoire {code} n’a pas été créé.",
            serverNotReached: "Le serveur ReVue-Remote est inaccessible.", playbackUnreadable: "La mise à jour de lecture est illisible.",
            connectedWaitingVro: "Connecté à {code}. En attente de ReVue VRO.", connectionInterrupted: "La connexion à {code} a été interrompue. Reconnexion…",
            soundEnabled: "Connecté à {code}. Son activé; en attente de ReVue VRO."
        }
    };

    let sessionCode = "";
    let eventSource = null;
    let currentVideoId = "";
    let currentVideoSource = "";
    let latestState = null;
    let appliedState = null;
    let stateApplySequence = 0;
    let reconnectTimer = null;
    let connectivityProbeSequence = 0;
    let lastConnectivityResultSequence = 0;
    let cachedVideoKey = "";
    let requestedCacheKey = "";
    let cachedObjectUrl = "";
    let cacheAbortController = null;
    let cacheGeneration = 0;
    let rinkIdDelayTimer = null;
    let panelType = "technical";
    let judgingReviewActive = false;
    let judgingVideoId = "";
    let judgingSourceStartSeconds = 0;
    let judgingPositionSeconds = 0;
    let judgingIsPlaying = false;
    let judgingLastVideoTime = Number.NaN;
    let judgingTimelinePointerActive = false;
    let stopwatchEnabled = false;
    let stopwatchAnchorSeconds = Number.NaN;
    let autoplayMutedByBrowser = false;
    let language = localStorage.getItem("ReVueRemoteLanguage") === "fr" ? "fr" : "en";
    let lastStatus = null;
    let lastEmpty = null;
    let connectivityState = "checking";
    let connectivityMessage = "";

    const CONNECTIVITY_INTERVAL_MS = 3000;
    const CONNECTIVITY_TIMEOUT_MS = 3000;
    const FAST_CONNECTION_LIMIT_MS = 500;
    const VIDEO_CACHE_NAME = "revue-remote-active-video-v1";
    const PANEL_SESSION_KEY = "ReVueRemotePanelType";

    function t(key, values = {}) {
        const template = translations[language][key] || translations.en[key] || key;
        return template.replace(/\{(\w+)\}/g, (_, name) => String(values[name] ?? ""));
    }

    function updateStaticTranslations() {
        document.documentElement.lang = language;
        document.querySelectorAll("[data-i18n]").forEach(element => { element.textContent = t(element.dataset.i18n); });
        document.querySelectorAll("[data-i18n-aria]").forEach(element => { element.setAttribute("aria-label", t(element.dataset.i18nAria)); });
        document.querySelectorAll("[data-i18n-title]").forEach(element => { element.setAttribute("title", t(element.dataset.i18nTitle)); });
        sortPanelTypeMenu();
        updatePanelTypeLabel();
        updateLanguageLabel();
        updateJudgingControls();
        updateReviewIndicator(latestState);
        if (lastStatus) playbackStatus.textContent = t(lastStatus.key, lastStatus.values);
        if (lastEmpty) emptyStateMessage.textContent = t(lastEmpty.key, lastEmpty.values);
        setConnectivityState(connectivityState, connectivityState === "checking" ? t("checking") : connectivityMessage);
    }

    function normalizeCode(value) {
        return String(value || "").toUpperCase().replace(/[^A-Z0-9]/g, "").slice(0, 6);
    }

    function apiUrl(path) {
        return new URL(`api/${String(path || "").replace(/^\/+/, "")}`, document.baseURI).toString();
    }

    function setStatus(key, values = {}) {
        lastStatus = { key, values };
        playbackStatus.textContent = t(key, values);
    }

    function setStatusText(message) {
        lastStatus = null;
        playbackStatus.textContent = message;
    }

    function formatCountdown(totalSeconds) {
        const seconds = Math.max(0, Math.ceil(Number(totalSeconds) || 0));
        if (seconds < 60) return t("secondsCount", { count: seconds, suffix: seconds === 1 ? "" : "s" });
        const hours = Math.floor(seconds / 3600);
        const minutes = Math.floor((seconds % 3600) / 60);
        const remainder = seconds % 60;
        return hours > 0
            ? `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`
            : `${minutes}:${String(remainder).padStart(2, "0")}`;
    }

    function clearRinkIdDelay() {
        if (rinkIdDelayTimer !== null) window.clearInterval(rinkIdDelayTimer);
        rinkIdDelayTimer = null;
        sessionCodeInput.disabled = false;
    }

    function startRinkIdDelay(payload) {
        clearRinkIdDelay();
        closeConnection();
        const blocked = !!payload?.blocked;
        const finalAttempt = !!payload?.finalAttempt;
        const delaySeconds = Math.max(1, Number(payload?.retryAfterSeconds) || 1);
        const endsAt = Date.now() + delaySeconds * 1000;
        sessionCodeInput.value = "";
        sessionCodeInput.disabled = true;
        setEmpty(blocked ? "blockedEmpty" : "invalidRinkEmpty");

        const update = () => {
            const remaining = Math.max(0, Math.ceil((endsAt - Date.now()) / 1000));
            if (remaining <= 0) {
                clearRinkIdDelay();
                if (blocked) setEmpty("waitingForRinkId");
                setStatus(finalAttempt ? "finalAttempt" : "enterRink");
                if (!blocked) sessionCodeInput.focus();
                return;
            }

            if (blocked) {
                setStatus("ipBlocked", { time: formatCountdown(remaining) });
            } else if (finalAttempt) {
                setStatus("finalAttemptAvailable", { time: formatCountdown(remaining) });
            } else {
                setStatus("invalidRinkDelay", { time: formatCountdown(remaining) });
            }
        };

        update();
        rinkIdDelayTimer = window.setInterval(update, 250);
    }

    function playbackStatusText(state, isPlaying) {
        const zoomPercent = Math.round(Math.max(1, Number(state?.zoomScale || 1)) * 100);
        const zoomText = zoomPercent === 100 ? "" : t("zoom", { percent: zoomPercent });
        if (!isPlaying) return t("playbackPaused", { zoom: zoomText });
        const rate = Math.max(0.05, Math.min(4, Number(state?.playbackRate || 1)));
        const speed = Math.abs(rate - 1) < 0.0005
            ? t("normalSpeed")
            : t("speed", { speed: rate.toFixed(2).replace(/\.00$/, "").replace(/(\.\d)0$/, "$1") });
        return t("playingAt", { speed, zoom: zoomText });
    }

    function isRecordingState(state) {
        return String(state?.mode || "").toLowerCase() === "recording";
    }

    function isReviewState(state) {
        return !!state?.playbackEnabled && !!state?.videoId && !isRecordingState(state) &&
            String(state?.mode || "").toLowerCase() !== "ready";
    }

    function updateReviewIndicator(state) {
        reviewIndicator.classList.remove("live", "review", "hidden");
        if (isRecordingState(state)) {
            reviewIndicator.classList.add("live");
            reviewIndicatorText.textContent = t("live");
        } else if (isReviewState(state)) {
            reviewIndicator.classList.add("review");
            const startedAt = Math.max(0, Number(state?.reviewStartedAtUnixMs) || 0);
            const elapsedSeconds = startedAt > 0 ? Math.max(0, (Date.now() - startedAt) / 1000) : 0;
            const wholeSeconds = Math.floor(elapsedSeconds);
            const minutes = Math.floor(wholeSeconds / 60);
            const seconds = wholeSeconds % 60;
            reviewIndicatorText.innerHTML =
                `<span class="reviewTimeBlock"><span class="reviewTimeLabel">${t("review")}</span>` +
                `<span class="reviewTimeValue">${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}</span></span>`;
        } else {
            reviewIndicator.classList.add("hidden");
            reviewIndicatorText.textContent = "";
        }
    }

    function isJudgingReview(state = latestState) {
        return panelType !== "technical" && isReviewState(state);
    }

    function timelineDuration(state = latestState) {
        return Math.max(0, Number(state?.timelineDurationSeconds) || 0);
    }

    function updateJudgingControls() {
        const active = isJudgingReview() && judgingReviewActive;
        const hideTimelineDuringBroadcast = panelType !== "technical" && isRecordingState(latestState);
        timelineControlRow.hidden = hideTimelineDuringBroadcast;
        timelineCanvas.setAttribute("aria-hidden", hideTimelineDuringBroadcast ? "true" : "false");
        judgingTransport.classList.toggle("hidden", !active);
        refereeStopwatch.classList.toggle("hidden", !active || panelType !== "referee");
        refereeStopwatch.classList.toggle("active", stopwatchEnabled);
        refereeStopwatch.setAttribute("aria-pressed", stopwatchEnabled ? "true" : "false");
        refereeStopwatch.setAttribute("title", t(stopwatchEnabled ? "clearStopwatch" : "setStopwatchZero"));
        refereeStopwatch.setAttribute("aria-label", t(stopwatchEnabled ? "clearStopwatch" : "setStopwatchZero"));
        timelineCanvas.classList.toggle("interactive", active);
        timelineCanvas.tabIndex = active ? 0 : -1;
        timelineCanvas.setAttribute("aria-disabled", active ? "false" : "true");
        judgingPlayPause.textContent = t(judgingIsPlaying ? "pause" : "play");
        judgingPlayPause.setAttribute("aria-label", t(judgingIsPlaying ? "pauseVideo" : "playVideo"));
    }

    function judgingStatusText() {
        return playbackStatusText(latestState, judgingIsPlaying);
    }

    function statusWithAutoplayNotice(message) {
        return autoplayMutedByBrowser ? t("autoplayMuted", { message }) : message;
    }

    async function playWithAutoplayFallback() {
        try {
            await video.play();
            return true;
        } catch (error) {
            if (video.muted || video.volume <= 0) return false;
            try {
                video.muted = true;
                muteInput.checked = true;
                autoplayMutedByBrowser = true;
                await video.play();
                return true;
            } catch {
                video.muted = false;
                muteInput.checked = false;
                autoplayMutedByBrowser = false;
                return false;
            }
        }
    }

    function setConnectivityState(state, message) {
        connectivityState = state;
        connectivityMessage = message;
        networkIndicator.classList.remove("checking", "good", "slow", "disconnected");
        networkIndicator.classList.add(state);
        networkLatency.textContent = message;
        networkIndicator.setAttribute(
            "aria-label",
            state === "disconnected"
                ? t("serverDisconnected")
                : state === "checking"
                ? t("checkingServer")
                : t("serverLatency", { message })
        );
    }

    async function probeServerConnectivity() {
        const sequence = ++connectivityProbeSequence;
        const controller = new AbortController();
        const startedAt = performance.now();
        const timeoutId = window.setTimeout(() => controller.abort(), CONNECTIVITY_TIMEOUT_MS);

        try {
            const response = await fetch(apiUrl(`health?probe=${Date.now()}-${sequence}`), {
                cache: "no-store",
                signal: controller.signal
            });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);

            const latencyMs = Math.max(0, Math.round(performance.now() - startedAt));
            if (sequence < lastConnectivityResultSequence) return;
            lastConnectivityResultSequence = sequence;
            setConnectivityState(
                latencyMs <= FAST_CONNECTION_LIMIT_MS ? "good" : "slow",
                `${latencyMs} ms`
            );
        } catch {
            if (sequence < lastConnectivityResultSequence) return;
            lastConnectivityResultSequence = sequence;
            setConnectivityState("disconnected", t("disconnected"));
        } finally {
            window.clearTimeout(timeoutId);
        }
    }

    function setEmpty(key, values = {}) {
        lastEmpty = { key, values };
        emptyStateMessage.textContent = t(key, values);
        emptyState.classList.toggle("waitingForVideo", key === "waitingForVideo");
        emptyState.classList.remove("hidden");
    }

    function hideEmpty() {
        emptyState.classList.add("hidden");
    }

    function closeConnection() {
        if (eventSource) eventSource.close();
        eventSource = null;
        if (reconnectTimer !== null) clearTimeout(reconnectTimer);
        reconnectTimer = null;
    }

    function hostedPosition(state) {
        return Math.max(0, Number(state?.positionSeconds || 0));
    }

    function normalizeToDuration(position) {
        if (!Number.isFinite(video.duration) || video.duration <= 0) return position;
        return position % video.duration;
    }

    function timelinePosition(state) {
        if (judgingReviewActive && isJudgingReview(state)) {
            return Math.max(0, judgingPositionSeconds);
        }
        const timelineBase = Number(state?.timelinePositionSeconds || 0);
        const videoBase = Number(state?.positionSeconds || 0);
        if (
            String(state?.videoId || "") === currentVideoId &&
            Number.isFinite(video.currentTime) &&
            Number.isFinite(videoBase)
        ) {
            return Math.max(0, timelineBase + (Number(video.currentTime) - videoBase));
        }
        return Math.max(0, timelineBase);
    }

    function effectiveTimelineDuration(state, playheadSeconds = timelinePosition(state)) {
        const clips = Array.isArray(state?.clips) ? state.clips : [];
        const programStart = state?.programStartSeconds == null ? Number.NaN : Number(state.programStartSeconds);
        const halfway = state?.halfwaySeconds == null ? Number.NaN : Number(state.halfwaySeconds);
        const markerEnd = Number.isFinite(programStart) && Number.isFinite(halfway)
            ? programStart + halfway
            : 0;
        const clipEnd = clips.reduce((max, clip) => Math.max(max, Number(clip?.endSeconds) || 0), 0);
        const requiredDuration = Math.max(playheadSeconds, clipEnd, markerEnd, 0);
        let duration = Math.max(0.001, Number(state?.timelineDurationSeconds) || 0);
        if (isRecordingState(state)) {
            duration = Math.max(175, duration);
            while (requiredDuration > duration) duration += 60;
        } else {
            duration = Math.max(duration, requiredDuration);
        }
        return duration;
    }

    function formatTimelineTime(seconds) {
        const value = Math.max(0, Math.floor(Number(seconds) || 0));
        return `${Math.floor(value / 60)}:${String(value % 60).padStart(2, "0")}`;
    }

    function formatSignedTimelineTime(seconds) {
        const value = Number(seconds) || 0;
        return `${value < 0 ? "-" : ""}${formatTimelineTime(Math.abs(value))}`;
    }

    function formatStopwatchOffset(seconds) {
        const value = Number(seconds) || 0;
        const absolute = Math.abs(value);
        const wholeSeconds = Math.floor(absolute);
        const minutes = Math.floor(wholeSeconds / 60);
        const secondsPart = wholeSeconds % 60;
        const hundredths = Math.min(99, Math.floor((absolute - wholeSeconds) * 100 + 1e-6));
        return `${value < 0 ? "-" : ""}${String(minutes).padStart(2, "0")}:${String(secondsPart).padStart(2, "0")}:${String(hundredths).padStart(2, "0")}`;
    }

    function formatProgramPlayTime(seconds) {
        const value = Number(seconds) || 0;
        const absolute = Math.abs(value);
        const wholeSeconds = Math.floor(absolute);
        const minutes = Math.floor(wholeSeconds / 60);
        const secondsPart = wholeSeconds % 60;
        const hundredths = Math.min(99, Math.floor((absolute - wholeSeconds) * 100 + 1e-6));
        const formatted = `${String(minutes).padStart(2, "0")}:${String(secondsPart).padStart(2, "0")}:${String(hundredths).padStart(2, "0")}`;
        return value < 0 ? `-${formatted}` : formatted;
    }

    function drawTimeline() {
        if (!timelineCanvas) return;
        const rect = timelineCanvas.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        const width = Math.max(1, Math.round(rect.width));
        const height = Math.max(1, Math.round(rect.height));
        const pixelWidth = Math.max(1, Math.round(width * dpr));
        const pixelHeight = Math.max(1, Math.round(height * dpr));
        if (timelineCanvas.width !== pixelWidth || timelineCanvas.height !== pixelHeight) {
            timelineCanvas.width = pixelWidth;
            timelineCanvas.height = pixelHeight;
        }

        const ctx = timelineCanvas.getContext("2d");
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, width, height);
        ctx.fillStyle = "#142b35";
        ctx.fillRect(0, 0, width, height);

        const clips = Array.isArray(latestState?.clips) ? latestState.clips : [];
        const playheadSeconds = timelinePosition(latestState);
        const openClipStart = latestState?.openClipStartSeconds == null
            ? Number.NaN
            : Number(latestState.openClipStartSeconds);
        const programStart = latestState?.programStartSeconds == null
            ? Number.NaN
            : Number(latestState.programStartSeconds);
        const halfway = latestState?.halfwaySeconds == null
            ? Number.NaN
            : Number(latestState.halfwaySeconds);
        const useShiftedTimeline = Number.isFinite(programStart) && programStart >= 0;
        const displayOriginSeconds = useShiftedTimeline ? programStart : 0;
        const duration = effectiveTimelineDuration(latestState, playheadSeconds);
        timelineCanvas.setAttribute("aria-valuemax", String(Math.round(duration)));
        timelineCanvas.setAttribute("aria-valuenow", String(Math.round(playheadSeconds)));
        timelineCanvas.setAttribute("aria-valuetext", formatSignedTimelineTime(playheadSeconds - displayOriginSeconds));
        const xFor = seconds => Math.max(0, Math.min(width, (Number(seconds) / duration) * width));
        const barTop = 3;
        const barHeight = 36;
        const tickHeight = 9;
        const clipTop = barTop + 2;
        const clipHeight = barHeight - 2 - tickHeight;
        const barBottom = barTop + barHeight;

        // Match the VRO timeline: replay remains dark, while live recording
        // paints only the elapsed portion of the recording rail white.
        if (isRecordingState(latestState)) {
            ctx.fillStyle = "rgba(255,255,255,0.95)";
            ctx.fillRect(0, clipTop, xFor(playheadSeconds), clipHeight);
        }

        ctx.font = "600 13px Segoe UI, Arial, sans-serif";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        for (const clip of clips) {
            const start = Number(clip?.startSeconds);
            const end = Number(clip?.endSeconds);
            const index = Number(clip?.index);
            if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) continue;
            const x1 = xFor(start);
            const x2 = xFor(end);
            ctx.fillStyle = "#314784";
            ctx.fillRect(x1, clipTop, Math.max(1, x2 - x1), clipHeight);
            if (Number.isFinite(index)) {
                ctx.fillStyle = "#ffffff";
                ctx.fillText(String(index), x1 + Math.max(1, x2 - x1) / 2, clipTop + clipHeight / 2);
            }
        }

        if (
            latestState?.mode === "recording" &&
            Number.isFinite(openClipStart) &&
            openClipStart >= 0 &&
            playheadSeconds > openClipStart
        ) {
            const x1 = xFor(openClipStart);
            const x2 = xFor(playheadSeconds);
            ctx.fillStyle = "rgba(255, 55, 75, 0.95)";
            ctx.fillRect(x1, clipTop, Math.max(1, x2 - x1), clipHeight);
        }

        const showStopwatch = panelType === "referee" && judgingReviewActive &&
            stopwatchEnabled && Number.isFinite(stopwatchAnchorSeconds);
        const stopwatchElapsed = showStopwatch ? playheadSeconds - stopwatchAnchorSeconds : 0;
        if (showStopwatch) {
            const anchorX = xFor(stopwatchAnchorSeconds);
            const currentX = xFor(playheadSeconds);
            ctx.fillStyle = "rgba(80, 255, 40, 0.52)";
            ctx.fillRect(Math.min(anchorX, currentX), clipTop, Math.max(1, Math.abs(currentX - anchorX)), clipHeight);
            ctx.strokeStyle = "#ffe600";
            ctx.lineWidth = 3;
            ctx.beginPath();
            ctx.moveTo(Math.round(anchorX) + 0.5, Math.max(0, clipTop - 2));
            ctx.lineTo(Math.round(anchorX) + 0.5, clipTop + clipHeight + 2);
            ctx.stroke();
        }

        const drawMarker = (seconds, color, lineWidth) => {
            if (!Number.isFinite(seconds) || seconds < 0) return;
            const x = Math.round(xFor(seconds)) + 0.5;
            ctx.strokeStyle = color;
            ctx.lineWidth = lineWidth;
            ctx.beginPath();
            ctx.moveTo(x, clipTop);
            ctx.lineTo(x, clipTop + clipHeight);
            ctx.stroke();
        };
        if (Number.isFinite(programStart) && programStart >= 0) {
            drawMarker(programStart, "#39ff14", 3);
        }
        if (Number.isFinite(programStart) && programStart >= 0 && Number.isFinite(halfway) && halfway > 0) {
            drawMarker(programStart + halfway, "#ffbf00", 4);
        }

        ctx.strokeStyle = "rgba(255,214,64,0.98)";
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.moveTo(0, barBottom - tickHeight + 0.5);
        ctx.lineTo(width, barBottom - tickHeight + 0.5);
        ctx.stroke();

        ctx.strokeStyle = "rgba(255,255,255,0.9)";
        ctx.lineWidth = 1;
        const minDisplaySeconds = -displayOriginSeconds;
        const maxDisplaySeconds = duration - displayOriginSeconds;
        const firstTickDisplaySeconds = useShiftedTimeline
            ? Math.ceil(minDisplaySeconds / 5) * 5
            : 0;
        for (let displaySeconds = firstTickDisplaySeconds; displaySeconds <= maxDisplaySeconds + 0.001; displaySeconds += 5) {
            const timelineSeconds = useShiftedTimeline ? displaySeconds + displayOriginSeconds : displaySeconds;
            const x = Math.round(xFor(timelineSeconds)) + 0.5;
            const major = Math.abs(displaySeconds / 15 - Math.round(displaySeconds / 15)) < 1e-9;
            ctx.beginPath();
            ctx.moveTo(x, barBottom - tickHeight);
            ctx.lineTo(x, major ? barBottom : barBottom - Math.round(tickHeight / 2));
            ctx.stroke();
        }

        ctx.fillStyle = "rgba(255,255,255,0.95)";
        ctx.font = "600 9px Segoe UI, Arial, sans-serif";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        const labelY = barBottom + (height - barBottom) / 2;
        const firstLabelDisplaySeconds = useShiftedTimeline
            ? Math.ceil(minDisplaySeconds / 15) * 15
            : 15;
        if (useShiftedTimeline && minDisplaySeconds < -0.001 && firstLabelDisplaySeconds > minDisplaySeconds + 0.001) {
            ctx.textAlign = "left";
            ctx.fillText(formatSignedTimelineTime(minDisplaySeconds), 2, labelY);
        }
        for (let displaySeconds = firstLabelDisplaySeconds; displaySeconds <= maxDisplaySeconds + 0.001; displaySeconds += 15) {
            const timelineSeconds = useShiftedTimeline ? displaySeconds + displayOriginSeconds : displaySeconds;
            ctx.textAlign = "center";
            ctx.fillText(
                useShiftedTimeline ? formatSignedTimelineTime(displaySeconds) : formatTimelineTime(displaySeconds),
                Math.round(xFor(timelineSeconds)),
                labelY
            );
        }

        const playheadX = Math.round(xFor(playheadSeconds)) + 0.5;
        const showProgramTime = isReviewState(latestState);
        if (showProgramTime) {
            const playheadWidth = 12;
            const playheadHeight = 43;
            ctx.fillStyle = "#39ff14";
            ctx.fillRect(playheadX - playheadWidth / 2, 0, playheadWidth, playheadHeight);
            ctx.strokeStyle = "#043a00";
            ctx.lineWidth = 2;
            ctx.strokeRect(playheadX - playheadWidth / 2, 0, playheadWidth, playheadHeight);
        }

        replayProgramTimeIndicator.classList.toggle("hidden", !showProgramTime);
        if (showProgramTime) {
            replayProgramTimeIndicator.textContent = formatProgramPlayTime(playheadSeconds - displayOriginSeconds);
            replayProgramTimeIndicator.style.left = `${Math.max(0, Math.min(1, playheadSeconds / duration)) * 100}%`;
        }

        if (showStopwatch) {
            const label = formatStopwatchOffset(stopwatchElapsed);
            refereeTimer.textContent = label;
            refereeTimer.classList.remove("hidden");
            const timerWidth = Math.max(50, refereeTimer.offsetWidth);
            const timerX = Math.max(timerWidth / 2 + 2, Math.min(width - timerWidth / 2 - 2, playheadX));
            refereeTimer.style.left = `${timerX}px`;
        } else {
            refereeTimer.classList.add("hidden");
            refereeTimer.textContent = "";
        }
    }

    function applyZoomState(state) {
        const scale = Math.max(1, Math.min(2.5, Number(state?.zoomScale || 1)));
        const offsetX = Math.max(-2, Math.min(0, Number(state?.zoomOffsetX || 0)));
        const offsetY = Math.max(-2, Math.min(0, Number(state?.zoomOffsetY || 0)));
        const tx = offsetX * Math.max(1, video.clientWidth || 1);
        const ty = offsetY * Math.max(1, video.clientHeight || 1);
        video.style.transform = `matrix(${scale},0,0,${scale},${tx},${ty})`;
    }

    function waitForVideoMetadata(sequence) {
        if (video.readyState >= 1) return Promise.resolve();
        return new Promise(resolve => {
            let completed = false;
            const finish = () => {
                if (completed) return;
                completed = true;
                video.removeEventListener("loadedmetadata", finish);
                resolve();
            };
            video.addEventListener("loadedmetadata", finish, { once: true });
            window.setTimeout(finish, 10000);
        }).then(() => sequence === stateApplySequence);
    }

    function seekExactly(position, sequence) {
        const target = normalizeToDuration(position);
        if (!Number.isFinite(target) || sequence !== stateApplySequence) return Promise.resolve(false);
        if (Math.abs(Number(video.currentTime || 0) - target) <= 0.004) return Promise.resolve(true);

        return new Promise(resolve => {
            let completed = false;
            const finish = () => {
                if (completed) return;
                completed = true;
                video.removeEventListener("seeked", finish);
                resolve(sequence === stateApplySequence);
            };
            video.addEventListener("seeked", finish, { once: true });
            try {
                video.currentTime = target;
            } catch {
                finish();
                return;
            }
            window.setTimeout(finish, 1500);
        });
    }

    async function purgeCachedVideo() {
        cacheGeneration++;
        cacheAbortController?.abort();
        cacheAbortController = null;
        cachedVideoKey = "";
        if (cachedObjectUrl) URL.revokeObjectURL(cachedObjectUrl);
        cachedObjectUrl = "";
        if ("caches" in window) {
            try { await caches.delete(VIDEO_CACHE_NAME); } catch { }
        }
    }

    async function cacheRecordingVideo(videoId, sourceUrl) {
        if (!("caches" in window) || typeof fetch !== "function") return;
        const videoKey = `${sessionCode}:${videoId}`;
        if (requestedCacheKey === videoKey) return;
        if (cachedVideoKey === videoKey && (cachedObjectUrl || cacheAbortController)) return;

        requestedCacheKey = videoKey;
        const generation = cacheGeneration + 1;
        await purgeCachedVideo();
        if (requestedCacheKey !== videoKey) return;
        cacheGeneration = generation;
        cachedVideoKey = videoKey;
        const controller = new AbortController();
        cacheAbortController = controller;

        try {
            let cache = await caches.open(VIDEO_CACHE_NAME);
            let response = await cache.match(sourceUrl);
            if (!response) {
                const fetched = await fetch(sourceUrl, { cache: "reload", signal: controller.signal });
                if (!fetched.ok) throw new Error(`HTTP ${fetched.status}`);
                await cache.put(sourceUrl, fetched.clone());
                response = fetched;
            }

            const blob = await response.blob();
            if (controller.signal.aborted || generation !== cacheGeneration || cachedVideoKey !== videoKey) return;
            cachedObjectUrl = URL.createObjectURL(blob);

            // Never replace the source during initial recording. Once VRO
            // stops or enters replay, re-apply the latest state and seek to
            // its exact transport position using the complete local copy.
            if (latestState && String(latestState.videoId || "") === videoId && latestState.mode !== "recording") {
                applyState(latestState).catch(() => { });
            }
        } catch (error) {
            if (error?.name !== "AbortError") console.warn("Video could not be cached; continuing with network playback.", error);
            if (requestedCacheKey === videoKey) requestedCacheKey = "";
            if (cachedVideoKey === videoKey && !cachedObjectUrl) cachedVideoKey = "";
        } finally {
            if (cacheAbortController === controller) cacheAbortController = null;
        }
    }

    function resetJudgingReview() {
        judgingReviewActive = false;
        judgingVideoId = "";
        judgingSourceStartSeconds = 0;
        judgingPositionSeconds = 0;
        judgingIsPlaying = false;
        judgingLastVideoTime = Number.NaN;
        stopwatchEnabled = false;
        stopwatchAnchorSeconds = Number.NaN;
        updateJudgingControls();
    }

    async function seekJudgingPosition(positionSeconds, sequence = stateApplySequence) {
        const duration = timelineDuration();
        judgingPositionSeconds = Math.max(0, Math.min(duration, Number(positionSeconds) || 0));
        const sourcePosition = judgingSourceStartSeconds + judgingPositionSeconds;
        await seekExactly(sourcePosition, sequence);
        if (sequence !== stateApplySequence) return;
        judgingLastVideoTime = Number(video.currentTime);
        drawTimeline();
        setStatusText(judgingStatusText());
    }

    async function enterJudgingReview(state, sequence, sourceChanged) {
        const videoId = String(state?.videoId || "");
        const startingTimelinePosition = Math.max(0, Math.min(
            timelineDuration(state),
            Number(state?.timelinePositionSeconds) || 0
        ));

        if (!judgingReviewActive || judgingVideoId !== videoId) {
            judgingReviewActive = true;
            judgingVideoId = videoId;
            judgingSourceStartSeconds = Math.max(
                0,
                (Number(state?.positionSeconds) || 0) - (Number(state?.timelinePositionSeconds) || 0)
            );
            judgingPositionSeconds = startingTimelinePosition;
            judgingIsPlaying = false;
            judgingLastVideoTime = Number.NaN;
            video.pause();
            await seekJudgingPosition(judgingPositionSeconds, sequence);
        } else if (sourceChanged) {
            const wasPlaying = judgingIsPlaying;
            video.pause();
            await seekJudgingPosition(judgingPositionSeconds, sequence);
            if (wasPlaying && sequence === stateApplySequence) {
                if (!await playWithAutoplayFallback()) judgingIsPlaying = false;
            }
        }

        if (sequence !== stateApplySequence) return;
        hideEmpty();
        video.playbackRate = 1;
        appliedState = state;
        updateJudgingControls();
        drawTimeline();
        setStatusText(statusWithAutoplayNotice(judgingStatusText()));
    }

    async function toggleJudgingPlayback() {
        if (!judgingReviewActive || !isJudgingReview()) return;
        if (judgingIsPlaying) {
            judgingIsPlaying = false;
            judgingLastVideoTime = Number.NaN;
            video.pause();
        } else {
            if (judgingPositionSeconds >= timelineDuration() - 0.01) {
                await seekJudgingPosition(0);
            }
            judgingIsPlaying = true;
            judgingLastVideoTime = Number(video.currentTime);
            try {
                if (!await playWithAutoplayFallback()) throw new Error("Playback was blocked.");
            } catch {
                judgingIsPlaying = false;
                judgingLastVideoTime = Number.NaN;
            }
        }
        updateJudgingControls();
        setStatusText(judgingStatusText());
    }

    async function applyState(state) {
        const sequence = ++stateApplySequence;
        const previousState = appliedState;
        latestState = state;
        updateJudgingControls();
        const videoId = String(state?.videoId || "");
        videoFileName.textContent = String(state?.videoFileName || "");
        applyZoomState(state);
        updateReviewIndicator(state);
        drawTimeline();

        if (!videoId) {
            video.pause();
            video.classList.add("preloading");
            resetJudgingReview();
            appliedState = state;
            setEmpty("waitingForVideo");
            setStatus("connectedWaitingVideo", { code: sessionCode });
            return;
        }

        const networkSource = apiUrl(`sessions/${encodeURIComponent(sessionCode)}/videos/${encodeURIComponent(videoId)}/content`);
        const videoKey = `${sessionCode}:${videoId}`;

        if (state?.playbackEnabled !== true) {
            video.pause();
            video.classList.add("preloading");
            resetJudgingReview();
            const cachedSourceAvailable = cachedVideoKey === videoKey && !!cachedObjectUrl;
            const preloadSource = cachedSourceAvailable ? cachedObjectUrl : networkSource;
            if (videoId !== currentVideoId || currentVideoSource !== preloadSource) {
                currentVideoId = videoId;
                currentVideoSource = preloadSource;
                video.src = preloadSource;
                video.load();
            }
            appliedState = state;
            setEmpty("waitingForVideo");
            setStatus("preparingVideo", { code: sessionCode });
            return;
        }

        video.classList.remove("preloading");

        if (state.mode === "recording" && state.isPlaying && cachedVideoKey !== videoKey) {
            cacheRecordingVideo(videoId, networkSource).catch(() => { });
        }

        const cachedSourceAvailable = cachedVideoKey === videoKey && !!cachedObjectUrl;
        const keepCachedDuringRecording = state.mode === "recording" && currentVideoSource === cachedObjectUrl;
        const desiredSource = cachedSourceAvailable && (state.mode !== "recording" || keepCachedDuringRecording)
            ? cachedObjectUrl
            : networkSource;
        const videoChanged = videoId !== currentVideoId || desiredSource !== currentVideoSource;
        if (videoChanged) {
            video.pause();
            currentVideoId = videoId;
            currentVideoSource = desiredSource;
            video.src = desiredSource;
            video.load();
            await waitForVideoMetadata(sequence);
            if (sequence !== stateApplySequence) return;
        }

        if (isJudgingReview(state)) {
            await enterJudgingReview(state, sequence, videoChanged);
            return;
        }

        resetJudgingReview();

        if (state.mode === "reverse-unavailable") {
            video.pause();
            setEmpty("reverseUnavailable");
            setStatus("waitForward", { code: sessionCode });
            return;
        }

        hideEmpty();
        video.playbackRate = Math.max(0.05, Math.min(4, Number(state.playbackRate || 1)));
        const target = normalizeToDuration(hostedPosition(state));
        const discontinuityChanged =
            Number(state?.playbackDiscontinuity || 0) !== Number(previousState?.playbackDiscontinuity || 0);
        const resumed = !!state.isPlaying && !previousState?.isPlaying;
        const modeChanged = String(state?.mode || "") !== String(previousState?.mode || "");

        // Do not chase ordinary network drift during uninterrupted playback.
        // Periodic seeking or rate changes can skip frames; alignment is only
        // performed at an operator-visible transport boundary.
        const alignTransportBoundary = videoChanged || discontinuityChanged || resumed || modeChanged || !state.isPlaying;

        if (alignTransportBoundary) {
            video.pause();
            await seekExactly(target, sequence);
            if (sequence !== stateApplySequence) return;
            drawTimeline();
        }

        appliedState = state;
        if (state.isPlaying) {
            try {
                if (!await playWithAutoplayFallback()) throw new Error("Playback was blocked.");
                setStatusText(statusWithAutoplayNotice(playbackStatusText(state, true)));
            } catch {
                setStatus("adjustVolume", { code: sessionCode });
            }
        } else {
            video.pause();
            setStatusText(playbackStatusText(state, false));
        }
    }

    async function connect(code) {
        code = normalizeCode(code);
        sessionCodeInput.value = code;
        if (!/^[A-Z0-9]{6}$/.test(code)) {
            setStatus("enterRink");
            return;
        }

        closeConnection();
        video.pause();
        sessionCode = code;
        currentVideoId = "";
        currentVideoSource = "";
        latestState = null;
        appliedState = null;
        stateApplySequence++;
        resetJudgingReview();
        updateReviewIndicator(null);
        setEmpty("connectingEmpty");
        setStatus("connecting", { code });
        history.replaceState(null, "", `?session=${encodeURIComponent(code)}`);

        try {
            const validation = await fetch(apiUrl(`sessions/${encodeURIComponent(code)}/validate`), { cache: "no-store" });
            if (!validation.ok) {
                let payload = {};
                try { payload = await validation.json(); } catch { }
                if (payload?.retryAfterSeconds || payload?.blocked) {
                    startRinkIdDelay(payload);
                } else {
                    setEmpty("invalidRinkEmpty");
                    setStatus("rinkNotCreated", { code });
                }
                return;
            }
            clearRinkIdDelay();
        } catch {
            setStatus("serverNotReached");
            return;
        }

        eventSource = new EventSource(apiUrl(`sessions/${encodeURIComponent(code)}/events`));
        eventSource.addEventListener("playback", event => {
            try {
                applyState(JSON.parse(event.data)).catch(() => { });
            } catch {
                setStatus("playbackUnreadable");
            }
        });
        eventSource.onopen = () => setStatus("connectedWaitingVro", { code });
        eventSource.onerror = () => {
            closeConnection();
            setStatus("connectionInterrupted", { code });
            reconnectTimer = setTimeout(() => connect(code), 2000);
        };
    }

    controls.addEventListener("submit", event => {
        event.preventDefault();
        connect(sessionCodeInput.value);
    });

    sessionCodeInput.addEventListener("input", () => {
        sessionCodeInput.value = normalizeCode(sessionCodeInput.value);
        if (sessionCodeInput.value.length === 6) connect(sessionCodeInput.value);
    });

    function updatePanelTypeLabel() {
        panelTypeLabel.textContent = t(
            panelType === "referee" ? "referee" : panelType === "judging" ? "judge" : "technicalPanel"
        );
    }

    function sortPanelTypeMenu() {
        const locale = language === "fr" ? "fr-CA" : "en-CA";
        [...panelTypeMenu.querySelectorAll("[data-panel-type]")]
            .sort((a, b) => t(a.dataset.i18n).localeCompare(t(b.dataset.i18n), locale))
            .forEach(option => panelTypeMenu.append(option));
    }

    function updateLanguageLabel() {
        languageValueLabel.textContent = language === "fr" ? "Français" : "English";
        languageMenu.querySelectorAll("[data-language]").forEach(option => {
            option.setAttribute("aria-selected", option.dataset.language === language ? "true" : "false");
        });
    }

    function setLanguage(nextLanguage) {
        language = nextLanguage === "fr" ? "fr" : "en";
        languageSelect.value = language;
        localStorage.setItem("ReVueRemoteLanguage", language);
        updateStaticTranslations();
        if (latestState) applyState(latestState).catch(() => { });
    }

    panelTypeInput.addEventListener("change", () => {
        panelType = panelTypeInput.value === "referee"
            ? "referee"
            : panelTypeInput.value === "judging"
                ? "judging"
                : "technical";
        sessionStorage.setItem(PANEL_SESSION_KEY, panelType);
        updatePanelTypeLabel();
        panelTypeMenu.querySelectorAll("[data-panel-type]").forEach(option => {
            option.setAttribute("aria-selected", option.dataset.panelType === panelType ? "true" : "false");
        });
        resetJudgingReview();
        if (latestState) applyState(latestState).catch(() => { });
    });

    function closePanelTypeMenu() {
        panelTypeMenu.classList.add("hidden");
        panelTypeButton.setAttribute("aria-expanded", "false");
    }

    function closeLanguageMenu() {
        languageMenu.classList.add("hidden");
        languageButton.setAttribute("aria-expanded", "false");
    }

    panelTypeButton.addEventListener("click", () => {
        const opening = panelTypeMenu.classList.contains("hidden");
        panelTypeMenu.classList.toggle("hidden", !opening);
        panelTypeButton.setAttribute("aria-expanded", opening ? "true" : "false");
    });

    panelTypeMenu.addEventListener("click", event => {
        const option = event.target.closest("[data-panel-type]");
        if (!option) return;
        panelTypeInput.value = option.dataset.panelType;
        panelTypeInput.dispatchEvent(new Event("change"));
        closePanelTypeMenu();
        panelTypeButton.focus();
    });

    languageButton.addEventListener("click", () => {
        const opening = languageMenu.classList.contains("hidden");
        languageMenu.classList.toggle("hidden", !opening);
        languageButton.setAttribute("aria-expanded", opening ? "true" : "false");
    });

    languageMenu.addEventListener("click", event => {
        const option = event.target.closest("[data-language]");
        if (!option) return;
        setLanguage(option.dataset.language);
        closeLanguageMenu();
        languageButton.focus();
    });

    document.addEventListener("pointerdown", event => {
        if (!event.target.closest(".panelControl")) closePanelTypeMenu();
        if (!event.target.closest(".languageControl")) closeLanguageMenu();
    });

    document.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            closePanelTypeMenu();
            closeLanguageMenu();
        }
    });

    judgingPlayPause.addEventListener("click", () => {
        toggleJudgingPlayback().catch(() => { });
    });

    refereeStopwatch.addEventListener("click", () => {
        if (panelType !== "referee" || !judgingReviewActive || !isJudgingReview()) return;
        if (stopwatchEnabled) {
            stopwatchEnabled = false;
            stopwatchAnchorSeconds = Number.NaN;
        } else {
            stopwatchEnabled = true;
            stopwatchAnchorSeconds = judgingPositionSeconds;
        }
        updateJudgingControls();
        drawTimeline();
    });

    function seekJudgingFromPointer(event) {
        if (!judgingReviewActive || !isJudgingReview()) return;
        const rect = timelineCanvas.getBoundingClientRect();
        const ratio = Math.max(0, Math.min(1, (event.clientX - rect.left) / Math.max(1, rect.width)));
        const duration = effectiveTimelineDuration(latestState);
        judgingPositionSeconds = ratio * duration;
        judgingLastVideoTime = Number.NaN;
        try { video.currentTime = normalizeToDuration(judgingSourceStartSeconds + judgingPositionSeconds); } catch { }
        drawTimeline();
    }

    timelineCanvas.addEventListener("pointerdown", event => {
        if (!judgingReviewActive || !isJudgingReview()) return;
        judgingTimelinePointerActive = true;
        timelineCanvas.setPointerCapture?.(event.pointerId);
        seekJudgingFromPointer(event);
    });
    timelineCanvas.addEventListener("pointermove", event => {
        if (judgingTimelinePointerActive) seekJudgingFromPointer(event);
    });
    timelineCanvas.addEventListener("pointerup", event => {
        if (!judgingTimelinePointerActive) return;
        seekJudgingFromPointer(event);
        judgingTimelinePointerActive = false;
        timelineCanvas.releasePointerCapture?.(event.pointerId);
    });
    timelineCanvas.addEventListener("pointercancel", () => {
        judgingTimelinePointerActive = false;
    });
    timelineCanvas.addEventListener("keydown", event => {
        if (!judgingReviewActive || !isJudgingReview()) return;
        let delta = 0;
        if (event.key === "ArrowLeft") delta = event.shiftKey ? -5 : -1;
        if (event.key === "ArrowRight") delta = event.shiftKey ? 5 : 1;
        if (!delta) return;
        event.preventDefault();
        seekJudgingPosition(judgingPositionSeconds + delta).catch(() => { });
    });

    volumeInput.addEventListener("input", () => {
        setVolume(volumeInput.value, true);
    });

    muteInput.addEventListener("change", () => {
        setMuted(muteInput.checked, true);
    });

    function setVolume(value, activateSound = false) {
        const percent = Math.max(0, Math.min(100, Math.round(Number(value) || 0)));
        volumeInput.value = String(percent);
        video.volume = percent / 100;
        localStorage.setItem("ReVueRemoteVolume", String(video.volume));
        activateAudio(activateSound);
    }

    function setMuted(muted, activateSound = false) {
        muteInput.checked = !!muted;
        video.muted = !!muted;
        if (activateSound) autoplayMutedByBrowser = false;
        localStorage.setItem("ReVueRemoteMuted", muted ? "true" : "false");
        activateAudio(activateSound);
        if (activateSound && judgingReviewActive && isJudgingReview()) {
            setStatusText(judgingStatusText());
        }
    }

    function activateAudio(activateSound) {
        if (!activateSound) return;

        if (judgingReviewActive && isJudgingReview()) {
            if (judgingIsPlaying && video.paused) video.play().catch(() => { });
        } else if (latestState?.isPlaying) {
            applyState(latestState).catch(() => { });
        } else if (currentVideoId && video.volume > 0 && !video.muted) {
            // A user gesture is required by major browsers before later
            // synchronized playback may begin with sound. Briefly starting and
            // pausing the already-loaded element grants that permission.
            const target = normalizeToDuration(hostedPosition(latestState));
            video.play()
                .then(() => {
                    video.pause();
                    if (Number.isFinite(target)) video.currentTime = target;
                    setStatus("soundEnabled", { code: sessionCode });
                })
                .catch(() => { });
        }
    }

    video.addEventListener("loadedmetadata", () => {
        if (latestState) applyState(latestState).catch(() => { });
        drawTimeline();
    });
    video.addEventListener("contextmenu", event => event.preventDefault());

    const savedVolume = Number(localStorage.getItem("ReVueRemoteVolume"));
    const initialVolume = Number.isFinite(savedVolume) ? Math.max(0, Math.min(1, savedVolume)) : 1;
    setVolume(initialVolume * 100, false);
    setMuted(localStorage.getItem("ReVueRemoteMuted") === "true", false);
    languageSelect.value = language;
    updateStaticTranslations();
    const savedPanelType = sessionStorage.getItem(PANEL_SESSION_KEY);
    panelType = savedPanelType === "referee" ? "referee" : savedPanelType === "judging" ? "judging" : "technical";
    panelTypeInput.value = panelType;
    panelTypeInput.dispatchEvent(new Event("change"));
    updateJudgingControls();
    video.loop = true;

    const animatePassiveUi = () => {
        updateReviewIndicator(latestState);
        if (judgingReviewActive && judgingIsPlaying && isJudgingReview()) {
            const currentVideoTime = Number(video.currentTime);
            if (Number.isFinite(currentVideoTime) && Number.isFinite(judgingLastVideoTime) && !video.paused) {
                let advanced = currentVideoTime - judgingLastVideoTime;
                if (advanced < -0.25 && Number.isFinite(video.duration) && video.duration > 0) {
                    advanced += video.duration;
                }
                if (advanced >= 0 && advanced < 2) judgingPositionSeconds += advanced;
            }
            judgingLastVideoTime = currentVideoTime;
            const duration = timelineDuration();
            if (judgingPositionSeconds >= duration) {
                judgingPositionSeconds = duration;
                judgingIsPlaying = false;
                judgingLastVideoTime = Number.NaN;
                video.pause();
                updateJudgingControls();
                setStatusText(judgingStatusText());
            }
            drawTimeline();
        } else if (latestState?.isPlaying) {
            drawTimeline();
        }
        requestAnimationFrame(animatePassiveUi);
    };
    requestAnimationFrame(animatePassiveUi);
    window.addEventListener("resize", () => {
        applyZoomState(latestState);
        drawTimeline();
    });

    function resumeExpectedPlayback() {
        if (document.hidden) return;
        if (judgingReviewActive && judgingIsPlaying && isJudgingReview()) {
            playWithAutoplayFallback().then(started => {
                if (!started) {
                    judgingIsPlaying = false;
                    updateJudgingControls();
                }
            });
        } else if (latestState?.isPlaying) {
            applyState(latestState).catch(() => { });
        }
    }

    window.addEventListener("pageshow", resumeExpectedPlayback);
    document.addEventListener("visibilitychange", resumeExpectedPlayback);

    probeServerConnectivity().catch(() => { });
    window.setInterval(() => {
        probeServerConnectivity().catch(() => { });
    }, CONNECTIVITY_INTERVAL_MS);

    const initialCode = normalizeCode(new URLSearchParams(location.search).get("session"));
    if (initialCode.length === 6) {
        sessionCodeInput.value = initialCode;
        connect(initialCode);
    }
})();
