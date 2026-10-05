(() => {
    const sessionCodeInput = document.getElementById("sessionCode");
    const panelTypeInput = document.getElementById("panelType");
    const panelTypeButton = document.getElementById("panelTypeButton");
    const panelTypeLabel = document.getElementById("panelTypeLabel");
    const panelTypeMenu = document.getElementById("panelTypeMenu");
    const refreshButton = document.getElementById("refreshButton");
    const languageSelect = document.getElementById("language");
    const languageButton = document.getElementById("languageButton");
    const languageValueLabel = document.getElementById("languageValueLabel");
    const languageMenu = document.getElementById("languageMenu");
    const volumeInput = document.getElementById("volume");
    const muteInput = document.getElementById("mute");
    const controls = document.getElementById("controls");
    const video = document.getElementById("video");
    const videoViewer = video.closest(".viewer");
    const liveTransitionFrame = document.getElementById("liveTransitionFrame");
    const emptyStateMessage = document.getElementById("emptyStateMessage");
    const testPatternLabel = document.getElementById("testPatternLabel") || document.querySelector(".testPatternLabel");
    const playbackStatus = document.getElementById("playbackStatus");
    const networkIndicator = document.getElementById("networkIndicator");
    const networkLatency = document.getElementById("networkLatency");
    const networkTransfer = document.getElementById("networkTransfer");
    const networkCache = document.getElementById("networkCache");
    const vroConnectionStatus = document.getElementById("vroConnectionStatus");
    const emptyState = document.getElementById("emptyState");
    const audioTestButton = document.getElementById("audioTestButton");
    const audioTestLabel = document.getElementById("audioTestLabel");
    const audioTestFeedback = document.getElementById("audioTestFeedback");
    const audioTestLevelText = document.getElementById("audioTestLevelText");
    const timelineCanvas = document.getElementById("timelineCanvas");
    const timelineSection = document.getElementById("timelineSection");
    const pageFrame = document.getElementById("pageFrame");
    const shell = pageFrame.querySelector(".shell");
    const timelineControlRow = document.getElementById("timelineControlRow");
    const videoFileName = document.getElementById("videoFileName");
    const judgingTransport = document.getElementById("judgingTransport");
    const judgingPlayPause = document.getElementById("judgingPlayPause");
    const refereeStopwatch = document.getElementById("refereeStopwatch");
    const refereeTimer = document.getElementById("refereeTimer");
    const replayProgramTimeIndicator = document.getElementById("replayProgramTimeIndicator");
    const reviewIndicator = document.getElementById("reviewIndicator");
    const reviewIndicatorText = document.getElementById("reviewIndicatorText");
    const communicationPanel = document.getElementById("communicationPanel");
    const communicationFeedback = document.getElementById("communicationFeedback");
    const communicationAlertStack = document.getElementById("communicationAlertStack");
    const communicationAlerts = {
        "judges-ready": document.getElementById("communicationAlertJudges"),
        "tech-panel-ready": document.getElementById("communicationAlertTech"),
        "competitor-scored": document.getElementById("communicationAlertScored")
    };

    const translations = {
        en: {
            language: "Language", waitingForRinkId: "Waiting for a Rink ID.", volume: "Volume", mute: "Mute",
            videoVolume: "Video volume", rinkId: "Rink ID", rinkIdInput: "Six-character Rink ID", role: "Role", refresh: "Refresh",
            viewerRole: "Viewer role", announcer: "Announcer", dataInputOperator: "Data Input Operator", dataSpecialist: "Data Specialist", judge: "Judge", referee: "Referee", technicalSpecialist1: "Technical Specialist 1", technicalSpecialist2: "Technical Specialist 2", technicalController: "Technical Controller", videoReplayOperator: "Video Replay Operator",
            remotePlayer: "Passive remote replay player", enterRinkId: "Welcome to ReVue-Remote\nEnter Rink ID", waitingForVideo: "Stand by for video...", noSignal: "NO SIGNAL", standBy: "STAND BY", playTestTone: "Sound check", stopTestTone: "Sound off", testToneUnavailable: "Audio test unavailable", testToneVolume: "Volume: {percent}%", audioMuted: "Audio is muted", operatorOffline: "ReVue VRO connection lost.", play: "Play", pause: "Pause",
            playVideo: "Play video", pauseVideo: "Pause video", setStopwatchZero: "Set stopwatch zero",
            clearStopwatch: "Clear stopwatch", videoTimeline: "Video timeline", videoPlaybackPosition: "Video playback position",
            checking: "CHECKING…", disconnected: "DISCONNECTED", checkingServer: "Checking server connectivity", serverDisconnected: "Server disconnected",
            serverLatency: "Server latency {message}", dataTransfer: "Data transfer {rate}", dataTransferUnavailable: "Data transfer unavailable", localVideoCache: "Local video cache {size}", localVideoCacheUnavailable: "Local video cache unavailable", vroOnline: "VRO Online", vroOffline: "VRO Offline", live: "LIVE", review: "Review:", normalSpeed: "normal speed",
            speed: "{speed}x speed", playbackPaused: "Playback paused{zoom}", playingAt: "Playing at {speed}{zoom}",
            zoom: " · Zoom {percent}%", autoplayMuted: "{message} · Muted by browser; uncheck Mute for sound",
            secondsCount: "{count} second{suffix}", blockedEmpty: "This IP address has been blocked for 24 hours.",
            invalidRinkEmpty: "This Rink ID is not valid.", finalAttempt: "Final attempt: another invalid Rink ID will block this IP address for 24 hours.",
            enterRink: "Enter a six-character Rink ID.", ipBlocked: "This IP address is blocked. Access resumes in {time}.",
            finalAttemptAvailable: "Final attempt available in {time}. Another invalid Rink ID will block this IP address for 24 hours.",
            invalidRinkDelay: "Invalid Rink ID. Try again in {time}.", connectedWaitingVideo: "Connected. Waiting for a video.",
            preparingVideo: "Waiting for video…", cacheUnavailable: "Video cache unavailable or full. Use a secure connection, free browser storage, and reload.", reverseUnavailable: "Reverse playback is not shown on the passive player.",
            waitForward: "Waiting for forward playback.", adjustVolume: "Adjust Volume to enable playback with sound.",
            connectingEmpty: "Connecting to ReVue VRO…", connecting: "Connecting…", rinkNotCreated: "This Rink ID has not been created.",
            serverNotReached: "The ReVue-Remote server could not be reached.", playbackUnreadable: "The playback update could not be read.",
            connectedWaitingVro: "Connected. Waiting for ReVue VRO.", connectionInterrupted: "Connection interrupted. Reconnecting…",
            panelStatus: "Panel status", judgesReady: "Judges", techPanelReady: "Tech",
            competitorScored: "Scored", judgesReadyAlert: "Judges Are Ready", judgesNotReadyAlert: "Judges Are Not Ready",
            techPanelReadyAlert: "Tech Panel Is Ready", techPanelNotReadyAlert: "Tech Panel Not Ready",
            competitorScoredAlert: "Competitor Scored", statusOn: "on", statusOff: "off",
            statusUnavailable: "unavailable", statusUpdateFailed: "Status update failed. Try again."
        },
        fr: {
            language: "Langue", waitingForRinkId: "En attente d’un ID de patinoire.", volume: "Volume", mute: "Muet",
            videoVolume: "Volume de la vidéo", rinkId: "ID de patinoire", rinkIdInput: "ID de patinoire à six caractères", role: "Rôle", refresh: "Actualiser",
            viewerRole: "Rôle du spectateur", announcer: "Annonceur", dataInputOperator: "Le RED (DIO)", dataSpecialist: "Spécialiste des données", judge: "Juge", referee: "Arbitre", technicalSpecialist1: "Spécialiste technique 1", technicalSpecialist2: "Spécialiste technique 2", technicalController: "Contrôleur technique", videoReplayOperator: "Opérateur de reprise vidéo",
            remotePlayer: "Lecteur de reprise à distance", enterRinkId: "Bienvenue dans ReVue-Remote\nSaisissez l’ID de patinoire", waitingForVideo: "En attente de la vidéo…", noSignal: "AUCUN SIGNAL", standBy: "EN ATTENTE", playTestTone: "Test sonore", stopTestTone: "Couper le son", testToneUnavailable: "Test audio indisponible", testToneVolume: "Volume : {percent} %", audioMuted: "Le son est coupé", operatorOffline: "Connexion à ReVue VRO perdue.", play: "Lire", pause: "Pause",
            playVideo: "Lire la vidéo", pauseVideo: "Mettre la vidéo en pause", setStopwatchZero: "Remettre le chronomètre à zéro",
            clearStopwatch: "Effacer le chronomètre", videoTimeline: "Chronologie de la vidéo", videoPlaybackPosition: "Position de lecture de la vidéo",
            checking: "VÉRIFICATION…", disconnected: "DÉCONNECTÉ", checkingServer: "Vérification de la connexion au serveur", serverDisconnected: "Serveur déconnecté",
            serverLatency: "Latence du serveur : {message}", dataTransfer: "Transfert de données {rate}", dataTransferUnavailable: "Transfert de données indisponible", localVideoCache: "Cache vidéo local {size}", localVideoCacheUnavailable: "Cache vidéo local indisponible", vroOnline: "VRO en ligne", vroOffline: "VRO hors ligne", live: "DIRECT", review: "Révision :", normalSpeed: "vitesse normale",
            speed: "vitesse {speed}x", playbackPaused: "Lecture en pause{zoom}", playingAt: "Lecture à {speed}{zoom}",
            zoom: " · Zoom {percent}%", autoplayMuted: "{message} · Son coupé par le navigateur; décochez Muet pour activer le son",
            secondsCount: "{count} seconde{suffix}", blockedEmpty: "Cette adresse IP a été bloquée pendant 24 heures.",
            invalidRinkEmpty: "Cet ID de patinoire n’est pas valide.", finalAttempt: "Dernière tentative : un autre ID de patinoire invalide bloquera cette adresse IP pendant 24 heures.",
            enterRink: "Saisissez un ID de patinoire à six caractères.", ipBlocked: "Cette adresse IP est bloquée. L’accès reprendra dans {time}.",
            finalAttemptAvailable: "Dernière tentative disponible dans {time}. Un autre ID de patinoire invalide bloquera cette adresse IP pendant 24 heures.",
            invalidRinkDelay: "ID de patinoire invalide. Réessayez dans {time}.", connectedWaitingVideo: "Connecté. En attente d’une vidéo.",
            preparingVideo: "En attente de la vidéo…", cacheUnavailable: "Cache vidéo indisponible ou plein. Utilisez une connexion sécurisée, libérez de l’espace et rechargez la page.", reverseUnavailable: "La lecture arrière n’est pas affichée dans le lecteur passif.",
            waitForward: "En attente de la lecture avant.", adjustVolume: "Réglez le volume pour activer la lecture avec le son.",
            connectingEmpty: "Connexion à ReVue VRO…", connecting: "Connexion…", rinkNotCreated: "Cet ID de patinoire n’a pas été créé.",
            serverNotReached: "Le serveur ReVue-Remote est inaccessible.", playbackUnreadable: "La mise à jour de lecture est illisible.",
            connectedWaitingVro: "Connecté. En attente de ReVue VRO.", connectionInterrupted: "Connexion interrompue. Reconnexion…",
            panelStatus: "État du panel", judgesReady: "Juges", techPanelReady: "Tech",
            competitorScored: "Noté", judgesReadyAlert: "Juges prêts", judgesNotReadyAlert: "Juges non prêts",
            techPanelReadyAlert: "Panel technique prêt", techPanelNotReadyAlert: "Panel technique non prêt",
            competitorScoredAlert: "Concurrent noté", statusOn: "activé", statusOff: "désactivé",
            statusUnavailable: "indisponible", statusUpdateFailed: "Échec de la mise à jour. Réessayez."
        }
    };

    let sessionCode = "";
    let eventSource = null;
    let viewerSessionId = "";
    let communicationRequestPending = "";
    const communicationAlertTimers = new Map();
    const communicationButtonTimers = new Map();
    const readyIndicatorOrder = [];
    const panelReadyIndicators = ["judges-ready", "tech-panel-ready"];
    const communicationIndicators = {
        "judges-ready": { roles: ["referee", "data-specialist"], property: "judgesReady", label: "judgesReady", alertLabel: "judgesReadyAlert" },
        "tech-panel-ready": { roles: ["technical-controller", "data-specialist"], property: "techPanelReady", label: "techPanelReady", alertLabel: "techPanelReadyAlert" },
        "competitor-scored": { roles: ["data-specialist"], property: "competitorScored", label: "competitorScored", alertLabel: "competitorScoredAlert" }
    };
    let currentVideoId = "";
    let currentVideoSource = "";
    let assignedVideoSource = "";
    let cacheSizeInFlight = false;
    let latestState = null;
    let appliedState = null;
    let stateApplySequence = 0;
    let reconnectTimer = null;
    let connectionAttempt = 0;
    let connectivityProbeSequence = 0;
    let lastConnectivityResultSequence = 0;
    let rinkIdDelayTimer = null;
    const panelTypeLabelKeys = {
        announcer: "announcer",
        "data-input-operator": "dataInputOperator",
        "data-specialist": "dataSpecialist",
        judging: "judge",
        referee: "referee",
        "technical-specialist-1": "technicalSpecialist1",
        "technical-specialist-2": "technicalSpecialist2",
        "technical-controller": "technicalController",
        "video-replay-operator": "videoReplayOperator"
    };
    const technicalPanelTypes = new Set([
        "data-input-operator", "technical-specialist-1", "technical-specialist-2",
        "technical-controller", "video-replay-operator"
    ]);
    const defaultPanelType = "technical-controller";
    let panelType = defaultPanelType;

    function isTechnicalPanel() {
        return technicalPanelTypes.has(panelType);
    }

    function isStandbyOnlyPanel() {
        return panelType === "announcer" || panelType === "data-specialist";
    }
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
    let displayedStatus = null;
    let pendingStatus = null;
    let statusDelayTimer = null;
    let statusVisibleAtMs = 0;
    let lastEmpty = null;
    let testToneContext = null;
    let testToneOscillator = null;
    let testToneGain = null;
    let pageFitScale = 1;
    let fitFrameRequest = 0;
    let connectivityState = "checking";
    let connectivityMessage = "";
    let transferRate = "";
    let videoCacheSizeBytes = null;
    let transferStatsInFlight = false;
    let transferStatsSequence = 0;
    let lastTransferredByteTotal = null;
    let lastTransferSampleAtMs = 0;
    let rinkMode = "Recorded";
    let liveHls = null;
    let liveErrorTimer = null;
    let liveTransitionTimer = null;
    let liveEventId = "";
    let livePrefetchTimer = null;
    let livePrefetchInFlight = false;
    let livePrefetchedUrls = new Set();
    const LIVE_CACHE_NAME = "revue-live-events-v1";

    const CONNECTIVITY_INTERVAL_MS = 3000;
    const CONNECTIVITY_TIMEOUT_MS = 3000;
    const FAST_CONNECTION_LIMIT_MS = 500;
    const TRANSFER_SAMPLE_INTERVAL_MS = 1000;
    const VRO_CONNECTION_INTERVAL_MS = 3000;
    const VIDEO_CACHE_SAMPLE_INTERVAL_MS = 5000;
    const VIDEO_CACHE_NAME = "revue-video-chunks-v3";
    const VIDEO_CACHE_SCRIPT_URL = new URL("./video-cache-sw.js?v=20261003-live-viewer-id", document.baseURI).href;
    const STATUS_MIN_DISPLAY_MS = 1000;
    const PANEL_SESSION_KEY = "ReVueRemotePanelType";
    const REFRESH_RINK_ID_KEY = "ReVueRemoteRefreshRinkId";
    const videoTransferViewerId = getVideoTransferViewerId();
    const videoCacheReady = initializeVideoCache();

    async function initializeVideoCache() {
        if (!("serviceWorker" in navigator) || !("caches" in window)) return false;
        try {
            await navigator.serviceWorker.register(VIDEO_CACHE_SCRIPT_URL, {
                scope: "./",
                updateViaCache: "none"
            });
            if (navigator.serviceWorker.controller?.scriptURL === VIDEO_CACHE_SCRIPT_URL) return true;
            return await new Promise(resolve => {
                const timeout = window.setTimeout(() => finish(false), 5000);
                function finish(ready) {
                    clearTimeout(timeout);
                    navigator.serviceWorker.removeEventListener("controllerchange", onChange);
                    resolve(ready);
                }
                function onChange() {
                    if (navigator.serviceWorker.controller?.scriptURL === VIDEO_CACHE_SCRIPT_URL) finish(true);
                }
                navigator.serviceWorker.addEventListener("controllerchange", onChange);
                onChange();
            });
        } catch {
            return false;
        }
    }

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
        updateTestPatternLabel(latestState);
        if (lastStatus) playbackStatus.textContent = t(lastStatus.key, lastStatus.values);
        updatePlaybackStatusVisibility();
        if (lastEmpty) emptyStateMessage.textContent = t(lastEmpty.key, lastEmpty.values);
        updateTestToneOutput();
        setConnectivityState(connectivityState, connectivityState === "checking" ? t("checking") : connectivityMessage);
        setTransferRate(transferRate);
        setVideoCacheSize(videoCacheSizeBytes);
        updateVroConnectionStatus();
        renderCommunicationStatus();
        scheduleViewportFit();
    }

    function renderCommunicationStatus(state = latestState) {
        const available = Boolean(sessionCode && viewerSessionId && state);
        for (const button of communicationPanel.querySelectorAll("[data-communication-indicator]")) {
            const indicator = communicationIndicators[button.dataset.communicationIndicator];
            const active = available && state.communicationStatus?.[indicator.property] === true;
            const dataSpecialistOnly = indicator.property === "competitorScored";
            button.hidden = dataSpecialistOnly && panelType !== "data-specialist" && !active;
            button.classList.toggle("active", active);
            button.classList.toggle("unknown", !available);
            button.disabled = !available || !indicator.roles.includes(panelType) || Boolean(communicationRequestPending);
            button.setAttribute("aria-pressed", active ? "true" : "false");
            button.setAttribute("aria-label", `${t(indicator.alertLabel)}: ${t(!available ? "statusUnavailable" : active ? "statusOn" : "statusOff")}`);
        }
    }

    function dismissCommunicationAlert(indicator) {
        const alert = communicationAlerts[indicator];
        if (!alert) return;
        const timer = communicationAlertTimers.get(indicator);
        if (timer !== undefined) clearTimeout(timer);
        communicationAlertTimers.delete(indicator);
        alert.classList.add("hidden");
        alert.classList.remove("visible");
        if (indicator === "competitor-scored") videoViewer.classList.remove("scoredFrameAlert");
    }

    function showCommunicationAlert(events) {
        for (const { indicator, active } of events) {
            const alert = communicationAlerts[indicator];
            if (alert) {
                if (indicator === "competitor-scored" && !active) {
                    dismissCommunicationAlert(indicator);
                    continue;
                }
                communicationAlertStack.appendChild(alert);
                alert.classList.toggle("not-ready", !active);
                if (indicator === "competitor-scored") videoViewer.classList.add("scoredFrameAlert");
                if (panelReadyIndicators.includes(indicator)) {
                    alert.classList.toggle("ready-first", readyIndicatorOrder[0] === indicator);
                    alert.classList.toggle("ready-second", readyIndicatorOrder[1] === indicator);
                    alert.querySelector(".communicationAlertReadyIcon").classList.toggle("hidden", !active);
                    alert.querySelector(".communicationAlertNotReadyIcon").classList.toggle("hidden", active);
                    alert.querySelector(".communicationAlertReadyMessage").classList.toggle("hidden", !active);
                    alert.querySelector(".communicationAlertNotReadyMessage").classList.toggle("hidden", active);
                }
                alert.classList.remove("hidden", "visible");
                requestAnimationFrame(() => alert.classList.add("visible"));
                const previousAlertTimer = communicationAlertTimers.get(indicator);
                if (previousAlertTimer !== undefined) clearTimeout(previousAlertTimer);
                communicationAlertTimers.set(indicator, window.setTimeout(() => dismissCommunicationAlert(indicator), 6000));
            }
            if (!active) continue;
            const button = communicationPanel.querySelector(`[data-communication-indicator="${indicator}"]`);
            if (!button) continue;
            if (panelReadyIndicators.includes(indicator)) {
                button.classList.toggle("ready-first", readyIndicatorOrder[0] === indicator);
                button.classList.toggle("ready-second", readyIndicatorOrder[1] === indicator);
            }
            button.classList.remove("justActivated");
            const previousTimer = communicationButtonTimers.get(indicator);
            if (previousTimer) clearTimeout(previousTimer);
            requestAnimationFrame(() => button.classList.add("justActivated"));
            communicationButtonTimers.set(indicator, window.setTimeout(() => {
                button.classList.remove("justActivated");
                communicationButtonTimers.delete(indicator);
            }, 2700));
        }
    }

    communicationPanel.addEventListener("click", async event => {
        const button = event.target.closest("[data-communication-indicator]");
        if (!button || button.disabled || !sessionCode || !viewerSessionId) return;
        const indicator = button.dataset.communicationIndicator;
        const definition = communicationIndicators[indicator];
        if (!definition || !definition.roles.includes(panelType)) return;
        const code = sessionCode;
        const token = viewerSessionId;
        const active = latestState?.communicationStatus?.[definition.property] !== true;
        communicationRequestPending = token;
        communicationFeedback.textContent = "";
        renderCommunicationStatus();
        try {
            const response = await fetch(apiUrl(`sessions/${encodeURIComponent(code)}/communication-status`), {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ viewerSessionId: token, indicator, active })
            });
            if (!response.ok) throw new Error("Status update failed");
        } catch {
            if (sessionCode === code) communicationFeedback.textContent = t("statusUpdateFailed");
        } finally {
            if (communicationRequestPending === token) communicationRequestPending = "";
            renderCommunicationStatus();
        }
    });

    function normalizeCode(value) {
        return String(value || "").toUpperCase().replace(/[^A-Z0-9]/g, "").slice(0, 6);
    }

    function apiUrl(path) {
        return new URL(`api/${String(path || "").replace(/^\/+/, "")}`, document.baseURI).toString();
    }

    function getVideoTransferViewerId() {
        // Duplicating a browser tab copies sessionStorage, including a saved
        // viewer ID. Give each page instance its own transfer limit instead.
        return globalThis.crypto?.randomUUID?.() ||
            `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
    }

    function videoContentUrl(videoId) {
        const url = new URL(apiUrl(`sessions/${encodeURIComponent(sessionCode)}/videos/${encodeURIComponent(videoId)}/content`));
        url.searchParams.set("viewer", videoTransferViewerId);
        return url.toString();
    }

    function livePreviewUrl() {
        const url = new URL(apiUrl(`sessions/${encodeURIComponent(sessionCode)}/live/preview/index.m3u8`));
        url.searchParams.set("viewer", videoTransferViewerId);
        return url.toString();
    }

    function liveEventUrl(eventId) {
        const url = new URL(apiUrl(`sessions/${encodeURIComponent(sessionCode)}/live/events/${encodeURIComponent(eventId)}/index.m3u8`));
        url.searchParams.set("viewer", videoTransferViewerId);
        return url.toString();
    }

    function stopLivePrefetch() {
        if (livePrefetchTimer !== null) clearInterval(livePrefetchTimer);
        livePrefetchTimer = null;
    }

    async function purgeLiveEventCache(eventId) {
        if (!sessionCode || !/^[a-f0-9]{32}$/.test(eventId)) return;
        const prefix = `/api/sessions/${sessionCode}/live/events/${eventId}/`;
        if (await videoCacheReady && navigator.serviceWorker.controller) {
            try {
                await new Promise((resolve, reject) => {
                    const channel = new MessageChannel();
                    const timeout = setTimeout(() => reject(new Error("Live cache purge timed out")), 10000);
                    channel.port1.onmessage = event => {
                        clearTimeout(timeout);
                        channel.port1.close();
                        event.data?.ok ? resolve() : reject(new Error("Live cache purge failed"));
                    };
                    navigator.serviceWorker.controller.postMessage({ type: "purge-live-event", prefix }, [channel.port2]);
                });
                return;
            } catch { }
        }
        try {
            const cache = await caches.open(LIVE_CACHE_NAME);
            await Promise.all((await cache.keys()).filter(request =>
                new URL(request.url).pathname.startsWith(prefix)).map(request => cache.delete(request)));
        } catch { }
    }

    async function prefetchLiveSegments(eventId) {
        if (livePrefetchInFlight || !sessionCode || eventId !== liveEventId ||
            isStandbyAfterStop(latestState) ||
            latestState?.mode === "preparing" || latestState?.mode === "recording" ||
            !await videoCacheReady) return;
        livePrefetchInFlight = true;
        try {
            const playlistUrl = liveEventUrl(eventId);
            const response = await fetch(playlistUrl, { cache: "no-store" });
            if (!response.ok) return;
            const playlist = await response.text();
            const names = [];
            const init = playlist.match(/#EXT-X-MAP:URI="([^"]+)"/);
            if (init) names.push(init[1]);
            for (const line of playlist.split(/\r?\n/)) {
                const name = line.trim();
                if (name && !name.startsWith("#")) names.push(name);
            }
            const eventPrefix = `/api/sessions/${sessionCode}/live/events/${eventId}/`;
            const urls = names.map(name => new URL(name, playlistUrl)).filter(url =>
                url.origin === location.origin && url.pathname.startsWith(eventPrefix));
            const cache = await caches.open(LIVE_CACHE_NAME);
            for (const url of urls) {
                if (eventId !== liveEventId || isStandbyAfterStop(latestState)) return;
                if (livePrefetchedUrls.has(url.pathname)) continue;
                if (await cache.match(url.href, { ignoreSearch: true })) {
                    livePrefetchedUrls.add(url.pathname);
                    continue;
                }
                const segment = await fetch(url.toString());
                if (segment.ok) {
                    await segment.arrayBuffer();
                    if (await cache.match(url.href, { ignoreSearch: true }))
                        livePrefetchedUrls.add(url.pathname);
                }
                // Playback has priority; fill missing history gradually after
                // Stop instead of downloading the whole event in one burst.
                break;
            }
        } catch { }
        finally { livePrefetchInFlight = false; }
    }

    function startLivePrefetch(eventId) {
        if (liveEventId === eventId && livePrefetchTimer !== null) return;
        stopLivePrefetch();
        liveEventId = eventId;
        livePrefetchedUrls = new Set();
        if (!eventId) return;
        void prefetchLiveSegments(eventId);
        livePrefetchTimer = setInterval(() => { void prefetchLiveSegments(eventId); }, 500);
    }

    function attachLiveHls(source, autoplay, startPositionSeconds =
        source.includes("/live/events/") ? 0 : -1) {
        if (assignedVideoSource === source) return false;
        if (rinkMode === "Live" && assignedVideoSource.includes("/live/")) {
            holdLiveFrame();
            if (liveTransitionTimer === null) {
                liveTransitionTimer = setTimeout(() => {
                    clearLiveFrame();
                    if (rinkMode === "Live" && video.readyState < 2)
                        setEmpty("waitingForVideo");
                }, 8000);
            }
        }
        if (liveErrorTimer !== null) {
            clearTimeout(liveErrorTimer);
            liveErrorTimer = null;
        }
        video.pause();
        liveHls?.destroy();
        liveHls = null;
        video.removeAttribute("src");
        video.load();
        currentVideoSource = source;
        assignedVideoSource = source;
        currentVideoId = source.includes("/live/events/") ? liveEventId : "";
        if (window.Hls?.isSupported()) {
            const hls = new window.Hls({
                lowLatencyMode: false,
                liveSyncDuration: 5,
                // Existing viewers follow the event from frame zero. A page
                // opened during recording joins the current live position.
                startPosition: startPositionSeconds,
                backBufferLength: 60,
                maxBufferLength: 10
            });
            liveHls = hls;
            hls.on(window.Hls.Events.MANIFEST_PARSED, () => {
                if (liveHls === hls && autoplay) void playWithAutoplayFallback();
            });
            hls.on(window.Hls.Events.ERROR, (_, detail) => {
                if (!detail.fatal || liveHls !== hls) return;
                console.warn("Live stream playback error", detail.type, detail.details,
                    detail.response?.code || "", source);
                if (detail.type === window.Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
                else {
                    const resumeAt = source.includes("/live/events/")
                        ? Math.max(0, Number(video.currentTime) || 0) : -1;
                    setTimeout(() => {
                    if (liveHls === hls && assignedVideoSource === source) {
                        holdLiveFrame();
                        hideEmpty();
                        if (liveTransitionTimer === null) {
                            liveTransitionTimer = setTimeout(() => {
                                clearLiveFrame();
                                if (rinkMode === "Live" && video.readyState < 2)
                                    setEmpty("waitingForVideo");
                            }, 8000);
                        }
                        assignedVideoSource = "";
                        attachLiveHls(source, autoplay, resumeAt);
                    }
                    }, 3000);
                }
            });
            hls.loadSource(source);
            hls.attachMedia(video);
        } else if (video.canPlayType("application/vnd.apple.mpegurl")) {
            if (source.includes("/live/events/")) {
                video.addEventListener("loadedmetadata", () => {
                    if (assignedVideoSource !== source) return;
                    if (startPositionSeconds >= 0) {
                        try { video.currentTime = startPositionSeconds; } catch { }
                    }
                    if (autoplay) void playWithAutoplayFallback();
                }, { once: true });
            }
            video.src = source;
            video.load();
            if (autoplay && !source.includes("/live/events/")) void playWithAutoplayFallback();
        } else setStatus("cacheUnavailable");
        return true;
    }

    function clearLiveFrame() {
        if (liveTransitionTimer !== null) clearTimeout(liveTransitionTimer);
        liveTransitionTimer = null;
        liveTransitionFrame.classList.remove("visible");
    }

    function holdLiveFrame() {
        if (video.readyState < 2 || !video.videoWidth || !video.videoHeight) return;
        try {
            liveTransitionFrame.width = video.videoWidth;
            liveTransitionFrame.height = video.videoHeight;
            liveTransitionFrame.getContext("2d").drawImage(video, 0, 0);
            liveTransitionFrame.classList.add("visible");
            if (liveTransitionTimer !== null) clearTimeout(liveTransitionTimer);
            liveTransitionTimer = setTimeout(() => {
                clearLiveFrame();
                if (rinkMode === "Live" && video.readyState < 2)
                    setEmpty("waitingForVideo");
            }, 8000);
        } catch { /* A frame is unavailable while the decoder changes streams. */ }
    }

    function transferStatsUrl() {
        const url = new URL(apiUrl(`sessions/${encodeURIComponent(sessionCode)}/transfer`));
        url.searchParams.set("viewer", videoTransferViewerId);
        return url.toString();
    }

    async function purgeBrowserVideoCache(source) {
        if (!source) return;
        if (await videoCacheReady && navigator.serviceWorker.controller) {
            try {
                await new Promise((resolve, reject) => {
                    const channel = new MessageChannel();
                    const timeout = window.setTimeout(() => reject(new Error("Cache purge timed out")), 10000);
                    channel.port1.onmessage = event => {
                        clearTimeout(timeout);
                        channel.port1.close();
                        event.data?.ok ? resolve() : reject(new Error("Cache purge failed"));
                    };
                    navigator.serviceWorker.controller.postMessage({ type: "purge-video", url: source }, [channel.port2]);
                });
                return;
            } catch {
                // Recover if an older worker cannot handle the message.
            }
        }
        try {
            const cache = await caches.open(VIDEO_CACHE_NAME);
            const path = new URL(source).pathname;
            await Promise.all((await cache.keys())
                .filter(request => new URL(request.url).pathname === path)
                .map(request => cache.delete(request)));
        } catch { }
        if (sessionCode) await fetch(apiUrl(`sessions/${encodeURIComponent(sessionCode)}/viewer-cache/clear`), {
            method: "POST", cache: "no-store"
        }).catch(() => { });
    }

    async function cachedVideoSize(source) {
        if (!source || !await videoCacheReady || cacheSizeInFlight) return;
        cacheSizeInFlight = true;
        try {
            if (rinkMode === "Live") {
                if (!liveEventId) { setVideoCacheSize(0); return; }
                const cache = await caches.open(LIVE_CACHE_NAME);
                const prefix = `/api/sessions/${sessionCode}/live/events/${liveEventId}/`;
                const requests = (await cache.keys()).filter(request => new URL(request.url).pathname.startsWith(prefix));
                let bytes = 0;
                for (const request of requests) {
                    const response = await cache.match(request);
                    bytes += Number(response?.headers.get("Content-Length") || 0);
                }
                setVideoCacheSize(bytes);
                return;
            }
            const cache = await caches.open(VIDEO_CACHE_NAME);
            const path = new URL(source).pathname;
            const keys = await cache.keys();
            const bytes = keys.reduce((sum, request) => {
                const url = new URL(request.url);
                return url.pathname === path && url.searchParams.has("revue_chunk")
                    ? sum + Number(url.searchParams.get("bytes") || 0) : sum;
            }, 0);
            if (source === currentVideoSource) setVideoCacheSize(bytes);
        } catch {
            if (source === currentVideoSource) setVideoCacheSize(null);
        } finally {
            cacheSizeInFlight = false;
        }
    }

    function reportVideoBuffer() {
        if (!currentVideoSource || !navigator.serviceWorker?.controller) return;
        const position = Number(video.currentTime) || 0;
        let bufferedAhead = 0;
        for (let index = 0; index < video.buffered.length; index++) {
            if (video.buffered.start(index) <= position + 0.05 && video.buffered.end(index) >= position) {
                bufferedAhead = Math.max(0, video.buffered.end(index) - position);
                break;
            }
        }
        navigator.serviceWorker.controller.postMessage({
            type: "video-buffer",
            url: currentVideoSource,
            duration: Number.isFinite(video.duration) ? video.duration : 0,
            position,
            bufferedAhead,
            mode: String(latestState?.mode || ""),
            recordedEnd: isReviewState(latestState)
                ? Math.max(0, Number(latestState.positionSeconds || 0) -
                    Number(latestState.timelinePositionSeconds || 0) +
                    Number(latestState.timelineDurationSeconds || 0))
                : null,
            active: !isStandbyAfterStop(latestState) && !isOperatorOfflineState(latestState) &&
                (isSelectedVideoAwaitingRecord(latestState) || latestState?.playbackEnabled === true)
        });
    }

    function statusesMatch(first, second) {
        return !!first && !!second && first.key === second.key &&
            (first.key !== null || first.message === second.message);
    }

    function refreshDisplayedStatus(status) {
        displayedStatus = status;
        lastStatus = status.key === null ? null : { key: status.key, values: status.values };
        playbackStatus.textContent = status.key === null ? status.message : t(status.key, status.values);
        updatePlaybackStatusVisibility();
        scheduleViewportFit();
    }

    function displayStatus(status) {
        if (statusDelayTimer !== null) clearTimeout(statusDelayTimer);
        statusDelayTimer = null;
        pendingStatus = null;
        refreshDisplayedStatus(status);
        statusVisibleAtMs = performance.now();
    }

    function requestStatus(status) {
        if (!displayedStatus) {
            displayStatus(status);
            return;
        }

        if (statusesMatch(displayedStatus, status)) {
            refreshDisplayedStatus(status);
            return;
        }

        pendingStatus = status;
        if (statusDelayTimer !== null) clearTimeout(statusDelayTimer);
        const remainingMs = Math.max(0, STATUS_MIN_DISPLAY_MS - (performance.now() - statusVisibleAtMs));
        statusDelayTimer = window.setTimeout(() => {
            const nextStatus = pendingStatus;
            if (nextStatus) displayStatus(nextStatus);
        }, remainingMs);
    }

    function setStatus(key, values = {}) {
        requestStatus({ key, values });
    }

    function setStatusText(message) {
        requestStatus({ key: null, message: String(message || "") });
    }

    function clearStatus() {
        if (statusDelayTimer !== null) clearTimeout(statusDelayTimer);
        statusDelayTimer = null;
        pendingStatus = null;
        displayedStatus = null;
        lastStatus = null;
        playbackStatus.textContent = "";
        playbackStatus.hidden = true;
        scheduleViewportFit();
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
        showEnterRinkId();
        const blocked = !!payload?.blocked;
        const finalAttempt = !!payload?.finalAttempt;
        const delaySeconds = Math.max(1, Number(payload?.retryAfterSeconds) || 1);
        const endsAt = Date.now() + delaySeconds * 1000;
        sessionCodeInput.value = "";
        sessionCodeInput.disabled = true;
        setEmpty(blocked ? "blockedEmpty" : "enterRinkId");

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

    function playbackStatusText(state, isPlaying, actualPlaybackRate = null) {
        const zoomPercent = isTechnicalPanel()
            ? Math.round(Math.max(1, Number(state?.zoomScale || 1)) * 100)
            : 100;
        const zoomText = zoomPercent === 100 ? "" : t("zoom", { percent: zoomPercent });
        if (!isPlaying) return t("playbackPaused", { zoom: zoomText });
        const rate = Math.max(0.05, Math.min(4, Number(actualPlaybackRate ?? state?.playbackRate ?? 1)));
        const speed = Math.abs(rate - 1) < 0.0005
            ? t("normalSpeed")
            : t("speed", { speed: rate.toFixed(2).replace(/\.00$/, "").replace(/(\.\d)0$/, "$1") });
        return t("playingAt", { speed, zoom: zoomText });
    }

    function isRecordingState(state) {
        return String(state?.mode || "").toLowerCase() === "recording";
    }

    function isOperatorOfflineState(state) {
        return String(state?.mode || "").toLowerCase() === "operator-offline";
    }

    function updateVroConnectionStatus() {
        const connected = /^[A-Z0-9]{6}$/.test(sessionCode) &&
            eventSource?.readyState === EventSource.OPEN &&
            latestState?.operatorConnected === true;
        const message = t(connected ? "vroOnline" : "vroOffline");
        if (vroConnectionStatus.textContent !== message) vroConnectionStatus.textContent = message;
        vroConnectionStatus.classList.toggle("connected", connected);
        vroConnectionStatus.classList.toggle("disconnected", !connected);
    }

    function isSnowScreenVisible() {
        return !emptyState.classList.contains("hidden") && emptyState.classList.contains("enterRinkId");
    }

    function isRinkIdDelayStatus() {
        return ["invalidRinkDelay", "finalAttemptAvailable", "ipBlocked"].includes(lastStatus?.key);
    }

    function updatePlaybackStatusVisibility() {
        playbackStatus.hidden = isStandbyAfterStop(latestState) || isOperatorOfflineState(latestState) ||
            (isSnowScreenVisible() && !isRinkIdDelayStatus()) || isRecordingState(latestState);
    }

    function isPostRecordingState(state) {
        return !!state?.playbackEnabled && !!state?.videoId && !isRecordingState(state) &&
            String(state?.mode || "").toLowerCase() !== "ready";
    }

    function isStandbyAfterStop(state) {
        return isStandbyOnlyPanel() && !!state?.videoId &&
            (isPostRecordingState(state) || Number(state?.reviewStartedAtUnixMs) > 0);
    }

    function isReviewState(state) {
        return !isStandbyOnlyPanel() && isPostRecordingState(state);
    }

    function syncVideoAudio() {
        // Technical roles hear the live/Recording feed according to their
        // own controls, but VRO replay must always be silent. Keep the saved
        // mute preference untouched so it is restored when Recording resumes.
        volumeInput.disabled = muteInput.checked;
        video.muted = muteInput.checked || (isTechnicalPanel() && isReviewState(latestState));
    }

    function isSelectedVideoAwaitingRecord(state) {
        return !!state?.videoId && (state.mode === "ready" || state.mode === "preparing") &&
            state.playbackEnabled !== true;
    }

    function updateTestPatternLabel(state) {
        testPatternLabel.textContent = t(isStandbyAfterStop(state) ||
            (isSelectedVideoAwaitingRecord(state) && !isOperatorOfflineState(state))
            ? "standBy" : "noSignal");
    }

    function updateReviewIndicator(state) {
        reviewIndicator.classList.remove("live", "review", "hidden");
        if (isRecordingState(state) && state?.playbackEnabled === true && state?.isPlaying === true &&
            !!state?.videoId && emptyState.classList.contains("hidden")) {
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
        return !isTechnicalPanel() && isReviewState(state);
    }

    function timelineDuration(state = latestState) {
        return Math.max(0, Number(state?.timelineDurationSeconds) || 0);
    }

    function updateJudgingControls() {
        const active = isJudgingReview() && judgingReviewActive;
        const hideTimelineDuringBroadcast = (rinkMode === "Live" && isOperatorOfflineState(latestState)) ||
            (!isTechnicalPanel() && isRecordingState(latestState));
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
        return playbackStatusText(latestState, judgingIsPlaying, video.playbackRate);
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
                muteInput.checked = true;
                syncVideoAudio();
                autoplayMutedByBrowser = true;
                await video.play();
                return true;
            } catch {
                muteInput.checked = false;
                syncVideoAudio();
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

    function setTransferRate(rate) {
        const numericRate = typeof rate === "number" && Number.isFinite(rate) ? rate : null;
        const displayRate = numericRate !== null
            ? `${numericRate.toFixed(2)} Mbps`
            : String(rate ?? "").trim();
        transferRate = displayRate;
        networkTransfer.textContent = displayRate || "— Mbps";
        networkTransfer.setAttribute(
            "aria-label",
            displayRate ? t("dataTransfer", { rate: displayRate }) : t("dataTransferUnavailable")
        );
    }

    function setVideoCacheSize(byteCount) {
        videoCacheSizeBytes = Number.isFinite(byteCount) && byteCount >= 0 ? byteCount : null;
        const size = videoCacheSizeBytes === null ? "" : `${(videoCacheSizeBytes / 1048576).toFixed(1)} MB`;
        networkCache.textContent = size || "— MB";
        networkCache.setAttribute(
            "aria-label",
            size ? t("localVideoCache", { size }) : t("localVideoCacheUnavailable")
        );
    }

    function resetTransferRate() {
        transferStatsSequence++;
        transferStatsInFlight = false;
        lastTransferredByteTotal = null;
        lastTransferSampleAtMs = 0;
        setTransferRate(0);
        setVideoCacheSize(0);
    }

    async function updateTransferRate() {
        if (isStandbyAfterStop(latestState)) {
            setTransferRate(0);
            return;
        }
        if (!currentVideoSource) {
            resetTransferRate();
            return;
        }

        if (transferStatsInFlight) return;
        transferStatsInFlight = true;
        const sequence = transferStatsSequence;
        const source = currentVideoSource;
        try {
            const response = await fetch(transferStatsUrl(), { cache: "no-store" });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const { bytes } = await response.json();
            if (sequence !== transferStatsSequence || source !== currentVideoSource) return;

            const sampledAtMs = performance.now();
            const transferredTotal = Math.max(0, Number(bytes) || 0);
            if (lastTransferredByteTotal === null) {
                lastTransferredByteTotal = transferredTotal;
                lastTransferSampleAtMs = sampledAtMs;
                setTransferRate("0.00 Mbps");
                return;
            }

            const elapsedMs = Math.max(1, sampledAtMs - lastTransferSampleAtMs);
            const transferredBytes = Math.max(0, transferredTotal - lastTransferredByteTotal);
            const megabitsPerSecond = transferredBytes * 8 / (elapsedMs * 1000);
            lastTransferredByteTotal = transferredTotal;
            lastTransferSampleAtMs = sampledAtMs;
            setTransferRate(`${megabitsPerSecond.toFixed(2)} Mbps`);
        } catch {
            if (sequence === transferStatsSequence) setTransferRate("");
        } finally {
            if (sequence === transferStatsSequence) transferStatsInFlight = false;
        }
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
        if (key !== "enterRinkId" && key !== "waitingForVideo" && key !== "operatorOffline") stopTestTone();
        lastEmpty = { key, values };
        emptyStateMessage.textContent = t(key, values);
        updateTestPatternLabel(latestState);
        emptyState.classList.toggle("waitingForVideo", key === "waitingForVideo");
        emptyState.classList.toggle("enterRinkId", key === "enterRinkId");
        emptyState.classList.toggle("operatorOffline", key === "operatorOffline");
        emptyState.classList.remove("hidden");
        updateReviewIndicator(latestState);
        updatePlaybackStatusVisibility();
        const showDiagnostics = /^[A-Z0-9]{6}$/.test(sessionCode);
        timelineSection.hidden = !showDiagnostics;
        timelineSection.classList.toggle("diagnosticsOnly", showDiagnostics);
        scheduleViewportFit();
    }

    function hideEmpty() {
        stopTestTone();
        emptyState.classList.add("hidden");
        updateReviewIndicator(latestState);
        updatePlaybackStatusVisibility();
        timelineSection.hidden = false;
        timelineSection.classList.remove("diagnosticsOnly");
        scheduleViewportFit();
    }

    function updateTestToneButton() {
        const key = testToneOscillator ? "stopTestTone" : "playTestTone";
        audioTestLabel.dataset.i18n = key;
        audioTestLabel.textContent = t(key);
        audioTestButton.setAttribute("aria-pressed", testToneOscillator ? "true" : "false");
    }

    function updateTestToneOutput() {
        const active = !!testToneOscillator;
        audioTestFeedback.classList.toggle("active", active);
        if (!active) {
            audioTestFeedback.classList.remove("audible");
            audioTestLevelText.textContent = "";
            return;
        }
        const percent = Math.max(0, Math.min(100, Number(volumeInput.value) || 0));
        audioTestFeedback.classList.toggle("audible", !muteInput.checked && percent > 0);
        audioTestLevelText.textContent = muteInput.checked ? t("audioMuted") : t("testToneVolume", { percent });
        testToneGain.gain.setTargetAtTime(muteInput.checked ? 0 : 0.05 * percent / 100,
            testToneContext.currentTime, 0.01);
    }

    function stopTestTone() {
        if (!testToneOscillator) return;
        const oscillator = testToneOscillator;
        const gain = testToneGain;
        const context = testToneContext;
        testToneOscillator = null;
        testToneGain = null;
        testToneContext = null;
        updateTestToneButton();
        updateTestToneOutput();
        try {
            gain.gain.setTargetAtTime(0, context.currentTime, 0.006);
            oscillator.stop(context.currentTime + 0.03);
        } catch { }
        window.setTimeout(() => context.close().catch(() => { }), 80);
    }

    async function startTestTone() {
        const AudioContextType = window.AudioContext || window.webkitAudioContext;
        if (!AudioContextType) {
            audioTestButton.disabled = true;
            audioTestLabel.dataset.i18n = "testToneUnavailable";
            audioTestLabel.textContent = t("testToneUnavailable");
            return;
        }

        try {
            const context = new AudioContextType();
            const oscillator = context.createOscillator();
            const gain = context.createGain();
            oscillator.type = "sine";
            oscillator.frequency.value = 1000;
            gain.gain.setValueAtTime(0, context.currentTime);
            oscillator.connect(gain).connect(context.destination);
            testToneContext = context;
            testToneOscillator = oscillator;
            testToneGain = gain;
            oscillator.start();
            updateTestToneButton();
            updateTestToneOutput();
            await context.resume();
        } catch {
            stopTestTone();
        }
    }

    audioTestButton.addEventListener("click", () => {
        if (testToneOscillator) stopTestTone();
        else startTestTone();
    });
    window.addEventListener("pagehide", stopTestTone);

    function closeConnection() {
        if (eventSource) eventSource.close();
        eventSource = null;
        for (const indicator of Object.keys(communicationAlerts)) dismissCommunicationAlert(indicator);
        viewerSessionId = "";
        communicationRequestPending = "";
        communicationFeedback.textContent = "";
        renderCommunicationStatus();
        if (reconnectTimer !== null) clearTimeout(reconnectTimer);
        reconnectTimer = null;
        updateVroConnectionStatus();
    }

    function openPlaybackEventStream(code, attempt) {
        const url = new URL(apiUrl(`sessions/${encodeURIComponent(code)}/events`), document.baseURI);
        url.searchParams.set("role", panelType);
        const source = new EventSource(url.href);
        eventSource = source;
        source.addEventListener("viewer-session", event => {
            if (attempt !== connectionAttempt || eventSource !== source) return;
            try {
                const token = JSON.parse(event.data);
                if (/^[a-f0-9]{32}$/i.test(token)) {
                    viewerSessionId = token;
                    renderCommunicationStatus();
                }
            } catch { }
        });
        source.addEventListener("playback", event => {
            if (attempt !== connectionAttempt || eventSource !== source) return;
            try {
                applyState(JSON.parse(event.data)).catch(() => { });
            } catch {
                setStatus("playbackUnreadable");
            }
        });
        source.onopen = () => {
            if (attempt !== connectionAttempt || eventSource !== source) return;
            setStatus("connectedWaitingVro", { code });
            updateVroConnectionStatus();
        };
        source.onerror = () => {
            if (attempt !== connectionAttempt || eventSource !== source) return;
            closeConnection();
            setStatus("connectionInterrupted", { code });
            reconnectTimer = setTimeout(() => connect(code), 2000);
        };
    }

    function showEnterRinkId() {
        clearLiveFrame();
        if (liveErrorTimer !== null) {
            clearTimeout(liveErrorTimer);
            liveErrorTimer = null;
        }
        const oldLiveEventId = liveEventId;
        stopLivePrefetch();
        liveHls?.destroy();
        liveHls = null;
        liveEventId = "";
        if (oldLiveEventId) void purgeLiveEventCache(oldLiveEventId);
        connectionAttempt++;
        closeConnection();
        video.pause();
        const sourceToPurge = currentVideoSource;
        if (video.hasAttribute("src")) {
            video.removeAttribute("src");
            video.load();
        }
        if (sourceToPurge && !sourceToPurge.includes("/live/")) purgeBrowserVideoCache(sourceToPurge).catch(() => { });
        sessionCode = "";
        currentVideoId = "";
        currentVideoSource = "";
        assignedVideoSource = "";
        rinkMode = "Recorded";
        latestState = null;
        appliedState = null;
        stateApplySequence++;
        resetJudgingReview();
        updateReviewIndicator(null);
        setEmpty("enterRinkId");
        setStatus("enterRink");
        const url = new URL(location.href);
        url.searchParams.delete("session");
        history.replaceState(null, "", url);
    }

    function hostedPosition(state) {
        return Math.max(0, Number(state?.positionSeconds || 0));
    }

    function normalizeToDuration(position) {
        if (!Number.isFinite(video.duration) || video.duration <= 0) return position;
        if (rinkMode === "Live" && assignedVideoSource.includes("/live/events/"))
            return Math.max(0, Math.min(position, Math.max(0, video.duration - 0.04)));
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
        const dpr = (window.devicePixelRatio || 1) * pageFitScale;
        const width = Math.max(1, timelineCanvas.clientWidth);
        const height = Math.max(1, timelineCanvas.clientHeight);
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
        const rawXFor = seconds => (Number(seconds) / duration) * width;
        const xFor = seconds => Math.max(0, Math.min(width, rawXFor(seconds)));
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
            const rawX = rawXFor(timelineSeconds);
            if (!Number.isFinite(rawX) || rawX < -0.5 || rawX > width + 0.5) continue;
            const x = Math.round(rawX) + 0.5;
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
        let lastLabelRight = -Infinity;
        const drawTimeLabel = (text, timelineSeconds) => {
            const rawX = rawXFor(timelineSeconds);
            if (!Number.isFinite(rawX) || rawX < -0.5 || rawX > width + 0.5) return;
            const textWidth = ctx.measureText(text).width;
            const edgePadding = 3;
            if (textWidth + edgePadding * 2 > width) return;
            const x = Math.max(edgePadding + textWidth / 2,
                Math.min(width - edgePadding - textWidth / 2, rawX));
            if (x - textWidth / 2 < lastLabelRight + 8) return;
            ctx.fillText(text, x, labelY);
            lastLabelRight = x + textWidth / 2;
        };
        if (useShiftedTimeline && minDisplaySeconds < -0.001 && firstLabelDisplaySeconds > minDisplaySeconds + 0.001) {
            drawTimeLabel(formatSignedTimelineTime(minDisplaySeconds), 0);
        }
        for (let displaySeconds = firstLabelDisplaySeconds; displaySeconds <= maxDisplaySeconds + 0.001; displaySeconds += 15) {
            const timelineSeconds = useShiftedTimeline ? displaySeconds + displayOriginSeconds : displaySeconds;
            drawTimeLabel(
                useShiftedTimeline ? formatSignedTimelineTime(displaySeconds) : formatTimelineTime(displaySeconds),
                timelineSeconds
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
        video.dataset.viewerRole = panelType;
        if (!isTechnicalPanel()) {
            // Judge-style review stays at the original framing even if
            // the operator zooms the same replay for a Technical role.
            video.style.transform = "none";
            return;
        }
        const scale = Math.max(1, Math.min(2.5, Number(state?.zoomScale || 1)));
        const offsetX = Math.max(-2, Math.min(0, Number(state?.zoomOffsetX || 0)));
        const offsetY = Math.max(-2, Math.min(0, Number(state?.zoomOffsetY || 0)));
        const tx = offsetX * Math.max(1, video.clientWidth || 1);
        const ty = offsetY * Math.max(1, video.clientHeight || 1);
        const transform = `matrix(${scale},0,0,${scale},${tx},${ty})`;
        if (video.style.transform !== transform) video.style.transform = transform;
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

    function showStandbyAfterStop(state) {
        stopLivePrefetch();
        clearLiveFrame();
        if (liveErrorTimer !== null) clearTimeout(liveErrorTimer);
        liveErrorTimer = null;
        liveHls?.destroy();
        liveHls = null;
        video.pause();
        const hadSource = !!assignedVideoSource || video.hasAttribute("src");
        assignedVideoSource = "";
        if (hadSource) {
            video.removeAttribute("src");
            video.load();
            transferStatsSequence++;
            transferStatsInFlight = false;
            lastTransferredByteTotal = null;
            lastTransferSampleAtMs = 0;
        }
        resetJudgingReview();
        appliedState = state;
        setEmpty("waitingForVideo");
        setTransferRate(0);
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
        // Judge-style playback is local, so reset the browser element before
        // any status update can read a prior technical transport rate.
        video.playbackRate = 1;
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
        appliedState = state;
        updateJudgingControls();
        drawTimeline();
        setStatusText(statusWithAutoplayNotice(judgingStatusText()));
    }

    async function applyLiveState(state, previousState, sequence) {
        if (isStandbyAfterStop(state)) {
            showStandbyAfterStop(state);
            return;
        }
        const eventId = String(state?.videoId || "");
        if (eventId && (eventId !== liveEventId || livePrefetchTimer === null)) {
            const oldEventId = liveEventId;
            startLivePrefetch(eventId);
            if (oldEventId && oldEventId !== eventId) void purgeLiveEventCache(oldEventId);
        }
        if (!eventId) {
            if (liveEventId && state?.operatorConnected === true) {
                const oldEventId = liveEventId;
                stopLivePrefetch();
                liveEventId = "";
                void purgeLiveEventCache(oldEventId);
            } else {
                // Losing the operator is not Next Competitor. Keep recorded
                // media cached, but stop requesting the abandoned event.
                stopLivePrefetch();
            }
            resetJudgingReview();
            video.playbackRate = 1;
            const hadLiveSource = assignedVideoSource.includes("/live/");
            const sourceChanged = attachLiveHls(livePreviewUrl(), true);
            appliedState = state;
            if (video.readyState >= 2) hideEmpty();
            else if (sourceChanged) {
                if (hadLiveSource) hideEmpty();
                else setEmpty("waitingForVideo");
            }
            return;
        }

        if (state.mode === "recording" || state.mode === "preparing") {
            resetJudgingReview();
            video.playbackRate = 1;
            const hadLiveSource = assignedVideoSource.includes("/live/");
            const recording = state.mode === "recording";
            const followedRecordStart = assignedVideoSource.includes("/live/preview/") &&
                (previousState?.mode === "ready" || previousState?.mode === "preparing");
            const sourceChanged = attachLiveHls(recording ? liveEventUrl(eventId) : livePreviewUrl(),
                true, recording && followedRecordStart ? 0 : -1);
            if (recording) currentVideoId = eventId;
            appliedState = state;
            if (video.readyState >= 2) hideEmpty();
            else if (sourceChanged) {
                if (hadLiveSource) hideEmpty();
                else setEmpty("waitingForVideo");
            }
            return;
        }

        const source = liveEventUrl(eventId);
        const sourceChanged = attachLiveHls(source, false);
        currentVideoId = eventId;
        if (sourceChanged || video.readyState < 1) {
            await waitForVideoMetadata(sequence);
            if (sequence !== stateApplySequence) return;
        }
        if (isJudgingReview(state)) {
            await enterJudgingReview(state, sequence, sourceChanged);
            return;
        }
        resetJudgingReview();
        if (state.mode === "reverse-unavailable") {
            video.pause();
            setEmpty("reverseUnavailable");
            appliedState = state;
            return;
        }
        hideEmpty();
        video.playbackRate = Math.max(0.05, Math.min(4, Number(state.playbackRate || 1)));
        const discontinuityChanged = Number(state.playbackDiscontinuity || 0) !==
            Number(previousState?.playbackDiscontinuity || 0);
        const transportChanged = sourceChanged || discontinuityChanged ||
            !!state.isPlaying !== !!previousState?.isPlaying || state.mode !== previousState?.mode || !state.isPlaying;
        if (transportChanged) {
            video.pause();
            await seekExactly(hostedPosition(state), sequence);
            if (sequence !== stateApplySequence) return;
        }
        appliedState = state;
        if (state.isPlaying) {
            if (!await playWithAutoplayFallback()) setStatus("adjustVolume");
            else setStatusText(statusWithAutoplayNotice(playbackStatusText(state, true)));
        } else {
            video.pause();
            setStatusText(playbackStatusText(state, false));
        }
        drawTimeline();
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
        const previousCommunicationStatus = latestState?.communicationStatus;
        const liveRink = rinkMode === "Live" || String(state?.sourceType || "").toLowerCase() === "live";
        const activeLiveRecording = liveRink && ["preparing", "recording"].includes(state?.mode);
        if (liveRink && !activeLiveRecording && (state?.operatorConnected !== true || isOperatorOfflineState(state))) {
            // A persisted replay belongs to the previous operator session.
            // Until a VRO takes control, display the incoming live preview,
            // with no old replay controls, clip timeline, or zoom applied.
            state = {
                ...state, sourceType: "Live", mode: "operator-offline",
                videoId: "", videoFileName: "", isPlaying: false, playbackEnabled: false,
                positionSeconds: 0, timelinePositionSeconds: 0, timelineDurationSeconds: 0,
                reviewStartedAtUnixMs: 0, programStartSeconds: null, halfwaySeconds: null,
                openClipStartSeconds: null, clips: [], zoomScale: 1, zoomOffsetX: 0, zoomOffsetY: 0
            };
        }
        const newlyActiveIndicators = latestState
            ? Object.entries(communicationIndicators)
                .filter(([, indicator]) => previousCommunicationStatus?.[indicator.property] === false &&
                    state.communicationStatus?.[indicator.property] === true)
                .map(([name]) => name)
            : [];
        const newlyInactiveIndicators = latestState
            ? panelReadyIndicators.filter(name => {
                const property = communicationIndicators[name].property;
                return previousCommunicationStatus?.[property] === true && state.communicationStatus?.[property] === false;
            })
            : [];
        const communicationAlertEvents = [
            ...newlyActiveIndicators.map(indicator => ({ indicator, active: true })),
            ...newlyInactiveIndicators.map(indicator => ({ indicator, active: false }))
        ];
        if (latestState && previousCommunicationStatus?.competitorScored === true &&
            state.communicationStatus?.competitorScored === false) {
            communicationAlertEvents.push({ indicator: "competitor-scored", active: false });
        }
        for (const name of panelReadyIndicators) {
            const property = communicationIndicators[name].property;
            if (state.communicationStatus?.[property] !== true) {
                const previousIndex = readyIndicatorOrder.indexOf(name);
                if (previousIndex !== -1) readyIndicatorOrder.splice(previousIndex, 1);
            }
        }
        if (latestState === null) {
            for (const name of panelReadyIndicators) {
                if (state.communicationStatus?.[communicationIndicators[name].property] === true) readyIndicatorOrder.push(name);
            }
        } else {
            for (const name of newlyActiveIndicators) {
                if (panelReadyIndicators.includes(name) && !readyIndicatorOrder.includes(name)) readyIndicatorOrder.push(name);
            }
        }
        latestState = state;
        renderCommunicationStatus(state);
        if (communicationAlertEvents.length) showCommunicationAlert(communicationAlertEvents);
        updateVroConnectionStatus();
        updateTestPatternLabel(state);
        updatePlaybackStatusVisibility();
        syncVideoAudio();
        updateJudgingControls();
        const videoId = String(state?.videoId || "");
        const operatorOnline = state?.operatorConnected === true && !isOperatorOfflineState(state);
        videoFileName.textContent = !liveRink && operatorOnline && videoId
            ? String(state?.videoFileName || "")
            : "";
        applyZoomState(state);
        updateReviewIndicator(state);
        drawTimeline();

        if (liveRink) {
            rinkMode = "Live";
            await applyLiveState(state, previousState, sequence);
            return;
        }

        if (!operatorOnline) {
            video.pause();
            if (video.hasAttribute("src")) {
                video.removeAttribute("src");
                video.load();
                currentVideoId = "";
                currentVideoSource = "";
                assignedVideoSource = "";
                resetTransferRate();
            }
            video.classList.add("preloading");
            resetJudgingReview();
            appliedState = state;
            setEmpty("operatorOffline");
            updateReviewIndicator(null);
            clearStatus();
            return;
        }

        if (isStandbyAfterStop(state)) {
            showStandbyAfterStop(state);
            return;
        }

        if (!videoId) {
            video.pause();
            if (currentVideoSource) {
                const sourceToPurge = currentVideoSource;
                video.removeAttribute("src");
                video.load();
                currentVideoId = "";
                currentVideoSource = "";
                assignedVideoSource = "";
                resetTransferRate();
                purgeBrowserVideoCache(sourceToPurge).catch(() => { });
            }
            video.classList.add("preloading");
            resetJudgingReview();
            appliedState = state;
            setEmpty("waitingForVideo");
            setStatus("connectedWaitingVideo", { code: sessionCode });
            return;
        }

        // A newly installed worker must control this page before media requests
        // begin, otherwise Chrome can bypass the chunk cache on first join.
        const cacheReady = await videoCacheReady;
        if (sequence !== stateApplySequence) return;
        if (!cacheReady) {
            video.pause();
            if (video.hasAttribute("src")) {
                video.removeAttribute("src");
                video.load();
            }
            setEmpty("waitingForVideo");
            setStatus("cacheUnavailable");
            return;
        }

        const networkSource = videoContentUrl(videoId);

        if (state?.playbackEnabled !== true) {
            video.pause();
            video.classList.add("preloading");
            resetJudgingReview();
            const preparing = state.mode === "preparing";
            // Safari can spend several seconds reloading MP4 metadata. Select
            // the eager policy before the first load and retain that resource
            // across ready -> preparing -> recording.
            video.preload = "auto";
            const sourceChanged = videoId !== currentVideoId || currentVideoSource !== networkSource;
            if (sourceChanged) {
                const sourceToPurge = currentVideoSource;
                currentVideoId = videoId;
                currentVideoSource = networkSource;
                assignedVideoSource = networkSource;
                resetTransferRate();
                video.src = networkSource;
                video.load();
                if (sourceToPurge && sourceToPurge !== networkSource)
                    purgeBrowserVideoCache(sourceToPurge).catch(() => { });
                cachedVideoSize(networkSource).catch(() => { });
            }
            if (preparing && video.readyState >= 1) {
                const target = normalizeToDuration(hostedPosition(state));
                if (Math.abs(Number(video.currentTime || 0) - target) > 0.05) {
                    try { video.currentTime = target; } catch { }
                }
            }
            reportVideoBuffer();
            appliedState = state;
            setEmpty("waitingForVideo");
            setStatus("preparingVideo", { code: sessionCode });
            return;
        }

        // Stop recording leaves completed chunks in place for replay.
        video.classList.remove("preloading");

        const videoChanged = videoId !== currentVideoId || networkSource !== currentVideoSource || assignedVideoSource !== networkSource;
        if (videoChanged) {
            video.pause();
            const sourceToPurge = currentVideoSource;
            currentVideoId = videoId;
            currentVideoSource = networkSource;
            resetTransferRate();
            video.src = networkSource;
            assignedVideoSource = networkSource;
            video.load();
            if (sourceToPurge && sourceToPurge !== networkSource)
                purgeBrowserVideoCache(sourceToPurge).catch(() => { });
            cachedVideoSize(networkSource).catch(() => { });
            await waitForVideoMetadata(sequence);
            if (sequence !== stateApplySequence) return;
        }
        if (video.readyState < 1) {
            await waitForVideoMetadata(sequence);
            if (sequence !== stateApplySequence) return;
        }
        reportVideoBuffer();

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
        const playbackRate = Math.max(0.05, Math.min(4, Number(state.playbackRate || 1)));
        if (Math.abs(video.playbackRate - playbackRate) > 0.001) video.playbackRate = playbackRate;
        const target = normalizeToDuration(hostedPosition(state));
        const discontinuityChanged =
            Number(state?.playbackDiscontinuity || 0) !== Number(previousState?.playbackDiscontinuity || 0);
        const resumed = !!state.isPlaying && !previousState?.isPlaying;
        const modeChanged = String(state?.mode || "") !== String(previousState?.mode || "");

        // Do not chase ordinary network drift during uninterrupted playback.
        // Periodic seeking or rate changes can skip frames; alignment is only
        // performed at an operator-visible transport boundary.
        const alignTransportBoundary = videoChanged || discontinuityChanged || resumed || modeChanged || !state.isPlaying;

        let seekPromise = Promise.resolve(true);
        if (alignTransportBoundary) {
            video.pause();
            seekPromise = seekExactly(target, sequence);
        }

        appliedState = state;
        if (state.isPlaying) {
            try {
                // Start the browser's play request while its seek is pending;
                // waiting for seeked first serialized two startup delays.
                const [, started] = await Promise.all([seekPromise, playWithAutoplayFallback()]);
                if (sequence !== stateApplySequence) return;
                if (!started) throw new Error("Playback was blocked.");
                drawTimeline();
                setStatusText(statusWithAutoplayNotice(playbackStatusText(state, true)));
            } catch {
                setStatus("adjustVolume", { code: sessionCode });
            }
        } else {
            await seekPromise;
            if (sequence !== stateApplySequence) return;
            video.pause();
            drawTimeline();
            setStatusText(playbackStatusText(state, false));
        }
    }

    async function connect(code) {
        code = normalizeCode(code);
        sessionCodeInput.value = code;
        if (!/^[A-Z0-9]{6}$/.test(code)) {
            showEnterRinkId();
            return;
        }

        const attempt = ++connectionAttempt;
        const previousSessionCode = sessionCode;
        const preserveLivePlayback = previousSessionCode === code && rinkMode === "Live" &&
            assignedVideoSource.includes("/live/");
        const sourceToPurge = previousSessionCode && previousSessionCode !== code
            ? currentVideoSource : "";
        closeConnection();
        if (!preserveLivePlayback) {
            clearLiveFrame();
            if (previousSessionCode !== code && liveEventId) void purgeLiveEventCache(liveEventId);
            stopLivePrefetch();
            liveHls?.destroy();
            liveHls = null;
            liveEventId = "";
            rinkMode = "Recorded";
            video.pause();
            if (sourceToPurge) {
                video.removeAttribute("src");
                video.load();
                if (!sourceToPurge.includes("/live/")) purgeBrowserVideoCache(sourceToPurge).catch(() => { });
            }
            currentVideoId = "";
            currentVideoSource = "";
            assignedVideoSource = "";
            latestState = null;
            appliedState = null;
            stateApplySequence++;
            resetJudgingReview();
            updateReviewIndicator(null);
            setEmpty("connectingEmpty");
        }
        sessionCode = code;
        setStatus("connecting", { code });
        history.replaceState(null, "", `?session=${encodeURIComponent(code)}`);

        try {
            const validation = await fetch(apiUrl(`sessions/${encodeURIComponent(code)}/validate`), { cache: "no-store" });
            if (attempt !== connectionAttempt) return;
            if (!validation.ok) {
                let payload = {};
                try { payload = await validation.json(); } catch { }
                if (attempt !== connectionAttempt) return;
                if (payload?.retryAfterSeconds || payload?.blocked) {
                    startRinkIdDelay(payload);
                } else {
                    showEnterRinkId();
                    setStatus("rinkNotCreated", { code });
                }
                return;
            }
            clearRinkIdDelay();
            const payload = await validation.json();
            rinkMode = payload?.mode === "Live" ? "Live" : "Recorded";
        } catch {
            if (attempt !== connectionAttempt) return;
            setStatus("serverNotReached");
            return;
        }

        openPlaybackEventStream(code, attempt);
    }

    refreshButton.addEventListener("click", () => {
        const rinkId = normalizeCode(sessionCodeInput.value);
        if (rinkId.length === 6) sessionStorage.setItem(REFRESH_RINK_ID_KEY, rinkId);
        else sessionStorage.removeItem(REFRESH_RINK_ID_KEY);
        window.location.reload();
    });

    video.addEventListener("playing", () => {
        clearLiveFrame();
        if (liveErrorTimer !== null) {
            clearTimeout(liveErrorTimer);
            liveErrorTimer = null;
        }
        if (rinkMode === "Live" && latestState?.mode !== "replay" &&
            assignedVideoSource.includes("/live/")) hideEmpty();
    });

    controls.addEventListener("submit", event => {
        event.preventDefault();
        connect(sessionCodeInput.value);
    });

    sessionCodeInput.addEventListener("input", () => {
        sessionCodeInput.value = normalizeCode(sessionCodeInput.value);
        if (sessionCodeInput.value.length === 6) connect(sessionCodeInput.value);
        else showEnterRinkId();
    });

    function updatePanelTypeLabel() {
        panelTypeLabel.textContent = t(panelTypeLabelKeys[panelType] || "technicalController");
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
        panelType = panelTypeLabelKeys[panelTypeInput.value] ? panelTypeInput.value : defaultPanelType;
        viewerSessionId = "";
        renderCommunicationStatus();
        sessionStorage.setItem(PANEL_SESSION_KEY, panelType);
        updatePanelTypeLabel();
        panelTypeMenu.querySelectorAll("[data-panel-type]").forEach(option => {
            option.setAttribute("aria-selected", option.dataset.panelType === panelType ? "true" : "false");
        });
        applyZoomState(latestState);
        resetJudgingReview();
        if (latestState) applyState(latestState).catch(() => { });
        if (eventSource && sessionCode) {
            eventSource.close();
            eventSource = null;
            openPlaybackEventStream(sessionCode, connectionAttempt);
        }
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

    window.addEventListener("wheel", event => {
        if (panelType !== "referee" || !judgingReviewActive || !isJudgingReview() ||
            video.readyState < 1 ||
            String(latestState?.mode || "").toLowerCase() !== "replay" ||
            event.ctrlKey || !Number.isFinite(event.deltaY) || event.deltaY === 0 ||
            (event.target instanceof Element &&
                event.target.closest("input, select, textarea, button, [contenteditable]"))) return;

        event.preventDefault();
        if (judgingIsPlaying) {
            judgingIsPlaying = false;
            updateJudgingControls();
        }
        judgingLastVideoTime = Number.NaN;
        video.pause();

        const fps = Math.max(1, Math.min(240, Number(latestState?.framesPerSecond) || 30));
        const sourceTime = judgingSourceStartSeconds + judgingPositionSeconds;
        const currentFrame = Math.floor(sourceTime * fps + 1e-6);
        // Match VRO: wheel up advances, wheel down rewinds one frame.
        const nextFrame = currentFrame + (event.deltaY < 0 ? 1 : -1);
        const nextPosition = nextFrame / fps - judgingSourceStartSeconds;
        seekJudgingPosition(nextPosition).catch(() => { });
        drawTimeline();
        setStatusText(judgingStatusText());
    }, { passive: false });

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
        syncVideoAudio();
        localStorage.setItem("ReVueRemoteVolume", String(video.volume));
        updateTestToneOutput();
        activateAudio(activateSound);
    }

    function setMuted(muted, activateSound = false) {
        muteInput.checked = !!muted;
        syncVideoAudio();
        if (activateSound) autoplayMutedByBrowser = false;
        localStorage.setItem("ReVueRemoteMuted", muted ? "true" : "false");
        updateTestToneOutput();
        activateAudio(activateSound);
        if (activateSound && judgingReviewActive && isJudgingReview()) {
            setStatusText(judgingStatusText());
        }
    }

    function activateAudio(activateSound) {
        if (!activateSound) return;

        if (judgingReviewActive && isJudgingReview()) {
            if (judgingIsPlaying && video.paused) video.play().catch(() => { });
        } else if (latestState?.isPlaying && video.paused) {
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
                })
                .catch(() => { });
        }
    }

    video.addEventListener("loadedmetadata", () => {
        reportVideoBuffer();
        if (latestState) applyState(latestState).catch(() => { });
        drawTimeline();
    });
    video.addEventListener("seeked", () => {
        reportVideoBuffer();
        if (rinkMode === "Live" && latestState?.mode === "replay" && video.readyState >= 2)
            clearLiveFrame();
    });
    video.addEventListener("loadstart", () => {
        if (!isStandbyAfterStop(latestState)) resetTransferRate();
    });
    video.addEventListener("error", () => {
        if (!currentVideoSource) return;
        if (rinkMode === "Live" && isRecordingState(latestState) &&
            assignedVideoSource.includes("/live/")) {
            // HLS can recover a short media error without losing the live
            // session. Do not flash the test pattern on every transient retry.
            if (liveErrorTimer !== null) clearTimeout(liveErrorTimer);
            liveErrorTimer = setTimeout(() => {
                liveErrorTimer = null;
                if (rinkMode === "Live" && isRecordingState(latestState) && video.readyState < 2)
                    setEmpty("waitingForVideo");
            }, 8000);
            return;
        }
        video.pause();
        setEmpty("waitingForVideo");
        setStatus("cacheUnavailable");
    });
    video.addEventListener("contextmenu", event => event.preventDefault());

    const savedVolume = Number(localStorage.getItem("ReVueRemoteVolume"));
    const initialVolume = Number.isFinite(savedVolume) ? Math.max(0, Math.min(1, savedVolume)) : 1;
    setVolume(initialVolume * 100, false);
    setMuted(localStorage.getItem("ReVueRemoteMuted") === "true", false);
    languageSelect.value = language;
    updateStaticTranslations();
    const savedPanelType = sessionStorage.getItem(PANEL_SESSION_KEY);
    panelType = panelTypeLabelKeys[savedPanelType] ? savedPanelType : defaultPanelType;
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
    function fitPageToViewport() {
        fitFrameRequest = 0;
        const bodyStyle = getComputedStyle(document.body);
        const horizontalPadding = parseFloat(bodyStyle.paddingLeft) + parseFloat(bodyStyle.paddingRight);
        const verticalPadding = parseFloat(bodyStyle.paddingTop) + parseFloat(bodyStyle.paddingBottom);
        const visualViewport = window.visualViewport;
        const useVisualViewport = visualViewport && Math.abs(visualViewport.scale - 1) < 0.01;
        const viewportWidth = useVisualViewport ? visualViewport.width : window.innerWidth;
        const viewportHeight = useVisualViewport ? visualViewport.height : window.innerHeight;
        const availableWidth = Math.max(1, viewportWidth - horizontalPadding - 4);
        const availableHeight = Math.max(1, viewportHeight - verticalPadding - 4);

        // Keep the responsive layout at its normal width, then scale the entire
        // page only when its full content would exceed the visible viewport.
        shell.style.width = `${Math.min(1400, availableWidth)}px`;
        const contentWidth = Math.max(shell.offsetWidth, shell.scrollWidth);
        const contentHeight = Math.max(shell.offsetHeight, shell.scrollHeight);
        const scale = Math.min(1, availableWidth / contentWidth, availableHeight / contentHeight);
        pageFitScale = scale;
        shell.style.transform = `scale(${scale})`;
        pageFrame.style.width = `${contentWidth * scale}px`;
        pageFrame.style.height = `${contentHeight * scale}px`;
        document.body.style.height = `${viewportHeight}px`;
        document.body.classList.add("fitViewport");
        drawTimeline();
    }

    function scheduleViewportFit() {
        if (!fitFrameRequest) fitFrameRequest = requestAnimationFrame(fitPageToViewport);
    }

    if (window.ResizeObserver) new ResizeObserver(scheduleViewportFit).observe(shell);
    scheduleViewportFit();
    window.addEventListener("load", scheduleViewportFit);
    window.addEventListener("resize", () => {
        scheduleViewportFit();
        applyZoomState(latestState);
        drawTimeline();
    });
    window.visualViewport?.addEventListener("resize", scheduleViewportFit);

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
    updateTransferRate().catch(() => { });
    window.setInterval(() => {
        probeServerConnectivity().catch(() => { });
    }, CONNECTIVITY_INTERVAL_MS);
    window.setInterval(() => {
        updateTransferRate().catch(() => { });
    }, TRANSFER_SAMPLE_INTERVAL_MS);
    window.setInterval(updateVroConnectionStatus, VRO_CONNECTION_INTERVAL_MS);
    window.setInterval(() => {
        cachedVideoSize(currentVideoSource).catch(() => { });
    }, VIDEO_CACHE_SAMPLE_INTERVAL_MS);
    window.setInterval(reportVideoBuffer, 100);

    const initialCode = normalizeCode(
        sessionStorage.getItem(REFRESH_RINK_ID_KEY) || new URLSearchParams(location.search).get("session")
    );
    sessionStorage.removeItem(REFRESH_RINK_ID_KEY);
    if (initialCode.length === 6) {
        sessionCodeInput.value = initialCode;
        connect(initialCode);
    }
})();
