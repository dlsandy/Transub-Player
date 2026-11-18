(() => {
  const params = new URLSearchParams(location.search);
  const inviteFromUrl = params.get("t") || "";
  let token = sessionStorage.getItem("lanSession") || "";

  const qs = (path) => {
    const u = new URL(path, location.origin);
    if (token) u.searchParams.set("t", token);
    return u.pathname + u.search;
  };

  const el = {
    status: document.getElementById("status"),
    gate: document.getElementById("gate"),
    gateForm: document.getElementById("gateForm"),
    gateError: document.getElementById("gateError"),
    password: document.getElementById("password"),
    browse: document.getElementById("browse"),
    player: document.getElementById("player"),
    list: document.getElementById("list"),
    crumbs: document.getElementById("crumbs"),
    empty: document.getElementById("empty"),
    video: document.getElementById("video"),
    sub: document.getElementById("sub"),
    title: document.getElementById("title"),
    job: document.getElementById("job"),
    back: document.getElementById("back"),
    compatBanner: document.getElementById("compatBanner"),
    subTrack: document.getElementById("subTrack"),
    zhTrack: document.getElementById("zhTrack"),
    waitOverlay: document.getElementById("waitOverlay"),
    waitTitle: document.getElementById("waitTitle"),
    waitDetail: document.getElementById("waitDetail"),
    waitHint: document.getElementById("waitHint"),
    waitSkip: document.getElementById("waitSkip"),
  };

  let rootIndex = 0;
  let relPath = "";
  let cues = [];
  let mode = "zh";
  let es = null;
  let activeMediaId = null;
  let seekTimer = null;
  let seekSentAt = -1;
  let ignoreSeekUntil = 0;
  let durationSec = 0;
  let subFrontier = 0;
  let zhFrontier = 0;
  let pendingZh = false;
  let waitingZh = false;

  async function api(path, opts) {
    const res = await fetch(qs(path), opts);
    if (res.status === 401) {
      token = "";
      sessionStorage.removeItem("lanSession");
      throw new Error("unauthorized");
    }
    if (!res.ok) {
      const text = await res.text().catch(() => "");
      throw new Error(text || res.statusText || String(res.status));
    }
    const ct = res.headers.get("content-type") || "";
    if (ct.includes("application/json")) return res.json();
    return res.text();
  }

  function setStatus(text) {
    el.status.textContent = text || "";
  }

  function showGate() {
    el.gate.classList.remove("hidden");
    el.browse.classList.add("hidden");
    el.player.classList.add("hidden");
    setStatus("需要访问密码");
    setTimeout(() => el.password && el.password.focus(), 50);
  }

  function hideGate() {
    el.gate.classList.add("hidden");
  }

  function renderModes(state) {
    mode = (state && state.mode) || mode;
    pendingZh = !!(state && state.pending);
    document.querySelectorAll(".mode").forEach((btn) => {
      const on = btn.dataset.mode === mode;
      btn.setAttribute("aria-pressed", on ? "true" : "false");
      btn.classList.toggle("pending", !!(on && pendingZh && (mode === "zh" || mode === "dual")));
    });
    applyWaitOverlay(state);
    updateJobLine(state && state.status);
  }

  function updateFrontier(state) {
    if (state) {
      if (typeof state.duration === "number" && state.duration > 1) durationSec = state.duration;
      if (typeof state.subFrontier === "number") subFrontier = state.subFrontier;
      if (typeof state.zhFrontier === "number") zhFrontier = state.zhFrontier;
      pendingZh = !!state.pending;
    }
    const dur = Math.max(durationSec, el.video.duration || 0, 1);
    const subPct = Math.max(0, Math.min(100, (subFrontier / dur) * 100));
    const zhPct = Math.max(0, Math.min(100, (zhFrontier / dur) * 100));
    el.subTrack.style.width = subPct.toFixed(2) + "%";
    el.zhTrack.style.width = zhPct.toFixed(2) + "%";
  }

  function updateJobLine(serverStatus) {
    if (waitingZh) {
      el.job.textContent = el.waitDetail.textContent || serverStatus || "";
      return;
    }
    if (pendingZh && (mode === "zh" || mode === "dual")) {
      el.job.textContent = "译文生成中…";
      return;
    }
    if (serverStatus) el.job.textContent = serverStatus;
  }

  function applyWaitOverlay(state) {
    const next = !!(state && state.waiting);
    const wasWaiting = waitingZh;
    waitingZh = next;
    if (!waitingZh) {
      el.waitOverlay.classList.add("hidden");
      if (wasWaiting) {
        try { el.video.play(); } catch (_) { /* autoplay may block */ }
      }
      return;
    }

    el.waitTitle.textContent = (state && state.waitTitle) || "正在生成译文字幕";
    el.waitDetail.textContent = (state && state.waitDetail) || "";
    el.waitHint.textContent = (state && state.waitHint) || "";
    el.waitSkip.textContent = (state && state.waitSkip) || "不等了，立即播放";
    el.waitOverlay.classList.remove("hidden");
    try { el.video.pause(); } catch (_) { /* ignore */ }
  }

  function activeCue(t) {
    for (let i = cues.length - 1; i >= 0; i--) {
      const c = cues[i];
      if (t >= c.start - 0.05 && t <= c.end + 0.12) return c;
    }
    return null;
  }

  function paintSub() {
    if (mode === "off") {
      el.sub.textContent = "";
      return;
    }
    const c = activeCue(el.video.currentTime || 0);
    el.sub.textContent = c && c.text ? c.text : "";
  }

  function showBrowse() {
    hideGate();
    el.browse.classList.remove("hidden");
    el.player.classList.add("hidden");
    if (es) { try { es.close(); } catch (_) {} es = null; }
    el.video.removeAttribute("src");
    el.video.load();
    el.sub.textContent = "";
    el.compatBanner.classList.add("hidden");
    applyWaitOverlay({ waiting: false });
    activeMediaId = null;
  }

  function showPlayer() {
    hideGate();
    el.browse.classList.add("hidden");
    el.player.classList.remove("hidden");
  }

  function renderCrumbs(parts) {
    el.crumbs.innerHTML = "";
    parts.forEach((p, i) => {
      const b = document.createElement("button");
      b.type = "button";
      b.className = "crumb";
      b.textContent = p.label;
      b.disabled = i === parts.length - 1;
      b.onclick = () => loadLibrary(p.root, p.path);
      el.crumbs.appendChild(b);
      if (i < parts.length - 1) {
        const sep = document.createElement("span");
        sep.textContent = "/";
        sep.style.color = "var(--muted)";
        el.crumbs.appendChild(sep);
      }
    });
  }

  async function login({ password, invite }) {
    const res = await fetch("/api/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ password: password || "", invite: invite || "" }),
    });
    if (!res.ok) {
      const text = await res.text().catch(() => "");
      throw new Error(text || "login failed");
    }
    const data = await res.json();
    token = data.token || "";
    if (!token) throw new Error("no token");
    sessionStorage.setItem("lanSession", token);
  }

  async function loadLibrary(root, path) {
    rootIndex = root;
    relPath = path || "";
    setStatus("加载目录…");
    try {
      const data = await api(`/api/library?root=${encodeURIComponent(root)}&path=${encodeURIComponent(relPath)}`);
      el.empty.classList.toggle("hidden", !!(data.items && data.items.length));
      el.list.innerHTML = "";
      const crumbs = [{ label: "片库", root: root, path: "" }];
      (data.crumbs || []).forEach((c) => crumbs.push({ label: c.name, root, path: c.path }));
      renderCrumbs(crumbs);

      if (data.roots && !relPath) {
        data.roots.forEach((r, idx) => {
          if (idx === root) return;
          const li = document.createElement("li");
          const btn = document.createElement("button");
          btn.type = "button";
          btn.innerHTML = `<strong>${escapeHtml(r.name)}</strong><span class="meta">切换根目录</span>`;
          btn.onclick = () => loadLibrary(idx, "");
          li.appendChild(btn);
          el.list.appendChild(li);
        });
      }

      (data.items || []).forEach((item) => {
        const li = document.createElement("li");
        const btn = document.createElement("button");
        btn.type = "button";
        if (item.type === "dir") {
          btn.innerHTML = `<strong>📁 ${escapeHtml(item.name)}</strong>`;
          btn.onclick = () => loadLibrary(root, item.path);
        } else {
          const warn = item.compat === "warn";
          const badge = warn
            ? `<span class="badge warn">${escapeHtml(item.compatHint || "手机可能无法播放")}</span>`
            : `<span class="badge ok">${escapeHtml(item.compatHint || "适合手机")}</span>`;
          btn.innerHTML = `<strong>${escapeHtml(item.name)}</strong><span class="meta">视频</span>${badge}`;
          btn.onclick = () => openMedia(item.id, item.name, item.compat, item.compatHint);
        }
        li.appendChild(btn);
        el.list.appendChild(li);
      });
      setStatus(data.status || "就绪");
    } catch (err) {
      if (String(err.message) === "unauthorized") {
        showGate();
        return;
      }
      setStatus("目录失败 · " + err.message);
      el.empty.classList.remove("hidden");
      el.list.innerHTML = "";
    }
  }

  async function openMedia(id, name, compat, compatHint) {
    if (compat === "warn") {
      const ok = window.confirm(
        (compatHint || "此格式手机浏览器可能无法播放") +
          "\n\n建议使用 MP4（H.264），或在电脑端打开。\n仍要尝试播放吗？"
      );
      if (!ok) return;
      el.compatBanner.textContent = compatHint || "此格式手机可能无法播放，可改用电脑端打开。";
      el.compatBanner.classList.remove("hidden");
    } else {
      el.compatBanner.classList.add("hidden");
    }

    activeMediaId = id;
    seekSentAt = -1;
    ignoreSeekUntil = Date.now() + 1600;
    durationSec = 0;
    subFrontier = 0;
    zhFrontier = 0;
    showPlayer();
    el.title.textContent = name || id;
    el.job.textContent = "正在连接桌面识别…";
    cues = [];
    paintSub();
    updateFrontier();
    el.video.src = qs("/media/" + encodeURIComponent(id));
    try {
      const state = await api("/api/session", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id }),
      });
      renderModes(state);
      updateFrontier(state);
      updateJobLine(state.status || "");
      connectEvents();
      const cuePack = await api("/api/cues");
      applyCues(cuePack);
    } catch (err) {
      el.job.textContent = "启动识别失败 · " + err.message;
    }
  }

  function applyCues(pack) {
    if (!pack) return;
    cues = pack.cues || [];
    if (pack.mode) mode = pack.mode;
    renderModes({ mode, pending: cues.some((c) => c.pending), status: el.job.textContent });
    paintSub();
  }

  function connectEvents() {
    if (es) { try { es.close(); } catch (_) {} }
    es = new EventSource(qs("/api/events"));
    es.onmessage = (ev) => {
      try {
        const msg = JSON.parse(ev.data);
        if (msg.type === "state") {
          renderModes(msg);
          updateFrontier(msg);
          setStatus(msg.status || "");
        } else if (msg.type === "cues") {
          applyCues(msg);
        }
      } catch (_) { /* ignore */ }
    };
  }

  function escapeHtml(s) {
    return String(s)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  document.querySelectorAll(".mode").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const next = btn.dataset.mode;
      try {
        const state = await api("/api/mode", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ mode: next }),
        });
        renderModes(state);
        updateFrontier(state);
        const cuePack = await api("/api/cues");
        applyCues(cuePack);
      } catch (err) {
        el.job.textContent = "切换失败 · " + err.message;
      }
    });
  });

  el.back.addEventListener("click", () => {
    showBrowse();
    loadLibrary(rootIndex, relPath);
  });

  el.video.addEventListener("timeupdate", paintSub);
  el.video.addEventListener("play", () => {
    if (waitingZh) {
      try { el.video.pause(); } catch (_) { /* ignore */ }
    }
  });
  el.waitSkip.addEventListener("click", async () => {
    try {
      const state = await api("/api/skip-wait", { method: "POST" });
      renderModes(state);
      updateFrontier(state);
      try { await el.video.play(); } catch (_) { /* ignore */ }
    } catch (err) {
      el.job.textContent = "跳过等待失败 · " + err.message;
    }
  });
  el.video.addEventListener("loadedmetadata", () => {
    if (el.video.duration && isFinite(el.video.duration) && el.video.duration > 1)
      durationSec = Math.max(durationSec, el.video.duration);
    updateFrontier();
    if (waitingZh) {
      try { el.video.pause(); } catch (_) { /* ignore */ }
    }
  });
  el.video.addEventListener("seeked", () => {
    paintSub();
    if (!activeMediaId) return;
    if (Date.now() < ignoreSeekUntil) return;
    const t = el.video.currentTime || 0;
    if (seekSentAt >= 0 && Math.abs(t - seekSentAt) < 2.5) return;
    clearTimeout(seekTimer);
    seekTimer = setTimeout(async () => {
      seekSentAt = t;
      try {
        await api("/api/seek", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ sec: t }),
        });
      } catch (_) { /* ignore */ }
    }, 700);
  });

  el.gateForm.addEventListener("submit", async (e) => {
    e.preventDefault();
    el.gateError.textContent = "";
    try {
      await login({ password: el.password.value || "" });
      hideGate();
      await enterApp();
    } catch (err) {
      el.gateError.textContent = "密码错误或无法连接";
    }
  });

  async function enterApp() {
    showBrowse();
    try {
      const boot = await api("/api/status");
      setStatus(boot.status || "就绪");
      if (!boot.roots || boot.roots.length === 0) {
        el.empty.classList.remove("hidden");
        return;
      }
      await loadLibrary(0, "");
    } catch (err) {
      if (String(err.message) === "unauthorized") {
        showGate();
        return;
      }
      setStatus("无法连接 · " + err.message);
      el.empty.classList.remove("hidden");
    }
  }

  (async () => {
    try {
      const boot = await fetch("/api/bootstrap").then((r) => r.json());
      if (boot.passwordRequired) {
        if (token) {
          try {
            await enterApp();
            return;
          } catch (_) {
            token = "";
            sessionStorage.removeItem("lanSession");
          }
        }
        showGate();
        return;
      }

      if (!token) {
        if (!inviteFromUrl) {
          setStatus("请扫描电脑端二维码打开");
          el.empty.classList.remove("hidden");
          el.browse.classList.remove("hidden");
          return;
        }
        await login({ invite: inviteFromUrl });
      }
      await enterApp();
    } catch (err) {
      setStatus("无法连接 · " + err.message);
      el.empty.classList.remove("hidden");
      el.browse.classList.remove("hidden");
    }
  })();
})();
