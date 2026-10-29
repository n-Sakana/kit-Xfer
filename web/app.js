(function () {
  "use strict";

  var state = {
    status: null,
    folders: {
      optical: null,
    },
    outputs: {
    },
    folderSignatures: {
      optical: null,
    },
    remotes: [],
    remoteId: null,
    remoteHeartbeatTimer: null,
    remoteFolderTimer: null,
    localRemoteTimer: null,
    role: "loading",
    activePage: null,
    opticalView: "send",
    opticalBusy: false,
    opticalStartGeneration: 0,
    screenWakeLock: null,
    wakeLockPending: false,
    opticalSession: null,
    opticalRenderer: null,
    qrDisplaySize: 900,
    cameraStream: null,
    cameraWorkers: [],
    cameraGeneration: 0,
    cameraStarting: false,
    cameraRunning: false,
    cameraComplete: false,
    cameraHasDecoded: false,
    cameraUpload: null,
    cameraTracking: null,
    cameraLastResultId: -1,
    cameraWorkerReason: null,
    cameraLastCaptureAt: 0,
    cameraLastDecodedAt: 0,
    cameraFrameId: 0,
    cameraQuietTimer: null,
    cameraReceiverId: null,
    themePreference: null,
    selections: {
      optical: new Set(),
    }
  };

  var diag = window.XferDiagnostics;
  var opticalCore = window.XferOptical;
  document.addEventListener("click", function (event) {
    var control = event.target.closest && event.target.closest("button, [role=switch], .file-row");
    if (!control) return;
    var name = /^(showQr|closeQr|readLocalCamera|receiveOpenFolder|pickFiles|pickFolder|selectAll|cameraNoSignalHelp|cameraHelpClose)$/.test(control.id)
      ? control.id : control.closest("#opticalDirection") ? "direction" : control.classList.contains("file-row") ? "selection" : "control";
    diag.sample("ui.action", name, 0);
  });
  document.addEventListener("change", function (event) {
    var name = event.target.id;
    if (!/^(frameAmount|frameRate|errorCorrection|cameraWidth|cameraFps|cameraWorkers)$/.test(name)) return;
    var value = event.target.value;
    diag.event("ui.setting", name, { value: /^[LMQH]$/.test(value) ? value : Number(value) || 0 });
  });
  diag.observe(function () {
    if (state.opticalRenderer) {
      var r = state.opticalRenderer;
      diag.event("send.status", r.session.transferId, { drawn: r.drawn, queued: r.queue.length, pending: r.pendingBatches,
        nextSequence: r.nextSequence, width: document.getElementById("qrFrame").width });
    }
    if (state.cameraStarting || state.cameraRunning) {
      var now = performance.now(), q = state.cameraUpload;
      diag.event("camera.status", diag.id(state.cameraReceiverId), { starting: state.cameraStarting,
        running: state.cameraRunning, captureAgeMs: state.cameraLastCaptureAt ? now - state.cameraLastCaptureAt : null,
        readAgeMs: state.cameraLastDecodedAt ? now - state.cameraLastDecodedAt : null,
        frames: state.cameraFrameId, queue: q ? q.queue.length : 0, inFlight: !!(q && q.inFlight), retries: q ? q.failures : 0,
        workers: state.cameraWorkers.map(function (s) { return { ready: s.ready, failed: s.failed, busy: s.busy,
          ageMs: s.busy && s.sentAt ? now - s.sentAt : 0 }; }) });
    }
  });
  var toast = document.getElementById("toast");
  var toastTimer = null;
  var cameraCanvas = document.createElement("canvas");
  var darkModeQuery = typeof window.matchMedia === "function"
    ? window.matchMedia("(prefers-color-scheme: dark)")
    : null;

  loadThemePreference();
  wireNavigation();
  wireControls();
  wireOverlayScrollbars();
  registerServiceWorker();
  watchCameraChanges();
  setPickerDisabled(true);
  setListMessage("opticalFiles", "Loading", "loading-state");
  loadInitialState();

  async function loadInitialState() {
    try {
      state.status = await requestJson("/api/status");
      applyStatus(state.status);
    } catch (error) {
      applyStatus(null);
      showToast(errorMessage(error));
    }

    if (state.role === "local") {
      pollConnectedRemotes();
      loadRemoteEntry();
    }

    var modes = ["optical"];
    var results = await Promise.allSettled(modes.map(function (mode) {
      return requestJson("/api/folder?mode=" + mode);
    }));
    modes.forEach(function (mode, index) {
      var result = results[index];
      if (result.status === "fulfilled") {
        applyFolder(mode, result.value);
        return;
      }
      var message = errorMessage(result.reason);
      setListMessage(mode + "Files", message, "empty-state");
      showToast(message);
    });

    var hashPage = window.location.hash.replace(/^#/, "");
    var requestedPage = isPage(hashPage) ? hashPage : state.status && isPage(state.status.initialMode) ? state.status.initialMode : null;
    navigate(requestedPage || "optical", false);
    // Do not accept remote commands before initial folder loading can overwrite them.
    if (state.role === "remote") startRemoteConnection();
  }

  function wireNavigation() {
    document.querySelectorAll("[data-page]").forEach(function (button) {
      button.addEventListener("click", function () {
        navigate(button.dataset.page, true);
      });
    });

    window.addEventListener("hashchange", function () {
      var page = window.location.hash.replace(/^#/, "");
      if (isPage(page) && page !== state.activePage) {
        navigate(page, false);
      }
    });

    document.querySelectorAll("#opticalDirection button").forEach(function (button) {
      button.addEventListener("click", function () {
        setOpticalView(button.dataset.view);
      });
    });
  }

  function wireControls() {
    document.querySelectorAll(".segmented:not(#opticalDirection)").forEach(function (group) {
      group.querySelectorAll("button").forEach(function (button) {
        button.addEventListener("click", function () {
          group.querySelectorAll("button").forEach(function (candidate) {
            var active = candidate === button;
            candidate.classList.toggle("is-active", active);
            candidate.setAttribute("aria-pressed", active ? "true" : "false");
          });
        });
      });
    });

    document.querySelectorAll(".switch").forEach(function (button) {
      button.addEventListener("click", function () {
        var on = button.getAttribute("aria-checked") !== "true";
        button.setAttribute("aria-checked", on ? "true" : "false");
        button.classList.toggle("is-on", on);
      });
    });

    document.querySelectorAll(".choose-folder").forEach(function (button) {
      button.addEventListener("click", function () {
        chooseFolder(button.dataset.pickerMode);
      });
    });

    document.querySelectorAll(".choose-files").forEach(function (button) {
      button.addEventListener("click", function () {
        chooseFiles(button.dataset.pickerMode);
      });
    });

    document.getElementById("selectAll").addEventListener("click", function () {
      var files = selectableFiles("optical");
      var selected = files.filter(function (file) { return state.selections.optical.has(file.name); }).length;
      setAllSelections("optical", selected < files.length);
    });

    document.querySelectorAll("[data-theme-toggle]").forEach(function (button) {
      button.addEventListener("click", toggleTheme);
    });

    document.getElementById("showQr").addEventListener("click", startOptical);
    document.getElementById("readLocalCamera").addEventListener("click", toggleCamera);
    document.getElementById("showRemoteQr").addEventListener("click", function () {
      sendRemoteCommand("showQr", this);
    });
    document.getElementById("readRemoteCamera").addEventListener("click", function () {
      sendRemoteCommand("readCamera", this);
    });
    document.getElementById("remoteDeviceButton").addEventListener("click", toggleDeviceMenu);
    document.getElementById("closeQr").addEventListener("click", stopOptical);
    document.getElementById("smallerQr").addEventListener("click", function () {
      changeQrSize(-1);
    });
    document.getElementById("largerQr").addEventListener("click", function () {
      changeQrSize(1);
    });
    document.getElementById("qrSmallerSetting").addEventListener("click", function () {
      changeQrSize(-1);
    });
    document.getElementById("qrLargerSetting").addEventListener("click", function () {
      changeQrSize(1);
    });
    document.getElementById("confirmOk").addEventListener("click", function () {
      document.getElementById("confirmDialog").close("ok");
    });
    document.getElementById("confirmCancel").addEventListener("click", function () {
      document.getElementById("confirmDialog").close("cancel");
    });
    ["cameraWidth", "cameraFps", "cameraWorkers"].forEach(function (id) {
      document.getElementById(id).addEventListener("change", function () {
        applyReceiveSettings();
      });
    });
    document.getElementById("cameraNoSignalHelp").addEventListener("click", function () {
      document.getElementById("cameraHelpDialog").showModal();
    });
    document.getElementById("cameraHelpClose").addEventListener("click", function () {
      document.getElementById("cameraHelpDialog").close();
    });
    document.getElementById("cameraHelpDialog").addEventListener("close", dismissCameraQuietHint);
    document.getElementById("cameraHelpDialog").addEventListener("click", function (event) {
      if (event.target === this) {
        this.close();
      }
    });
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && !document.getElementById("qrOverlay").hidden) {
        stopOptical();
      } else if (event.key === "Escape") {
        closeDeviceMenu();
      }
    });
    document.addEventListener("click", function (event) {
      var picker = document.querySelector(".remote-picker");
      if (picker && !picker.contains(event.target)) {
        closeDeviceMenu();
      }
    });
    window.addEventListener("pagehide", disconnectRemote);
    window.addEventListener("pagehide", shutdownCamera);
    window.addEventListener("pagehide", function () { stopOptical(true); });
    document.addEventListener("visibilitychange", function () {
      if (document.visibilityState === "visible" && (state.opticalSession || state.cameraRunning)) requestScreenWakeLock();
    });
    document.getElementById("errorCorrection").addEventListener("change", updateFrameAmountOptions);
    window.addEventListener("resize", function () {
      if (state.opticalRenderer) {
        sizeQrCanvas(state.opticalRenderer);
      }
      renderFolderPath("optical");
    });
    applyQrSize();
  }

  // A long path keeps its start and its last folders, so the chosen folder stays readable (as on the PC).
  function shortPath(path, element) {
    if (!path) return "";
    var room = Math.max(20, Math.floor(element.clientWidth / 7.4));
    var weight = function (value) { var n = 0; for (var i = 0; i < value.length; i++) n += value.charCodeAt(i) > 0x2e80 ? 2 : 1; return n; };
    if (weight(path) <= room) return path;
    var sep = path.indexOf("\\") >= 0 ? "\\" : "/";
    var parts = path.split(sep), head = parts[0], tail = [];
    for (var i = parts.length - 1; i > 0; i--) {
      var next = [parts[i]].concat(tail);
      if (tail.length > 0 && weight(head + sep + "…" + sep + next.join(sep)) > room) break;
      tail = next;
    }
    return head + sep + "…" + sep + tail.join(sep);
  }

  function renderFolderPath(mode) {
    var folder = state.folders[mode];
    var path = folder && folder.path ? folder.path : "";
    document.querySelectorAll("[data-folder-path='" + mode + "']").forEach(function (element) {
      element.textContent = shortPath(path, element);
      element.title = path;
    });
  }

  // Scrollbars as in the desktop window: shown while the page or the file list scrolls, or while the pointer is at
  // the right edge, and draggable; nothing is drawn otherwise. Phones already draw their scrollbars this way.
  function wireOverlayScrollbars() {
    if (typeof window.matchMedia !== "function" || !window.matchMedia("(pointer: fine)").matches) {
      return;
    }
    document.documentElement.classList.add("overlay-scrollbars");
    overlayScrollbar(document.scrollingElement || document.documentElement, null);
    overlayScrollbar(document.getElementById("opticalFiles"), document.querySelector(".table"));
  }

  function overlayScrollbar(scroller, box) {
    var page = box === null;
    var thumb = document.createElement("div");
    thumb.className = "scroll-thumb" + (page ? " is-page" : "");
    thumb.setAttribute("aria-hidden", "true");
    (page ? document.body : box).appendChild(thumb);
    var hideTimer = 0;
    var hot = false;
    var drag = null;

    // The strip the thumb moves in, in the thumb's own coordinates.
    function track() {
      return page
        ? { top: 2, height: window.innerHeight - 4 }
        : { top: scroller.offsetTop + 2, height: scroller.clientHeight - 4 };
    }
    function place() {
      var range = scroller.scrollHeight - scroller.clientHeight;
      if (range <= 1) {
        thumb.classList.remove("is-visible");
        return false;
      }
      var strip = track();
      var length = Math.max(24, strip.height * scroller.clientHeight / scroller.scrollHeight);
      thumb.style.height = length + "px";
      thumb.style.top = (strip.top + (strip.height - length) * scroller.scrollTop / range) + "px";
      return true;
    }
    function show() {
      window.clearTimeout(hideTimer);
      if (!place()) {
        return;
      }
      thumb.classList.add("is-visible");
      if (!hot && !drag) {
        hideTimer = window.setTimeout(function () { thumb.classList.remove("is-visible"); }, 900);
      }
    }
    (page ? window : scroller).addEventListener("scroll", show, { passive: true });
    document.addEventListener("pointermove", function (event) {
      if (drag) {
        return;
      }
      var bounds = page
        ? { right: window.innerWidth, top: 0, bottom: window.innerHeight }
        : scroller.getBoundingClientRect();
      var near = event.clientX >= bounds.right - 14 && event.clientX <= bounds.right &&
        event.clientY >= bounds.top && event.clientY <= bounds.bottom;
      if (near !== hot) {
        hot = near;
        thumb.classList.toggle("is-hot", hot);
        show();
      }
    });
    thumb.addEventListener("pointerdown", function (event) {
      drag = { y: event.clientY, scroll: scroller.scrollTop };
      thumb.setPointerCapture(event.pointerId);
      thumb.classList.add("is-hot");
      event.preventDefault();
    });
    thumb.addEventListener("pointermove", function (event) {
      if (!drag) {
        return;
      }
      var strip = track();
      var range = scroller.scrollHeight - scroller.clientHeight;
      var travel = Math.max(1, strip.height - thumb.getBoundingClientRect().height);
      scroller.scrollTop = drag.scroll + (event.clientY - drag.y) * range / travel;
    });
    function endDrag() {
      if (!drag) {
        return;
      }
      drag = null;
      thumb.classList.toggle("is-hot", hot);
      show();
    }
    thumb.addEventListener("pointerup", endDrag);
    thumb.addEventListener("pointercancel", endDrag);
  }

  function loadThemePreference() {
    var preference = null;
    try {
      var stored = window.localStorage.getItem("xfer-theme");
      if (stored === "light" || stored === "dark") {
        preference = stored;
      }
    } catch (_) {
      // The toggle still works for this session when storage is unavailable.
    }
    applyThemePreference(preference, false);
    if (darkModeQuery) {
      var updateFromSystem = function () {
        if (!state.themePreference) {
          updateThemeToggle();
        }
      };
      if (typeof darkModeQuery.addEventListener === "function") {
        darkModeQuery.addEventListener("change", updateFromSystem);
      } else if (typeof darkModeQuery.addListener === "function") {
        darkModeQuery.addListener(updateFromSystem);
      }
    }
  }

  function toggleTheme() {
    applyThemePreference(isDarkThemeActive() ? "light" : "dark", true);
  }

  function applyThemePreference(preference, persist) {
    state.themePreference = preference;
    if (preference) {
      document.documentElement.dataset.theme = preference;
    } else {
      document.documentElement.removeAttribute("data-theme");
    }
    if (persist) {
      try {
        window.localStorage.setItem("xfer-theme", preference);
      } catch (_) {
        // Keep the selected theme for this session when storage is unavailable.
      }
    }
    updateThemeToggle();
  }

  function isDarkThemeActive() {
    if (state.themePreference) {
      return state.themePreference === "dark";
    }
    return Boolean(darkModeQuery && darkModeQuery.matches);
  }

  function updateThemeToggle() {
    var dark = isDarkThemeActive();
    document.querySelectorAll("[data-theme-toggle]").forEach(function (button) {
      button.setAttribute("aria-pressed", dark ? "true" : "false");
      button.setAttribute("aria-label", dark ? "Use light theme" : "Use dark theme");
    });
  }

  function navigate(page, moveFocus) {
    if (state.role === "remote") {
      page = "optical";
    }

    if (!isPage(page)) {
      return;
    }

    if (page !== "optical" && (state.cameraRunning || state.cameraStarting)) {
      stopCamera(true, false);
      resetReceivePanel();
    }

    if (page !== "optical" && (state.opticalSession || state.opticalBusy)) stopOptical();
    state.activePage = page;
    document.querySelectorAll("[data-page-panel]").forEach(function (panel) {
      panel.hidden = panel.dataset.pagePanel !== page;
    });

    document.querySelectorAll("[data-page]").forEach(function (button) {
      var active = button.dataset.page === page;
      button.classList.toggle("is-active", active);
      if (active) {
        button.setAttribute("aria-current", "page");
      } else {
        button.removeAttribute("aria-current");
      }
    });

    if (window.location.hash !== "#" + page) {
      window.history.replaceState(null, "", "#" + page);
    }

    document.title = "Xfer";
    if (moveFocus) {
      document.getElementById("workspace").focus({ preventScroll: true });
      document.getElementById("workspace").scrollTo({ top: 0, behavior: "smooth" });
    }
  }

  function setOpticalView(view) {
    if (view !== "send" && view !== "receive") {
      return;
    }

    if (view === "send" && (state.cameraRunning || state.cameraStarting)) {
      stopCamera(true, false);
      resetReceivePanel();
    }

    state.opticalView = view;
    document.getElementById("appShell").dataset.view = view;
    document.querySelectorAll("#opticalDirection button").forEach(function (button) {
      var active = button.dataset.view === view;
      button.classList.toggle("is-active", active);
      button.setAttribute("aria-pressed", active ? "true" : "false");
    });
    document.querySelectorAll("[data-optical-view]").forEach(function (panel) {
      panel.hidden = panel.dataset.opticalView !== view;
    });
  }

  function isPage(value) {
    return value === "optical";
  }

  function applyStatus(status) {
    var device = status && status.device ? status.device : "This PC";
    var browserHost = window.location.hostname.toLowerCase();
    var fallbackRole = browserHost === "localhost" || browserHost === "127.0.0.1" ? "local" : "remote";
    var role = status && (status.role === "local" || status.role === "remote") ? status.role : fallbackRole;
    state.role = role;
    document.documentElement.classList.toggle("role-remote", role === "remote");
    document.documentElement.classList.toggle("role-local", role === "local");
    document.getElementById("appShell").dataset.role = role;
    document.getElementById("remoteDeviceName").textContent = device;

    refreshCameraStatus();
    setPickerDisabled(!status || role !== "local");
    updateOpticalAction();
  }

  // Recount devices on devicechange so camera availability follows hotplug events.
  function refreshCameraStatus() {
    var api = Boolean(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
    if (!api) {
      setCapability("statusCamera", "Unavailable", false);
      return;
    }
    if (!navigator.mediaDevices.enumerateDevices) {
      setCapability("statusCamera", "Available", true);
      return;
    }
    navigator.mediaDevices.enumerateDevices().then(function (devices) {
      var count = 0;
      for (var i = 0; i < devices.length; i++) {
        if (devices[i].kind === "videoinput") { count++; }
      }
      // Device labels may be empty before permission is granted.
      setCapability("statusCamera", count > 0 ? (count + " devices") : "None", count > 0);
      updateOpticalAction();
    }).catch(function () {
      setCapability("statusCamera", "Available", true);
    });
  }

  function watchCameraChanges() {
    if (!navigator.mediaDevices || !navigator.mediaDevices.addEventListener) { return; }
    navigator.mediaDevices.addEventListener("devicechange", function () {
      refreshCameraStatus();
    });
  }

  // The camera count is not shown; it only decides whether reading with this device's camera is offered.
  function setCapability(id, text, positive) {
    state.cameraAvailable = positive;
  }

  function applyFolder(mode, snapshot, preserveTransfer) {
    if (mode === "optical" && !preserveTransfer) {
      stopOptical();
    }
    var selectedSource = snapshot && snapshot.sourceKind !== "none" ? snapshot : null;
    state.folders[mode] = selectedSource;
    state.folderSignatures[mode] = folderSignature(snapshot);
    var files = selectedSource && Array.isArray(selectedSource.files) ? selectedSource.files : [];
    state.selections[mode] = new Set(files.filter(function (file) {
      return true;
    }).map(function (file) { return file.name; }));

    renderFolderPath(mode);


    renderFiles(mode);
  }

  function renderFiles(mode) {
    var files = selectableFiles(mode);
    renderSelectableFiles(mode + "Files", files, mode);
    updateSelectionActions(mode, files);
    updateSummary(mode);
  }

  function selectableFiles(mode) {
    var folder = state.folders[mode];
    var files = folder && Array.isArray(folder.files) ? folder.files : [];
    return files;
  }

  function renderSelectableFiles(containerId, files, mode) {
    var container = document.getElementById(containerId);
    container.replaceChildren();

    if (files.length === 0) {
      appendMessage(
        container,
        state.role === "remote" ? "Choose files or a folder on the PC" : "Choose files or a folder",
        "empty-state");
      return;
    }

    var fragment = document.createDocumentFragment();
    files.forEach(function (file) {
      var row = document.createElement("button");
      row.type = "button";
      row.className = "row file-row";
      row.dataset.fileName = file.name;
      row.title = file.name;
      row.setAttribute("role", "checkbox");
      row.setAttribute("aria-checked", state.selections[mode].has(file.name) ? "true" : "false");
      var check = document.createElement("span");
      check.className = "check-cell";
      check.setAttribute("aria-hidden", "true");
      check.appendChild(textCell("", "check"));
      row.appendChild(check);
      row.appendChild(textCell(file.name, "c-name"));
      row.appendChild(textCell(formatBytes(file.size), "c-size"));
      row.appendChild(textCell(formatDate(file.modifiedUtc), "c-date"));
      row.addEventListener("click", function () {
        toggleSelection(mode, file.name, row);
      });
      fragment.appendChild(row);
    });
    container.appendChild(fragment);
  }

  function textCell(text, className) {
    var cell = document.createElement("span");
    cell.className = className;
    cell.textContent = text;
    return cell;
  }

  function svgUse(symbol, className) {
    var svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", className);
    svg.setAttribute("aria-hidden", "true");
    var use = document.createElementNS("http://www.w3.org/2000/svg", "use");
    use.setAttribute("href", "#" + symbol);
    svg.appendChild(use);
    return svg;
  }

  function toggleSelection(mode, fileName, row) {
    var selected = state.selections[mode].has(fileName);
    if (selected) {
      state.selections[mode].delete(fileName);
    } else {
      state.selections[mode].add(fileName);
    }
    row.classList.toggle("is-selected", !selected);
    row.setAttribute("aria-checked", !selected ? "true" : "false");
    updateSelectionActions(mode, selectableFiles(mode));
    updateSummary(mode);
    if (mode === "optical") {
      updateOpticalAction();
    }
  }

  function setAllSelections(mode, select) {
    if (mode !== "optical") {
      return;
    }
    selectableFiles(mode).forEach(function (file) {
      if (select) {
        state.selections[mode].add(file.name);
      } else {
        state.selections[mode].delete(file.name);
      }
    });
    renderFiles(mode);

  }

  function updateSelectionActions(mode, files) {
    var all = document.getElementById("selectAll");
    var selectedCount = files.filter(function (file) {
      return state.selections[mode].has(file.name);
    }).length;
    all.disabled = files.length === 0;
    all.setAttribute("aria-checked", files.length > 0 && selectedCount === files.length
      ? "true"
      : selectedCount > 0 ? "mixed" : "false");
  }

  function updateSummary(mode) {
    var folder = state.folders[mode];
    var files = folder && Array.isArray(folder.files) ? folder.files : [];
    var selected = files.filter(function (file) {
      return state.selections[mode].has(file.name);
    });
    document.getElementById("sendSummary").textContent = files.length === 0
      ? ""
      : "Files: " + selected.length.toLocaleString("en-US") + " · " + formatBytes(selected.reduce(function (total, file) {
        return total + file.size;
      }, 0));
    if (mode === "optical") {
      updateOpticalAction();
    }
  }

  async function chooseFolder(mode) {
    if (!state.status || state.role === "remote" || !isPage(mode)) {
      return;
    }

    await openNativePicker(
      mode,
      "/api/pick-folder?mode=" + encodeURIComponent(mode),
      { method: "POST" });
  }

  async function chooseFiles(mode) {
    if (!state.status || state.role === "remote") {
      return;
    }

    await openNativePicker(
      mode,
      "/api/pick-files",
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ mode: mode })
      });
  }

  async function openNativePicker(mode, url, options) {
    setPickerDisabled(true);
    try {
      var result = await requestJson(url, options);
      if (!result || result.cancelled) {
        return;
      }
      if (!result.folder) {
        throw new Error("Could not load the selection.");
      }
      applyFolder(mode, result.folder);
    } catch (error) {
      showToast(errorMessage(error));
    } finally {
      setPickerDisabled(!state.status || state.role !== "local");
    }
  }

  function setPickerDisabled(disabled) {
    document.querySelectorAll(".choose-folder, .choose-files").forEach(function (button) {
      button.disabled = disabled;
    });
  }

  async function startOptical(command) {
    var fromRemoteCommand = command && command.action === "showQr";
    var files = fromRemoteCommand && Array.isArray(command.files)
      ? command.files
      : Array.from(state.selections.optical);
    if (state.opticalBusy || !state.status || files.length === 0) {
      return;
    }

    if (state.opticalSession) stopOptical();
    var startGeneration = ++state.opticalStartGeneration;
    setOpticalView("send");
    var button = document.getElementById("showQr");
    state.opticalBusy = true;
    updateOpticalAction();
    button.setAttribute("aria-busy", "true");
    try {
      var requestedFrameBytes = fromRemoteCommand
        ? Number(command.frameBytes)
        : Number(document.getElementById("frameAmount").value || "1000");
      var framesPerSecond = fromRemoteCommand
        ? Number(command.framesPerSecond)
        : Number(document.getElementById("frameRate").value || "30");
      var errorCorrection = fromRemoteCommand
        ? String(command.errorCorrection || "L")
        : String(document.getElementById("errorCorrection").value || "L");
      var displaySize = fromRemoteCommand
        ? Number(command.displaySize || 900)
        : Number(document.getElementById("qrDisplaySize").value || "900");
      setQrDisplaySize(displaySize);
      var result = await requestJson("/api/optical/start", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          files: files,
          format: fromRemoteCommand ? command.format : activeData("transferFormat", "transferFormat", "original"),
          frameBytes: requestedFrameBytes,
          framesPerSecond: framesPerSecond,
          errorCorrection: errorCorrection
        })
      });
      if (startGeneration !== state.opticalStartGeneration) {
        diag.fetch("/api/optical/stop", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ token: result.token })
        }).catch(function () {});
        return;
      }
      // Keep the selected budget after a tiny transfer; do not silently force
      // the next, larger transfer to use the tiny transfer's actual frame size.
      setActualFrameAmount(Math.max(result.frameBytes, Math.min(requestedFrameBytes,
        opticalCore.maximumFrameBytes(result.errorCorrection || errorCorrection))));
      document.getElementById("frameRate").value = String(result.framesPerSecond);
      document.getElementById("errorCorrection").value = String(result.errorCorrection || errorCorrection);
      updateFrameAmountOptions();
      if (!(await confirmLargeOptical(result))) {
        await requestJson("/api/optical/stop", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ token: result.token })
        });
        return;
      }
      state.opticalSession = result;
      requestScreenWakeLock();
      state.opticalPreviousFocus = document.activeElement;
      setQrStatus("Preparing QR codes", false);
      document.getElementById("qrOverlay").hidden = false;
      document.getElementById("closeQr").focus({ preventScroll: true });
      startQrRenderer(result);
    } catch (error) {
      showToast(errorMessage(error));
    } finally {
      state.opticalBusy = false;
      button.removeAttribute("aria-busy");
      updateOpticalAction();
    }
  }

  // Large transfers take a while: ask first, as the desktop screen does.
  function confirmLargeOptical(result) {
    var largeTransferBytes = 16 * 1024 * 1024;
    if (Number(result.originalBytes || 0) <= largeTransferBytes) {
      return Promise.resolve(true);
    }
    var dialog = document.getElementById("confirmDialog");
    document.getElementById("confirmText").textContent = formatBytes(result.originalBytes) +
      " total. Sending will take at least " + formatSeconds(result.minimumSeconds) + ".";
    dialog.returnValue = "";
    return new Promise(function (resolve) {
      dialog.addEventListener("close", function () { resolve(dialog.returnValue === "ok"); }, { once: true });
      dialog.showModal();
      document.getElementById("confirmOk").focus();
    });
  }

  function setQrStatus(text, alert) {
    var status = document.getElementById("qrProgress");
    status.textContent = text;
    status.classList.toggle("is-alert", alert);
  }

  function setActualFrameAmount(value) {
    var control = document.getElementById("frameAmount");
    control.querySelectorAll("[data-actual]").forEach(function (option) { option.remove(); });
    if (!Array.from(control.options).some(function (option) { return Number(option.value) === value; })) {
      var option = document.createElement("option");
      option.value = String(value);
      option.textContent = Number(value).toLocaleString("en-US") + " bytes (adjusted)";
      option.dataset.actual = "true";
      control.appendChild(option);
    }
    control.value = String(value);
  }

  function updateFrameAmountOptions() {
    var maximum = opticalCore.maximumFrameBytes(document.getElementById("errorCorrection").value);
    var control = document.getElementById("frameAmount");
    Array.from(control.options).forEach(function (option) { option.disabled = Number(option.value) > maximum; });
    if (Number(control.value) > maximum) {
      control.value = Array.from(control.options).filter(function (option) { return !option.disabled; })
        .reduce(function (value, option) { return Math.max(value, Number(option.value)); }, 0).toString();
    }
  }

  function startQrRenderer(session) {
    diag.event("send.display.start", session.transferId, { fps: session.framesPerSecond, frameBytes: session.frameBytes,
      blocks: session.sourceBlocks, qrVersion: session.qrVersion });
    var side = 17 + 4 * Number(session.qrVersion) + 8;
    var renderer = {
      session: session, side: side, nextSequence: 0, queue: [],
      staging: document.createElement("canvas"), interval: 1000 / session.framesPerSecond,
      nextAt: performance.now(), animationFrame: 0, stopped: false,
      batches: new Set(), pendingBatches: 0, drawn: 0, startedAt: 0, lastStatusAt: 0, lastImage: null
    };
    renderer.staging.width = side;
    renderer.staging.height = side;
    state.opticalRenderer = renderer;
    sizeQrCanvas(renderer);
    pumpQrFrames(renderer);
    var tick = function (now) {
      if (!isCurrentQrRenderer(renderer)) return;
      renderer.animationFrame = window.requestAnimationFrame(tick);
      if (now < renderer.nextAt) return;
      var entry = renderer.queue[0];
      if (!entry || !entry.image) { diag.sample("send.queue_empty", session.transferId, 0); return; } // keep the last complete QR visible
      renderer.queue.shift();
      pumpQrFrames(renderer);
      drawQrFrame(renderer, entry.image);
      if (!renderer.startedAt) renderer.startedAt = now;
      renderer.drawn++;
      diag.sample("send.draw", session.transferId, 0, { sequence: entry.sequence, lateMs: Math.max(0, now - renderer.nextAt) });
      if (renderer.drawn === 1 || renderer.retrying) {
        renderer.retrying = false;
        setQrStatus("Displaying QR codes", false);
      }
      renderer.nextAt += renderer.interval;
      // Do not rapidly flash several catch-up frames after a stalled request/tab.
      if (renderer.nextAt <= now) renderer.nextAt = now + renderer.interval;
    };
    renderer.animationFrame = window.requestAnimationFrame(tick);
  }

  function pumpQrFrames(renderer) {
    while (isCurrentQrRenderer(renderer) && renderer.pendingBatches < 2 && renderer.queue.length < 24) {
      var batch = { sequence: renderer.nextSequence, entries: [], failures: 0, retryTimer: null, controller: null };
      for (var index = 0; index < Math.min(8, 24 - renderer.queue.length); index++) {
        batch.entries.push({ sequence: batch.sequence + index, image: null });
      }
      renderer.nextSequence = batch.sequence + batch.entries.length;
      Array.prototype.push.apply(renderer.queue, batch.entries);
      renderer.pendingBatches++;
      renderer.batches.add(batch);
      fetchQrBatch(renderer, batch);
    }
  }

  async function fetchQrBatch(renderer, batch) {
    if (!isCurrentQrRenderer(renderer)) return;
    var diagnosticEnd = diag.begin("send.batch", renderer.session.transferId);
    var diagnosticOk = false;
    batch.controller = new AbortController();
    var timeout = window.setTimeout(function () { batch.controller.abort(); }, 10000);
    try {
      var response = await diag.fetch("/api/optical/frames?token=" + encodeURIComponent(renderer.session.token) +
        "&seq=" + batch.sequence + "&count=" + batch.entries.length,
        { cache: "no-store", signal: batch.controller.signal });
      if (!response.ok) {
        var httpError = new Error("QR frame HTTP " + response.status);
        httpError.status = response.status;
        throw httpError;
      }
      var buffer = await response.arrayBuffer();
      diagnosticOk = true;
      if (!isCurrentQrRenderer(renderer)) return;
      var frames = opticalCore.unpackFrames(buffer, renderer.side, batch.sequence, batch.entries.length);
      frames.forEach(function (frame, index) {
        batch.entries[index].image = new ImageData(frame.pixels, renderer.side, renderer.side);
      });
      renderer.batches.delete(batch);
      renderer.pendingBatches--;
      pumpQrFrames(renderer);
    } catch (error) {
      if (!isCurrentQrRenderer(renderer)) return;
      if (error.status >= 400 && error.status < 500 && error.status !== 408 && error.status !== 429) {
        showToast("The QR display stopped. Start sending again.");
        stopOptical();
        return;
      }
      batch.failures++;
      diag.sample("send.batch.retry", renderer.session.transferId, 0, { failures: batch.failures, status: error.status || 0 });
      renderer.retrying = true;
      setQrStatus("Waiting for the PC connection", true);
      batch.retryTimer = window.setTimeout(function () {
        batch.retryTimer = null;
        fetchQrBatch(renderer, batch);
      }, Math.min(3000, 150 * Math.pow(2, Math.min(batch.failures - 1, 5))));
    } finally {
      diagnosticEnd(response ? response.status : 0, !diagnosticOk);
      window.clearTimeout(timeout);
    }
  }

  function drawQrFrame(renderer, image) {
    renderer.lastImage = image;
    var stagingContext = renderer.staging.getContext("2d");
    stagingContext.putImageData(image, 0, 0);
    var canvas = document.getElementById("qrFrame");
    var context = canvas.getContext("2d");
    context.imageSmoothingEnabled = false;
    context.drawImage(renderer.staging, 0, 0, canvas.width, canvas.height);
  }

  function sizeQrCanvas(renderer) {
    if (!isCurrentQrRenderer(renderer)) {
      return;
    }
    var canvas = document.getElementById("qrFrame");
    var dpr = window.devicePixelRatio || 1;
    var overlay = document.getElementById("qrOverlay");
    var overlayStyle = getComputedStyle(overlay);
    var horizontalChrome = Number.parseFloat(overlayStyle.paddingLeft) +
      Number.parseFloat(overlayStyle.paddingRight) +
      Number.parseFloat(overlayStyle.borderLeftWidth) +
      Number.parseFloat(overlayStyle.borderRightWidth);
    var containerWidth = overlay.getBoundingClientRect().width || window.innerWidth;
    var verticalChrome = 56;
    overlay.querySelectorAll(".present-bar, .qr-status.is-alert").forEach(function (element) {
      if (getComputedStyle(element).display !== "none") verticalChrome += element.getBoundingClientRect().height + 16;
    });
    var viewportBudget = Math.max(1, Math.min(window.innerWidth - 36, window.innerHeight - verticalChrome));
    var containerBudget = Math.max(1, containerWidth - horizontalChrome);
    var cssBudget = Math.max(1, Math.min(
      viewportBudget,
      containerBudget,
      state.qrDisplaySize));
    var scale = Math.max(1, Math.floor((cssBudget * dpr) / renderer.side));
    var pixelSide = renderer.side * scale;
    if (canvas.width !== pixelSide || canvas.height !== pixelSide) {
      canvas.width = pixelSide;
      canvas.height = pixelSide;
    }
    canvas.style.width = (canvas.width / dpr) + "px";
    canvas.style.height = (canvas.height / dpr) + "px";
    if (renderer.lastImage) drawQrFrame(renderer, renderer.lastImage);
  }

  function isCurrentQrRenderer(renderer) {
    return !renderer.stopped && state.opticalRenderer === renderer &&
      state.opticalSession && state.opticalSession.token === renderer.session.token;
  }

  async function requestScreenWakeLock() {
    if (state.screenWakeLock || state.wakeLockPending || !navigator.wakeLock || document.visibilityState !== "visible") return;
    state.wakeLockPending = true;
    try {
      var lock = await navigator.wakeLock.request("screen");
      if (!state.opticalSession && !state.cameraRunning) { await lock.release(); return; }
      state.screenWakeLock = lock;
      lock.addEventListener("release", function () {
        if (state.screenWakeLock === lock) state.screenWakeLock = null;
      });
    } catch (_) {
      // Best effort: unsupported browsers and battery-saving mode may refuse it.
    } finally { state.wakeLockPending = false; }
  }

  function releaseScreenWakeLock() {
    if (state.opticalSession || state.cameraRunning) return;
    var lock = state.screenWakeLock;
    state.screenWakeLock = null;
    if (lock) lock.release().catch(function () {});
  }

  function stopOptical(unloading) {
    state.opticalStartGeneration++;
    var renderer = state.opticalRenderer;
    state.opticalRenderer = null;
    if (renderer) {
      diag.event("send.display.stop", renderer.session.transferId, { drawn: renderer.drawn, queued: renderer.queue.length });
      renderer.stopped = true;
      window.cancelAnimationFrame(renderer.animationFrame);
      renderer.batches.forEach(function (batch) {
        window.clearTimeout(batch.retryTimer);
        if (batch.controller) batch.controller.abort();
      });
      renderer.batches.clear();
      renderer.queue = [];
    }
    var session = state.opticalSession;
    state.opticalSession = null;
    var overlay = document.getElementById("qrOverlay");
    if (overlay) {
      overlay.hidden = true;
    }
    releaseScreenWakeLock();
    if (session && unloading === true && navigator.sendBeacon) {
      navigator.sendBeacon("/api/optical/stop", new Blob([JSON.stringify({ token: session.token })], { type: "application/json" }));
    } else if (session) {
      diag.fetch("/api/optical/stop", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token: session.token })
      }).catch(function () {});
    }
    if (state.opticalPreviousFocus && document.contains(state.opticalPreviousFocus)) {
      state.opticalPreviousFocus.focus({ preventScroll: true });
    }
    state.opticalPreviousFocus = null;
    updateOpticalAction();
  }

  async function toggleCamera() {
    if (state.cameraRunning || state.cameraStarting) {
      stopCamera(true, false);
      resetReceivePanel();
      return;
    }
    await startCamera();
  }

  async function startCamera() {
    if (state.cameraRunning || state.cameraStarting || !state.status) {
      return;
    }

    if (state.opticalSession || state.opticalBusy) {
      stopOptical();
    }
    setOpticalView("receive");
    resetReceivePanel();
    state.cameraStarting = true;
    state.cameraComplete = false;
    state.cameraHasDecoded = false;
    state.cameraTracking = null;
    state.cameraLastResultId = -1;
    state.cameraLastCaptureAt = 0;
    state.cameraLastDecodedAt = 0;
    state.cameraFrameId = 0;
    var generation = ++state.cameraGeneration;
    setCameraLabel("Starting camera");
    updateOpticalAction();

    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      finishCameraError(window.isSecureContext
        ? "This browser does not support camera access."
        : "Open this page over HTTPS to use the camera.");
      return;
    }

    diag.event("camera.request", "", { width: Number(document.getElementById("cameraWidth").value), fps: Number(document.getElementById("cameraFps").value) });
    var captureWidth = Number(document.getElementById("cameraWidth").value || "1280");
    var captureFps = Number(document.getElementById("cameraFps").value || "60");
    var stream = null;
    var constraints = {
      facingMode: "environment",
      width: { ideal: captureWidth },
      height: { ideal: Math.round(captureWidth * 3 / 4) }
    };
    try {
      try {
        stream = await navigator.mediaDevices.getUserMedia({
          audio: false,
          video: Object.assign({}, constraints, { frameRate: { exact: captureFps } })
        });
      } catch (error) {
        if (generation !== state.cameraGeneration) return;
        if (error.name !== "OverconstrainedError" && error.name !== "ConstraintNotSatisfiedError") throw error;
        stream = await navigator.mediaDevices.getUserMedia({
          audio: false,
          video: Object.assign({}, constraints, { frameRate: { ideal: captureFps } })
        });
      }

      if (generation !== state.cameraGeneration) {
        stream.getTracks().forEach(function (track) { track.stop(); });
        return;
      }

      // A unique ID prevents a delayed POST/stop from an earlier start erasing this run.
      state.cameraReceiverId = createRemoteId();
      var settings = stream.getVideoTracks()[0].getSettings();
      diag.event("camera.open", diag.id(state.cameraReceiverId), { width: settings.width, height: settings.height, fps: settings.frameRate });
      await requestJson("/api/optical/receive/start", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ receiverId: state.cameraReceiverId })
      });

      if (generation !== state.cameraGeneration) {
        stream.getTracks().forEach(function (track) { track.stop(); });
        return;
      }

      state.cameraStream = stream;
      var video = document.getElementById("cameraVideo");
      video.srcObject = stream;
      video.hidden = false;
      await video.play();
      if (generation !== state.cameraGeneration) {
        stream.getTracks().forEach(function (track) { track.stop(); });
        return;
      }
      var receiverId = state.cameraReceiverId;
      state.cameraUpload = new opticalCore.UploadQueue({
        send: function (frames, signal) {
          return requestJson("/api/optical/receive/frames", {
            method: "POST", headers: { "Content-Type": "application/json" }, signal: signal,
            body: JSON.stringify({ receiverId: receiverId, frames: frames.map(bytesToBase64),
              openWhenDone: document.getElementById("receiveOpenFolder").getAttribute("aria-checked") === "true" })
          }, 15000);
        },
        onProgress: function (result) {
          if (generation === state.cameraGeneration && state.cameraRunning) applyReceiveProgress(result);
        },
        onTransient: function () {
          diag.sample("relay.retry", diag.id(receiverId), 0);
          if (generation === state.cameraGeneration && state.cameraRunning)
            document.getElementById("receiveState").textContent = "Waiting for the PC connection";
        },
        onFatal: function (error) {
          diag.event("relay.failed", diag.id(receiverId), { status: error.status || 0 });
          if (generation === state.cameraGeneration) finishCameraError(errorMessage(error));
        }
      });
      diag.event("camera.running", diag.id(receiverId));
      startCameraWorkers(generation);
      state.cameraStarting = false;
      state.cameraRunning = true;
      requestScreenWakeLock();
      stream.getVideoTracks().forEach(function (track) {
        track.addEventListener("ended", function () {
          if (generation === state.cameraGeneration && state.cameraRunning)
            { diag.event("camera.track_ended", diag.id(state.cameraReceiverId)); finishCameraError("The camera was disconnected. Reconnect it and try again."); }
        });
      });
      document.getElementById("cameraPreview").classList.add("is-live");
      applyCameraExtras();
      document.getElementById("receiveState").textContent = "Waiting for QR codes";
      setCapability("statusCamera", "Available", true);
      updateOpticalAction();
      armCameraQuietHint(generation);
      scheduleCameraFrame(generation);
    } catch (error) {
      if (stream) {
        stream.getTracks().forEach(function (track) { track.stop(); });
      }
      if (generation === state.cameraGeneration) {
        diag.event("camera.open.failed", diag.id(state.cameraReceiverId), { type: safeErrorType(error) });
        finishCameraError(cameraErrorMessage(error));
      }
    }
  }

  async function applyCameraExtras() {
    var track = state.cameraStream && state.cameraStream.getVideoTracks()[0];
    if (!track || typeof track.getCapabilities !== "function") {
      return;
    }
    var capabilities;
    try {
      capabilities = track.getCapabilities();
    } catch (_) {
      return;
    }

    var focusModes = capabilities && capabilities.focusMode;
    if (Array.isArray(focusModes) && focusModes.indexOf("continuous") >= 0) {
      try {
        await track.applyConstraints({ advanced: [{ focusMode: "continuous" }] });
      } catch (_) {
        // Preserve the current focus mode if continuous focus is unsupported.
      }
    }

    var maxFrameRate = capabilities && capabilities.frameRate && capabilities.frameRate.max;
    if (maxFrameRate) {
      Array.from(document.getElementById("cameraFps").options).forEach(function (option) {
        option.disabled = Number(option.value) > Number(maxFrameRate);
      });
    }
  }

  async function applyReceiveSettings() {
    if (!state.cameraRunning || state.cameraComplete) {
      return;
    }

    startCameraWorkers(state.cameraGeneration);
    var track = state.cameraStream && state.cameraStream.getVideoTracks()[0];
    if (!track) {
      return;
    }
    var width = Number(document.getElementById("cameraWidth").value || "1280");
    try {
      await track.applyConstraints({
        width: { ideal: width },
        height: { ideal: Math.round(width * 3 / 4) },
        frameRate: { ideal: Number(document.getElementById("cameraFps").value || "60") }
      });
    } catch (_) {
      showToast("This camera cannot change settings while scanning. Stop and restart it to apply the settings.");
    }
  }

  function startCameraWorkers(generation) {
    stopCameraWorkers();
    state.cameraTracking = null;
    var workerCount = Math.max(1, Math.min(3, Number(document.getElementById("cameraWorkers").value || "2")));
    diag.event("worker.start", diag.id(state.cameraReceiverId), { workers: workerCount });
    for (var index = 0; index < workerCount; index++) {
      (function () {
        var worker = new Worker("/qr-worker.js?v=__Xfer_BUILD_ID__");
        var slot = { worker: worker, busy: false, failed: false, ready: false, frameId: -1, initTimer: null, region: null };
        var fail = function (reason, detail) {
          diag.event("worker.failed", diag.id(state.cameraReceiverId), { initTimeout: !reason, wasReady: slot.ready,
            failure: detail || { stage: reason ? "worker.script" : "worker.init_timeout", type: reason ? "WorkerError" : "Timeout", code: 0, site: "app.startCameraWorkers" } });
          window.clearTimeout(slot.initTimer);
          slot.busy = false;
          slot.failed = true;
          worker.terminate();
          // Include the worker failure detail so startup errors identify their cause.
          if (reason && !state.cameraWorkerReason) state.cameraWorkerReason = String(reason);
          if (generation === state.cameraGeneration && state.cameraWorkers.indexOf(slot) >= 0 &&
              state.cameraWorkers.every(function (candidate) { return candidate.failed; }))
            finishCameraError("Could not start QR scanning. Reload the page and try again."
              + (state.cameraWorkerReason ? " (" + state.cameraWorkerReason + ")" : ""));
        };
        slot.initTimer = window.setTimeout(fail, 15000);
        worker.onmessage = function (event) {
          if (generation !== state.cameraGeneration || state.cameraWorkers.indexOf(slot) < 0) return;
          var message = event.data || {};
          if (message.fatal) { fail(message.error, message.diagnostic); return; }
          if (message.id === -1) {
            window.clearTimeout(slot.initTimer);
            slot.ready = message.ready === true;
            diag.event("worker.ready", diag.id(state.cameraReceiverId), { ready: slot.ready });
            if (!slot.ready) fail(message.error, message.diagnostic);
            return;
          }
          if (message.id !== slot.frameId) return;
          slot.busy = false;
          diag.sample(message.bytes ? "decode.read" : "decode.miss", diag.id(state.cameraReceiverId), performance.now() - slot.sentAt,
            { failed: !!message.error, symbols: message.observed ? message.observed.symbols : 0,
              invalid: message.observed ? message.observed.invalid : 0, foreign: message.observed ? message.observed.foreign : 0 });
          if (message.observed && message.observed.foreign) diag.sample("decode.foreign", diag.id(state.cameraReceiverId), 0);
          if (message.observed && message.observed.invalid) diag.sample("decode.non_transfer", diag.id(state.cameraReceiverId), 0);
          if (message.error) diag.sample("decode.failed", diag.id(state.cameraReceiverId), 0,
            message.diagnostic || { stage: "decode.unknown", type: "OtherError", code: 0, site: "app.workerMessage" });
          if (!state.cameraRunning) return;
          var bytes = message.bytes ? new Uint8Array(message.bytes) : null;
          var frame = bytes && opticalCore.parseFrame(bytes);
          if (frame && state.cameraUpload && state.cameraUpload.session &&
              frame.session !== state.cameraUpload.session) frame = null;
          if (message.id > state.cameraLastResultId) {
            state.cameraLastResultId = message.id;
            if (frame) {
              state.cameraLastDecodedAt = performance.now();
              var tracking = opticalCore.trackPosition(message.position, slot.region,
                slot.videoWidth, slot.videoHeight, state.cameraLastDecodedAt);
              if (tracking) state.cameraTracking = tracking;
            } else if (state.cameraTracking) state.cameraTracking.misses++;
          }
          if (frame && state.cameraUpload) {
            var queue = state.cameraUpload;
            var reason = queue.keys.has(frame.key) ? "duplicate" : queue.queue.length >= 128 ? "full" : "accepted";
            var accepted = queue.push(bytes);
            diag.sample("relay." + (accepted ? "accepted" : reason === "accepted" ? "rejected" : reason), diag.transfer(frame.session), 0,
              { receiver: diag.id(state.cameraReceiverId), sequence: frame.sequence, queued: queue.queue.length });
          }
        };
        worker.onerror = function (error) { fail(error, { stage: "worker.script", type: "WorkerError", code: 0,
          site: "app.workerError", line: error.lineno || 0, column: error.colno || 0 }); };
        state.cameraWorkers.push(slot);
      })();
    }
  }

  function stopCameraWorkers() {
    state.cameraWorkers.forEach(function (slot) {
      window.clearTimeout(slot.initTimer);
      slot.worker.terminate();
    });
    state.cameraWorkers = [];
  }

  function scheduleCameraFrame(generation) {
    if (generation !== state.cameraGeneration || !state.cameraRunning) {
      return;
    }
    var video = document.getElementById("cameraVideo");
    var next = function () {
      if (generation !== state.cameraGeneration || !state.cameraRunning) {
        return;
      }
      diag.sample("camera.video_callback", diag.id(state.cameraReceiverId), 0);
      captureCameraFrame(generation);
      scheduleCameraFrame(generation);
    };
    if (typeof video.requestVideoFrameCallback === "function") {
      video.requestVideoFrameCallback(next);
    } else {
      window.requestAnimationFrame(next);
    }
  }

  function captureCameraFrame(generation) {
    var slot = state.cameraWorkers.find(function (candidate) { return candidate.ready && !candidate.busy && !candidate.failed; });
    if (!slot) { diag.sample("camera.no_worker", diag.id(state.cameraReceiverId), 0); return; }
    var video = document.getElementById("cameraVideo");
    var width = video.videoWidth, height = video.videoHeight;
    if (!width || !height) return;
    var now = performance.now();
    // Avoid burning CPU on blank video; immediately return to the capture rate on detection.
    if ((!state.cameraTracking || now - state.cameraLastDecodedAt > 1200) && now - state.cameraLastCaptureAt < 65) return;
    state.cameraLastCaptureAt = now;
    var frameId = state.cameraFrameId++;
    var region = opticalCore.scanRegion(width, height, state.cameraTracking, frameId, now);
    if (cameraCanvas.width !== region.width || cameraCanvas.height !== region.height) {
      cameraCanvas.width = region.width;
      cameraCanvas.height = region.height;
    }
    try {
      var context = cameraCanvas.getContext("2d", { willReadFrequently: true });
      context.drawImage(video, region.x, region.y, region.width, region.height, 0, 0, region.width, region.height);
      var image = context.getImageData(0, 0, region.width, region.height);
      slot.busy = true;
      slot.sentAt = performance.now();
      diag.sample("camera.capture", diag.id(state.cameraReceiverId), slot.sentAt - now, { width: width, height: height, cropWidth: region.width, cropHeight: region.height, full: region.full });
      slot.frameId = frameId;
      slot.region = region;
      slot.videoWidth = width;
      slot.videoHeight = height;
      slot.worker.postMessage({ id: frameId, buf: image.data.buffer, w: region.width, h: region.height,
        session: state.cameraUpload && state.cameraUpload.session }, [image.data.buffer]);
    } catch (error) {
      slot.busy = false;
      if (generation === state.cameraGeneration) finishCameraError(cameraErrorMessage(error));
    }
  }

  function applyReceiveProgress(result) {
    diag.sample("relay.progress", diag.id(state.cameraReceiverId), 0, result ? {
      recognized: result.recognized, complete: result.complete, solved: result.solvedBlocks, blocks: result.sourceBlocks,
      received: result.framesCollected, duplicates: result.duplicateFrames, elapsedSeconds: result.elapsedSeconds
    } : null);
    if (!result || !result.recognized || state.cameraComplete) {
      return;
    }

    state.cameraHasDecoded = true;
    window.clearTimeout(state.cameraQuietTimer);
    state.cameraQuietTimer = null;
    document.getElementById("cameraNoSignal").hidden = true;
    var helpDialog = document.getElementById("cameraHelpDialog");
    if (helpDialog.open) {
      helpDialog.close();
    }
    var percent = Math.max(0, Math.min(100, Math.round(Number(result.progress || 0) * 100)));
    document.getElementById("receiveProgressBar").style.width = percent + "%";
    document.getElementById("receiveProgress").setAttribute("aria-valuenow", String(percent));
    // The numbers people read change at most once a second (the bar follows every result); the last one at once.
    var now = Date.now();
    if (result.complete || !state.receiveShownAt || now - state.receiveShownAt >= 1000) {
      state.receiveShownAt = now;
      document.getElementById("receiveState").textContent = result.complete ? "Received" : "Receiving " + percent + "%";
      document.getElementById("receiveRate").textContent = formatRate(result.kilobytesPerSecond);
      var elapsed = Number(result.elapsedSeconds || 0);
      var progress = Number(result.progress || 0);
      var remaining = progress > 0 && progress < 1 ? elapsed * (1 - progress) / progress : 0;
      document.getElementById("receiveEta").textContent = result.complete
        ? "Total: " + formatSeconds(elapsed)
        : remaining > 0
          ? "Remaining: " + formatSeconds(remaining)
          : "";
      document.getElementById("receiveDetail").textContent =
        Number(result.solvedBlocks || 0).toLocaleString("en-US") + " / " +
        Number(result.sourceBlocks || 0).toLocaleString("en-US") + " blocks";
    }

    if (!result.complete) {
      armCameraQuietHint(state.cameraGeneration, 4000);
      return;
    }

    diag.event("receive.complete", diag.id(state.cameraReceiverId), { files: result.fileCount, bytes: result.totalBytes });
    state.cameraComplete = true;
    document.getElementById("receiveProgressBar").style.width = "100%";
    document.getElementById("receiveProgressBar").classList.add("is-done");
    document.getElementById("receiveProgress").setAttribute("aria-valuenow", "100");
    document.getElementById("receiveResultText").textContent =
      "Files: " + Number(result.fileCount || 0).toLocaleString("en-US") + " · " + formatBytes(result.totalBytes);
    var output = document.getElementById("receiveOutputPath");
    output.textContent = result.outputPath || "";
    output.title = result.outputPath || "";
    document.getElementById("receiveResult").hidden = false;
    stopCamera(false, true);
    setCameraLabel("Received");
    showToast("Files received: " + Number(result.fileCount || 0).toLocaleString("en-US"));
    if (result.openError) {
      showToast("Received, but could not open the folder: " + result.openError);
    }
  }

  function stopCamera(clearServer, keepLabel) {
    if (state.cameraStarting || state.cameraRunning) diag.event("camera.stop", diag.id(state.cameraReceiverId), { complete: state.cameraComplete, clearServer: !!clearServer });
    state.cameraGeneration++;
    if (state.cameraUpload) state.cameraUpload.stop();
    state.cameraUpload = null;
    state.cameraTracking = null;
    window.clearTimeout(state.cameraQuietTimer);
    state.cameraQuietTimer = null;
    document.getElementById("cameraNoSignal").hidden = true;
    stopCameraWorkers();
    if (state.cameraStream) {
      state.cameraStream.getTracks().forEach(function (track) { track.stop(); });
    }
    state.cameraStream = null;
    state.cameraStarting = false;
    state.cameraRunning = false;
    releaseScreenWakeLock();
    var helpDialog = document.getElementById("cameraHelpDialog");
    if (helpDialog.open) {
      helpDialog.close();
    }
    document.getElementById("cameraPreview").classList.remove("is-live");
    var video = document.getElementById("cameraVideo");
    video.pause();
    video.srcObject = null;
    video.hidden = true;
    if (!keepLabel) {
      setCameraLabel("Camera stopped");
    }
    if (clearServer && state.cameraReceiverId) {
      diag.fetch("/api/optical/receive/stop", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ receiverId: state.cameraReceiverId })
      }).catch(function () {});
    }
    updateOpticalAction();
  }

  function finishCameraError(message) {
    diag.event("camera.failed", diag.id(state.cameraReceiverId));
    stopCamera(true, true);
    setCameraLabel(message);
    document.getElementById("receiveState").textContent = "Stopped";
    showToast(message);
  }

  function resetReceivePanel() {
    state.cameraComplete = false;
    state.cameraHasDecoded = false;
    document.getElementById("receiveState").textContent = "Waiting";
    document.getElementById("receiveRate").textContent = "";
    document.getElementById("receiveProgressBar").classList.remove("is-done");
    document.getElementById("receiveProgressBar").style.width = "0%";
    document.getElementById("receiveProgress").setAttribute("aria-valuenow", "0");
    document.getElementById("receiveDetail").textContent = "";
    document.getElementById("receiveEta").textContent = "";
    state.receiveShownAt = 0;
    document.getElementById("cameraNoSignal").hidden = true;
    document.getElementById("receiveResult").hidden = true;
  }

  function armCameraQuietHint(generation, delay) {
    window.clearTimeout(state.cameraQuietTimer);
    state.cameraQuietTimer = window.setTimeout(function () {
      if (generation !== state.cameraGeneration || !state.cameraRunning || state.cameraComplete) {
        return;
      }
      document.getElementById("cameraNoSignal").hidden = false;
    }, delay || 8000);
  }

  function dismissCameraQuietHint() {
    document.getElementById("cameraNoSignal").hidden = true;
    if (state.cameraRunning && !state.cameraComplete) {
      armCameraQuietHint(state.cameraGeneration, 15000);
    }
  }

  function setCameraLabel(message) {
    document.getElementById("cameraLabel").textContent = message;
  }

  function cameraErrorMessage(error) {
    var name = error && error.name ? error.name : "";
    if (name === "NotAllowedError" || name === "SecurityError") {
      return "Camera access is denied. Allow it in your browser settings and try again.";
    }
    if (name === "NotFoundError" || name === "DevicesNotFoundError") {
      setCapability("statusCamera", "None", false);
      return "No camera was found.";
    }
    if (name === "NotReadableError" || name === "TrackStartError") {
      return "Could not open the camera. Check whether another app is using it.";
    }
    return errorMessage(error);
  }

  function bytesToBase64(bytes) {
    var binary = "";
    for (var index = 0; index < bytes.length; index++) {
      binary += String.fromCharCode(bytes[index]);
    }
    return window.btoa(binary);
  }

  function formatRate(value) {
    var rate = Math.max(0, Number(value) || 0);
    return rate.toLocaleString("en-US", { maximumFractionDigits: 1 }) + " KB/s";
  }

  function shutdownCamera() {
    if (!state.cameraRunning && !state.cameraStarting) {
      return;
    }
    var receiverId = state.cameraReceiverId;
    stopCamera(false, true);
    if (!receiverId || !navigator.sendBeacon) {
      return;
    }
    navigator.sendBeacon(
      "/api/optical/receive/stop",
      new Blob([JSON.stringify({ receiverId: receiverId })], { type: "application/json" }));
  }

  function updateOpticalAction() {
    updateTransferFormats();
    var hasFiles = state.selections.optical.size > 0;
    document.getElementById("showQr").disabled = state.opticalBusy
      || !state.status
      || !hasFiles;
    var cameraButton = document.getElementById("readLocalCamera");
    cameraButton.disabled = !state.status;
    cameraButton.textContent = state.cameraRunning || state.cameraStarting
      ? "Stop scanning"
      : state.role === "remote"
        ? "Use device camera"
        : "Use PC camera";
    document.getElementById("showRemoteQr").disabled = state.opticalBusy
      || state.role !== "local"
      || state.remotes.length === 0
      || !hasFiles;
    document.getElementById("readRemoteCamera").disabled = state.role !== "local"
      || state.remotes.length === 0;
  }

  function updateTransferFormats() {
    // Xfer sends the selected original files.

  }

  async function sendRemoteCommand(action, button) {
    if (state.role !== "local" || button.disabled) {
      return;
    }
    button.disabled = true;
    try {
      var body = { action: action };
      if (action === "showQr") {
        body.files = Array.from(state.selections.optical);
        body.format = activeData("transferFormat", "transferFormat", "original");
        body.frameBytes = Number(document.getElementById("frameAmount").value || "1000");
        body.framesPerSecond = Number(document.getElementById("frameRate").value || "30");
        body.errorCorrection = String(document.getElementById("errorCorrection").value || "L");
        body.displaySize = Number(document.getElementById("qrDisplaySize").value || "900");
      }
      await requestJson("/api/remotes/command", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
    } catch (error) {
      showToast(errorMessage(error));
    } finally {
      updateOpticalAction();
    }
  }

  function activeData(groupId, property, fallback) {
    var active = document.querySelector("#" + groupId + " button.is-active");
    return active && active.dataset[property] ? active.dataset[property] : fallback;
  }

  function changeQrSize(direction) {
    setQrDisplaySize(state.qrDisplaySize + direction * 50);
  }

  function applyQrSize() {
    setQrDisplaySize(Number(document.getElementById("qrDisplaySize").value || "900"));
  }

  function setQrDisplaySize(value) {
    var control = document.getElementById("qrDisplaySize");
    var minimum = Number(control.min || "300");
    var maximum = Number(control.max || "1200");
    value = Math.round(Number(value) / 50) * 50;
    value = Math.max(minimum, Math.min(maximum, value || 900));
    state.qrDisplaySize = value;
    control.value = String(value);
    document.getElementById("qrSizeSetting").textContent = value.toLocaleString("en-US") + " px";
    document.getElementById("qrSizeOutput").textContent = value.toLocaleString("en-US") + " px";
    document.getElementById("smallerQr").disabled = value === minimum;
    document.getElementById("largerQr").disabled = value === maximum;
    if (state.opticalRenderer) {
      sizeQrCanvas(state.opticalRenderer);
    }
  }

  function formatSeconds(value) {
    var seconds = Math.max(0, Number(value) || 0);
    if (seconds < 10) {
      return seconds.toFixed(1) + " s";
    }
    if (seconds < 60) {
      return Math.ceil(seconds).toLocaleString("en-US") + " s";
    }
    var rounded = Math.ceil(seconds);
    var minutes = Math.floor(rounded / 60);
    var remainder = rounded % 60;
    return minutes.toLocaleString("en-US") + " min" + (remainder ? " " + remainder + " s" : "");
  }

  async function pollConnectedRemotes() {
    window.clearTimeout(state.localRemoteTimer);
    if (state.role !== "local") {
      return;
    }
    try {
      var result = await requestJson("/api/remotes");
      state.remotes = result && Array.isArray(result.remotes) ? result.remotes : [];
      renderConnectedRemotes();
      updateOpticalAction();
    } catch (_) {
      // A later poll will retry without interrupting the current operation.
    } finally {
      if (state.role === "local") {
        state.localRemoteTimer = window.setTimeout(pollConnectedRemotes, 3000);
      }
    }
  }

  function renderConnectedRemotes() {
    var container = document.getElementById("connectedRemotes");
    container.replaceChildren();
    if (state.remotes.length === 0) {
      appendMessage(container, "No phones connected", "list-empty");
      return;
    }
    state.remotes.forEach(function (remote) {
      var row = document.createElement("div");
      row.className = "list-row";
      var dot = document.createElement("span");
      dot.className = "dot";
      dot.setAttribute("aria-hidden", "true");
      row.appendChild(dot);
      row.appendChild(textCell(remote.name || "Phone", ""));
      container.appendChild(row);
    });
  }

  async function loadRemoteEntry() {
    if (state.role !== "local") {
      return;
    }
    var image = document.getElementById("pairingQr");
    var status = document.getElementById("pairingStatus");
    var url = document.getElementById("pairingUrl");
    try {
      var entry = await requestJson("/api/remote-entry");
      if (!entry || !entry.url) {
        throw new Error("Could not obtain the Tailscale phone connection URL.");
      }
      image.onload = function () {
        image.hidden = false;
        status.textContent = "Scan this code with your phone to connect to this PC.";
      };
      image.onerror = function () {
        image.hidden = true;
        status.textContent = "Could not display the QR code.";
      };
      url.textContent = entry.url;
      url.title = entry.url;
      url.hidden = false;
      image.src = "/api/remote-entry/qr";
    } catch (error) {
      image.hidden = true;
      url.hidden = true;
      status.textContent = errorMessage(error);
    }
  }

  function startRemoteConnection() {
    if (!state.remoteId) {
      state.remoteId = createRemoteId();
    }
    heartbeatRemote();
    pollRemoteFolder();
  }

  async function heartbeatRemote() {
    window.clearTimeout(state.remoteHeartbeatTimer);
    if (state.role !== "remote" || !state.remoteId) {
      return;
    }
    try {
      var result = await requestJson("/api/remotes/heartbeat", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id: state.remoteId, name: describeRemoteDevice() })
      });
      if (result && result.command) {
        handleRemoteCommand(result.command);
      }
    } catch (_) {
      // Staying on the page is enough; the next heartbeat reconnects it.
    } finally {
      if (state.role === "remote") {
        state.remoteHeartbeatTimer = window.setTimeout(heartbeatRemote, 3500);
      }
    }
  }

  async function pollRemoteFolder() {
    window.clearTimeout(state.remoteFolderTimer);
    if (state.role !== "remote") {
      return;
    }
    try {
      var snapshot = await requestJson("/api/folder?mode=optical");
      if (folderSignature(snapshot) !== state.folderSignatures.optical) {
        // The active QR payload is immutable. Metadata refresh must not close it.
        applyFolder("optical", snapshot, true);
      }
    } catch (_) {
      // A transient tailnet pause leaves the last visible selection in place.
    } finally {
      if (state.role === "remote") {
        state.remoteFolderTimer = window.setTimeout(pollRemoteFolder, 2200);
      }
    }
  }

  function handleRemoteCommand(command) {
    if (command.action === "showQr") {
      if (Array.isArray(command.files)) {
        state.selections.optical = new Set(command.files);
        renderFiles("optical");
      }
      startOptical(command);
    } else if (command.action === "readCamera") {
      startCamera();
    }
  }

  function disconnectRemote() {
    window.clearTimeout(state.remoteHeartbeatTimer);
    window.clearTimeout(state.remoteFolderTimer);
    if (state.role !== "remote" || !state.remoteId || !navigator.sendBeacon) {
      return;
    }
    var body = new Blob(
      [JSON.stringify({ id: state.remoteId })],
      { type: "application/json" });
    navigator.sendBeacon("/api/remotes/disconnect", body);
  }

  function createRemoteId() {
    if (window.crypto && typeof window.crypto.randomUUID === "function") {
      return window.crypto.randomUUID();
    }
    var values = new Uint32Array(4);
    if (window.crypto && typeof window.crypto.getRandomValues === "function") {
      window.crypto.getRandomValues(values);
      return Array.from(values).map(function (value) {
        return value.toString(16).padStart(8, "0");
      }).join("");
    }
    return String(Date.now()) + "-" + String(Math.random()).slice(2);
  }

  function describeRemoteDevice() {
    var agent = navigator.userAgent || "";
    if (/iPad/i.test(agent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1)) {
      return "iPad";
    }
    if (/iPhone/i.test(agent)) {
      return "iPhone";
    }
    if (/Android/i.test(agent)) {
      return /Mobile/i.test(agent) ? "Android phone" : "Android tablet";
    }
    return "Phone";
  }

  async function toggleDeviceMenu() {
    var menu = document.getElementById("remoteDeviceMenu");
    if (!menu.hidden) {
      closeDeviceMenu();
      return;
    }
    menu.hidden = false;
    document.getElementById("remoteDeviceButton").setAttribute("aria-expanded", "true");
    setListMessage("remoteDeviceMenu", "Looking for PCs", "menu-message");
    try {
      var result = await requestJson("/api/devices");
      renderDeviceOptions(result && Array.isArray(result.devices) ? result.devices : []);
    } catch (error) {
      setListMessage("remoteDeviceMenu", errorMessage(error), "menu-message");
    }
  }

  function closeDeviceMenu() {
    document.getElementById("remoteDeviceMenu").hidden = true;
    document.getElementById("remoteDeviceButton").setAttribute("aria-expanded", "false");
  }

  function renderDeviceOptions(devices) {
    var menu = document.getElementById("remoteDeviceMenu");
    menu.replaceChildren();
    if (devices.length === 0) {
      appendMessage(menu, "No PCs running Xfer were found", "menu-message");
      return;
    }
    devices.forEach(function (device) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "menu-item";
      button.setAttribute("role", "option");
      button.setAttribute("aria-selected", device.current ? "true" : "false");
      button.classList.toggle("is-on", Boolean(device.current));
      var dot = document.createElement("span");
      dot.className = "dot";
      dot.setAttribute("aria-hidden", "true");
      button.appendChild(dot);
      button.appendChild(textCell(device.name || device.host, "menu-name"));
      button.addEventListener("click", function () {
        if (device.current || !device.url) {
          closeDeviceMenu();
          return;
        }
        window.location.assign(device.url + "#optical");
      });
      menu.appendChild(button);
    });
  }

  function folderSignature(snapshot) {
    return JSON.stringify(snapshot || null);
  }

  function registerServiceWorker() {
    if (!("serviceWorker" in navigator) || !window.isSecureContext) {
      return;
    }
    window.addEventListener("load", function () {
      navigator.serviceWorker.register(
        "/service-worker.js?v=__Xfer_BUILD_ID__",
        { scope: "/", updateViaCache: "none" }).catch(function () {});
    });
  }

  function safeErrorType(error) {
    var name = error && error.name;
    return /^(NotAllowedError|NotFoundError|NotReadableError|OverconstrainedError|AbortError|TypeError|SecurityError|RuntimeError)$/.test(name) ? name : "OtherError";
  }

  async function requestJson(url, options, timeoutMs) {
    var diagnosticEnd = diag.begin("http.body", diag.route(url));
    var diagnosticOk = false;
    var settings = Object.assign({}, options || {});
    var controller = timeoutMs ? new AbortController() : null;
    var originalSignal = settings.signal;
    var forwardAbort = function () { if (controller) controller.abort(); };
    var timer = null;
    if (controller) {
      settings.signal = controller.signal;
      if (originalSignal) {
        if (originalSignal.aborted) controller.abort();
        else originalSignal.addEventListener("abort", forwardAbort, { once: true });
      }
      timer = window.setTimeout(function () { controller.abort(); }, timeoutMs);
    }
    try {
      var response = await diag.fetch(url, settings);
      var body;
      try { body = await response.json(); } catch (_) { body = null; }
      if (!response.ok || body === null) {
        var error = new Error(body && body.error ? body.error : "Could not connect to Xfer.");
        error.status = response.ok ? 502 : response.status;
        throw error;
      }
      diagnosticOk = true;
      return body;
    } finally {
      diagnosticEnd(response ? response.status : 0, !diagnosticOk);
      window.clearTimeout(timer);
      if (originalSignal && controller) originalSignal.removeEventListener("abort", forwardAbort);
    }
  }

  function setListMessage(id, message, className) {
    var container = document.getElementById(id);
    container.replaceChildren();
    appendMessage(container, message, className);
  }

  function appendMessage(container, message, className) {
    var element = document.createElement("div");
    element.className = className;
    element.textContent = message;
    container.appendChild(element);
  }

  function showToast(message) {
    window.clearTimeout(toastTimer);
    toast.textContent = message;
    toast.hidden = false;
    toastTimer = window.setTimeout(function () {
      toast.hidden = true;
    }, 4200);
  }

  function errorMessage(error) {
    return error && error.message ? error.message : "An Xfer operation failed.";
  }

  function folderLeaf(path) {
    var parts = String(path || "output").split(/[\\/]/).filter(Boolean);
    return parts.length ? parts[parts.length - 1] : "output";
  }

  function previewOutputPath(folder, fileName) {
    var target = folder.sourceKind === "files"
      && Array.isArray(folder.files)
      && folder.files.length === 1
      ? folder.files[0].name
      : folderLeaf(folder.path);
    return ".\\output\\" + target + "_YYYYMMDD-HHmm\\" + fileName;
  }

  function formatBytes(value) {
    var bytes = Number(value) || 0;
    if (bytes < 1024) {
      return bytes + " B";
    }
    var units = ["KB", "MB", "GB", "TB"];
    var size = bytes;
    var unit = -1;
    do {
      size /= 1024;
      unit++;
    } while (size >= 1024 && unit < units.length - 1);
    var digits = size >= 100 ? 0 : size >= 10 ? 1 : 1;
    return size.toLocaleString("en-US", { maximumFractionDigits: digits }) + " " + units[unit];
  }

  function formatDate(value) {
    var date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return "";
    }
    return pad(date.getMonth() + 1) + "/" + pad(date.getDate()) + " " + pad(date.getHours()) + ":" + pad(date.getMinutes());
  }

  function pad(value) {
    return String(value).padStart(2, "0");
  }
})();
