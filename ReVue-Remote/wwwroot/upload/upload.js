(() => {
    const $ = id => document.getElementById(id);
    let me = null;
    let keys = [];
    let currentVideos = [];
    let draggedVideoId = "";
    let videoLoadSequence = 0;
    const uploads = new Map();

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

    function show(view) {
        for (const id of ["videos", "keys", "profile", "users"])
            $(`${id}View`).classList.toggle("hidden", id !== view);
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

    async function loadKeys() {
        const previousCode = $("videoKey").value;
        keys = await api("/api/manage/keys");
        $("videoKey").innerHTML = keys.map(key =>
            `<option value="${esc(key.code)}">${esc(key.code)}</option>`).join("");
        if (keys.some(key => key.code === previousCode)) $("videoKey").value = previousCode;
        $("keyList").innerHTML = keys.map(key => {
            const created = new Date(key.createdAtUtc).toLocaleString();
            const detail = me.role === "Admin"
                ? `Created by ${esc(key.createdByEmail)}, ${created}`
                : `Created ${created}`;
            return `<div class="row"><div><b>${esc(key.code)}</b><div class="muted">${detail}</div></div>` +
            `<div class="rowActions"><button class="danger small" data-delete-key="${esc(key.code)}">Delete Rink ID</button></div></div>`
        }).join("") || `<p>${me.role === "Admin" ? "No Rink IDs have been created." : "You have not created any Rink IDs."}</p>`;
        await loadVideos();
    }

    function renderVideos() {
        $("deleteAllVideos").disabled = currentVideos.length === 0 || !$("videoKey").value;
        $("videoList").innerHTML = currentVideos.map(video =>
            `<div class="row videoRow" draggable="true" data-video-id="${esc(video.id)}">` +
            `<span class="dragHandle" title="Drag to reorder" aria-hidden="true">⋮⋮</span>` +
            `<div><b>${esc(video.fileName)}</b><div class="muted">${(video.sizeBytes / 1048576).toFixed(1)} MB · ${new Date(video.uploadedAtUtc).toLocaleString()}</div></div>` +
            `<div class="rowActions"><button class="small" data-rename-video="${esc(video.id)}">Rename</button>` +
            `<button class="danger small" data-delete-video="${esc(video.id)}">Delete</button></div></div>`
        ).join("") || "<p>No videos uploaded for this Rink ID.</p>";
    }

    async function loadVideos() {
        const sequence = ++videoLoadSequence;
        const code = $("videoKey").value;
        if (!code) {
            currentVideos = [];
            renderVideos();
            $("videoList").innerHTML = "<p>Create a Rink ID before uploading videos.</p>";
            return;
        }
        const videos = await api(`/api/sessions/${encodeURIComponent(code)}/videos`);
        if (sequence !== videoLoadSequence || code !== $("videoKey").value) return;
        currentVideos = videos;
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

    function delay(milliseconds) {
        return new Promise(resolve => window.setTimeout(resolve, milliseconds));
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

    async function createUpload(file, code) {
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
        row.innerHTML = `<div class="uploadName">${esc(file.name)}</div><progress max="100" value="0"></progress><span class="uploadStatus">Preparing…</span><button class="danger small">Cancel</button>`;
        $("uploadProgressList").appendChild(row);

        const progress = row.querySelector("progress");
        const status = row.querySelector("span");
        const job = {
            id, fingerprint, file, code, row, progress, status,
            uploadId: "", xhrs: new Set(), cancelled: false, paused: false, samples: []
        };
        uploads.set(id, job);

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

        try {
            const started = await api(`/api/sessions/${encodeURIComponent(code)}/uploads`, {
                method: "POST",
                body: JSON.stringify({
                    fileName: file.name,
                    sizeBytes: file.size,
                    lastModifiedUnixMs: file.lastModified
                })
            });
            if (job.cancelled) return;
            job.uploadId = started.uploadId;
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

            updateProgress(file.size, "Finalizing…");
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
            uploads.delete(id);
            progress.value = 100;
            status.textContent = "Complete";
            row.classList.add("uploadComplete");
            if ($("videoKey").value === code) await loadVideos();
            msg(`${file.name} upload complete.`, true);
            window.setTimeout(() => row.remove(), 3500);
        } catch (error) {
            if (job.cancelled) return;
            job.paused = true;
            for (const xhr of [...job.xhrs]) xhr.abort();
            row.classList.add("uploadFailed");
            status.textContent = "Paused—select the same file to resume";
            msg(`${file.name} upload paused: ${error.message}`);
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

    $("keyForm").onsubmit = async event => {
        event.preventDefault();
        try {
            await api("/api/manage/keys", { method: "POST", body: JSON.stringify({ code: $("newKey").value.toUpperCase() }) });
            $("newKey").value = "";
            await loadKeys();
            msg("Rink ID created.", true);
        } catch (error) { msg(error.message); }
    };

    $("keyList").onclick = async event => {
        const code = event.target.dataset.deleteKey;
        if (!code || !confirm(`Delete Rink ID ${code}? All videos stored under this Rink ID will be permanently deleted.`)) return;
        try {
            await cancelUploadsFor(code);
            await api(`/api/manage/keys/${encodeURIComponent(code)}`, { method: "DELETE" });
            await loadKeys();
            msg(`Rink ID ${code} and all of its videos were deleted.`, true);
        } catch (error) { msg(error.message); }
    };

    $("videoKey").onchange = loadVideos;
    $("uploadBtn").onclick = () => {
        const files = [...$("videoFiles").files];
        const code = $("videoKey").value;
        if (!code || files.length === 0) return msg("Choose a Rink ID and at least one MP4 video.");
        for (const file of files) void createUpload(file, code);
        $("videoFiles").value = "";
    };

    $("deleteAllVideos").onclick = async () => {
        const code = $("videoKey").value;
        if (!code || currentVideos.length === 0 || !confirm(`Delete all videos stored under Rink ID ${code}? This cannot be undone.`)) return;
        try {
            await cancelUploadsFor(code);
            await api(`/api/sessions/${encodeURIComponent(code)}/videos`, { method: "DELETE" });
            await loadVideos();
            msg(`All videos under Rink ID ${code} were deleted.`, true);
        } catch (error) { msg(error.message); }
    };

    $("videoList").onclick = async event => {
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
    start();
})();
