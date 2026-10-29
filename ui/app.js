(function () {
  "use strict";

  var $ = function (id) { return document.getElementById(id); };
  var MB = 1024 * 1024;
  var FRAME_BYTES = [500, 1000, 1465, 1850, 2331, 2953];
  var FRAME_RATES = [10, 15, 20, 24, 30, 60];
  var COLUMNS = [1, 2, 3];
  var DEFAULT_FPS = 30, DEFAULT_COLUMNS = 1;
  // Tile (named "fx2" in settings and requests): independent patches fill the window. 30 fps was fastest
  // in the C920 screen-to-file measurements; patches crossing a display transition are recovered later.
  var DEFAULT_LAYOUT = "fx2", DEFAULT_FX2_FPS = 30, DEFAULT_COARSE = 0, MAX_CELL = 32;
  // Tile cells are set and shown in CSS px (3 by default, like the rest of the page) and drawn in whole device
  // pixels: the CSS size times the pixel ratio, rounded.
  var DEFAULT_CELL = 3, MAX_CSS_CELL = 16;
  var cellOverride = null; // device px from --cell (measurements), not saved
  // Full HD capture: every cell or QR module covers half as many pixels again as at 1280.
  var DEFAULT_CAMERA_WIDTH = 1920, DEFAULT_WORKERS = 3;
  var EC_LIMIT = { L: 2953, M: 2331, Q: 1663, H: 1273 };

  // ---------------------------------------------------------------- native component

  function native(op, args) {
    var request = { op: op };
    if (args) for (var key in args) request[key] = args[key];
    var reply = JSON.parse(uiHost.call(JSON.stringify(request)));
    if (reply && reply.error) throw new Error(reply.error);
    return reply;
  }

  // Work that takes time (dialogs, building a transfer) runs as a job; the page keeps drawing meanwhile.
  function job(op, args) {
    return new Promise(function (resolve, reject) {
      var started;
      try { started = native(op, args); } catch (error) { reject(error); return; }
      (function poll() {
        var reply;
        try { reply = native("job", { id: started.job }); } catch (error) { reject(error); return; }
        if (reply.pending) { setTimeout(poll, 40); return; }
        if (reply.error) reject(new Error(reply.error)); else resolve(reply.result);
      })();
    });
  }

  // ---------------------------------------------------------------- stored preferences

  // One JSON file in the page's own storage (navigator.storage.getDirectory), read once before start-up.
  var saved = {}, savedFile = null, saving = Promise.resolve();
  function loadSaved() {
    return navigator.storage.getDirectory()
      .then(function (root) { return root.getFileHandle("settings.json", { create: true }); })
      .then(function (handle) { savedFile = handle; return handle.getFile(); })
      .then(function (file) { return file.text(); })
      .then(function (text) { var value = text ? JSON.parse(text) : {}; if (value && typeof value === "object") saved = value; })
      .catch(function () { /* the defaults apply */ });
  }
  function stored(key, fallback) { return saved[key] === undefined ? fallback : saved[key]; }
  function store(key, value) {
    saved[key] = value;
    if (!savedFile) return;
    var text = JSON.stringify(saved);
    saving = saving.then(function () { return savedFile.createWritable(); })
      .then(function (writer) { return writer.write(text).then(function () { return writer.close(); }); })
      .catch(function () { /* the setting still applies to this session */ });
  }

  var settings = {};
  function setting(name, fallback) { return settings[name] === undefined ? fallback : settings[name]; }
  function save(name, value) { settings[name] = value; store("settings", settings); }

  var state = {
    view: "send", info: null, folder: null, files: [], rows: {}, picking: false, starting: false,
    sending: null, camera: null, cameras: [], remotes: 0, activation: -1, receivedPath: ""
  };

  // ---------------------------------------------------------------- formatting

  function bytes(value) {
    var n = Number(value) || 0;
    if (n < 1024) return n + " B";
    var units = ["KB", "MB", "GB"], unit = -1;
    do { n /= 1024; unit++; } while (n >= 1024 && unit < units.length - 1);
    return (n >= 100 ? Math.round(n) : Math.round(n * 10) / 10).toLocaleString("ja-JP") + " " + units[unit];
  }
  function count(n) { return Number(n || 0).toLocaleString("ja-JP"); }
  function seconds(value) {
    var s = Math.max(0, Number(value) || 0);
    if (s < 60) return Math.max(1, Math.ceil(s)) + " 秒";
    var m = Math.floor(Math.ceil(s) / 60), rest = Math.ceil(s) % 60;
    return m + " 分" + (rest ? " " + rest + " 秒" : "");
  }
  function pad(n) { return n < 10 ? "0" + n : String(n); }
  function date(value) {
    var d = new Date(value);
    if (isNaN(d.getTime())) return "";
    return (d.getMonth() + 1) + "/" + pad(d.getDate()) + " " + pad(d.getHours()) + ":" + pad(d.getMinutes());
  }

  // ---------------------------------------------------------------- toast

  var toastTimer = 0;
  function toast(message) {
    var el = $("toast");
    el.textContent = message;
    el.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.hidden = true; }, 4200);
  }
  function fail(error) { toast(error && error.message ? error.message : String(error)); }

  // ---------------------------------------------------------------- theme and sidebar

  function applyTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme);
    $("themeButton").textContent = theme === "dark" ? "ライトにする" : "ダークにする";
  }
  $("themeButton").addEventListener("click", function () {
    var next = document.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark";
    applyTheme(next);
    store("theme", next);
  });

  var sidebar = $("sidebar"), resizer = $("resizer");
  function sidebarWidth(width) { return Math.max(180, Math.min(320, Math.round(width))); }
  var drag = null;
  resizer.addEventListener("pointerdown", function (event) {
    drag = { x: event.clientX, width: sidebar.getBoundingClientRect().width };
    resizer.setPointerCapture(event.pointerId);
    resizer.classList.add("is-dragging");
    event.preventDefault();
  });
  resizer.addEventListener("pointermove", function (event) {
    if (!drag) return;
    sidebar.style.width = sidebarWidth(drag.width + event.clientX - drag.x) + "px";
  });
  function endDrag() {
    if (!drag) return;
    drag = null;
    resizer.classList.remove("is-dragging");
    store("sidebar", sidebarWidth(sidebar.getBoundingClientRect().width));
  }
  resizer.addEventListener("pointerup", endDrag);
  resizer.addEventListener("pointercancel", endDrag);

  // ---------------------------------------------------------------- views

  document.querySelectorAll("[data-view]").forEach(function (button) {
    button.addEventListener("click", function () { showView(button.getAttribute("data-view")); });
  });

  function showView(view) {
    if (view !== "receive" && state.camera) stopReceiving();
    state.view = view;
    document.querySelectorAll("[data-view]").forEach(function (button) {
      button.classList.toggle("is-active", button.getAttribute("data-view") === view);
    });
    document.querySelectorAll("[data-panel]").forEach(function (panel) {
      panel.hidden = panel.getAttribute("data-panel") !== view;
    });
    closeMenu();
    if (view === "receive") loadCameras();
    if (view === "phone") loadPhone();
  }

  // ---------------------------------------------------------------- menus

  var openMenu = null;
  function closeMenu() {
    if (!openMenu) return;
    openMenu.element.remove();
    openMenu.owner.setAttribute("aria-expanded", "false");
    openMenu = null;
  }

  function showMenu(owner, items, choose) {
    if (openMenu && openMenu.owner === owner) { closeMenu(); return; }
    closeMenu();
    var menu = document.createElement("div");
    menu.className = "menu";
    menu.setAttribute("role", "listbox");
    items.forEach(function (item) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "menu-item" + (item.on ? " is-on" : "");
      button.setAttribute("role", "option");
      button.setAttribute("aria-selected", item.on ? "true" : "false");
      button.textContent = item.label;
      button.disabled = !!item.disabled;
      button.addEventListener("click", function (event) {
        event.stopPropagation();
        closeMenu();
        choose(item.value);
      });
      menu.appendChild(button);
    });
    var box = owner.getBoundingClientRect();
    menu.style.left = Math.round(box.left) + "px";
    menu.style.top = Math.round(box.bottom + 4) + "px";
    menu.style.minWidth = Math.round(box.width) + "px";
    $("menuLayer").appendChild(menu);
    // Keep the menu inside the window: open upwards when there is no room below.
    var height = menu.getBoundingClientRect().height;
    if (box.bottom + 4 + height > window.innerHeight - 8) menu.style.top = Math.max(8, Math.round(box.top - 4 - height)) + "px";
    owner.setAttribute("aria-expanded", "true");
    openMenu = { element: menu, owner: owner };
  }

  document.addEventListener("click", function (event) {
    if (openMenu && !openMenu.element.contains(event.target) && !openMenu.owner.contains(event.target)) closeMenu();
  });

  // ---------------------------------------------------------------- send: selection

  function loadFolder() {
    var first = native("folder", { offset: 0 });
    var files = first.files.slice();
    while (files.length < first.count) {
      var next = native("folder", { offset: files.length });
      if (!next.files.length) break;
      files = files.concat(next.files);
    }
    state.folder = first;
    state.files = files;
    renderFolder();
  }

  // A long path keeps its start and its last folders, so the chosen folder stays readable.
  function shortPath(path, element) {
    if (!path) return "";
    var room = Math.max(24, Math.floor(element.clientWidth / 7.2));
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

  function renderPath() {
    var element = $("sourcePath"), path = state.folder ? state.folder.path || "" : "";
    element.textContent = shortPath(path, element);
    element.title = path;
  }

  function renderFolder() {
    var folder = state.folder;
    renderPath();
    var list = $("fileList");
    list.replaceChildren();
    state.rows = {};
    var fragment = document.createDocumentFragment();
    state.files.forEach(function (file) {
      var row = document.createElement("button");
      row.type = "button";
      row.className = "row" + (file.selected ? " is-on" : "");
      row.setAttribute("role", "checkbox");
      row.setAttribute("aria-checked", file.selected ? "true" : "false");
      row.title = file.name;
      var cell = document.createElement("span"); cell.className = "check-cell";
      var check = document.createElement("span"); check.className = "check";
      cell.appendChild(check);
      row.appendChild(cell);
      row.appendChild(text("c-name", file.name));
      row.appendChild(text("c-size", bytes(file.size)));
      row.appendChild(text("c-date", date(file.modified)));
      row.addEventListener("click", function () { toggleFile(file); });
      state.rows[file.name] = row;
      fragment.appendChild(row);
    });
    list.appendChild(fragment);
    list.scrollTop = 0;
    $("fileEmpty").hidden = state.files.length > 0;
    renderTotals(folder);
  }

  function text(className, value) {
    var span = document.createElement("span");
    span.className = className;
    span.textContent = value;
    return span;
  }

  function toggleFile(file) {
    try {
      var totals = native("select", { name: file.name, on: !file.selected });
      file.selected = !file.selected;
      var row = state.rows[file.name];
      row.classList.toggle("is-on", file.selected);
      row.setAttribute("aria-checked", file.selected ? "true" : "false");
      renderTotals(totals);
    } catch (error) { fail(error); }
  }

  $("selectAll").addEventListener("click", function () {
    var on = state.folder.selectedCount < state.files.length;
    try {
      var totals = native("selectAll", { on: on });
      state.files.forEach(function (file) {
        file.selected = on;
        var row = state.rows[file.name];
        row.classList.toggle("is-on", on);
        row.setAttribute("aria-checked", on ? "true" : "false");
      });
      renderTotals(totals);
    } catch (error) { fail(error); }
  });

  function renderTotals(totals) {
    state.folder.selectedCount = totals.selectedCount;
    state.folder.selectedBytes = totals.selectedBytes;
    var all = $("selectAll");
    var total = state.files.length;
    all.setAttribute("aria-checked", total > 0 && totals.selectedCount === total ? "true" : totals.selectedCount > 0 ? "mixed" : "false");
    all.disabled = total === 0;
    $("sendSummary").textContent = total === 0 ? "" : count(totals.selectedCount) + " ファイル · " + bytes(totals.selectedBytes);
    updateSendButtons();
  }

  function updateSendButtons() {
    var ready = state.folder && state.folder.selectedCount > 0 && !state.starting;
    $("showQr").disabled = !ready;
    $("phoneShowQr").disabled = !ready || state.remotes === 0;
    $("pickFiles").disabled = state.picking;
    $("pickFolder").disabled = state.picking;
  }

  function pick(op) {
    if (state.picking) return;
    state.picking = true;
    updateSendButtons();
    job(op).then(function (result) {
      if (!result.cancelled) loadFolder();
    }).catch(fail).then(function () {
      state.picking = false;
      updateSendButtons();
    });
  }
  $("pickFiles").addEventListener("click", function () { pick("pickFiles"); });
  $("pickFolder").addEventListener("click", function () { pick("pickFolder"); });

  // ---------------------------------------------------------------- send: settings

  // QR and Tile keep their own frame rate and error correction.
  function layout() { return setting("layout", DEFAULT_LAYOUT) === "qr" ? "qr" : "fx2"; }
  function fps() { return layout() === "fx2" ? setting("fx2Fps", DEFAULT_FX2_FPS) : setting("fps", DEFAULT_FPS); }
  function ec() { return layout() === "fx2" ? setting("fx2Ec", "L") : setting("ec", "L"); }
  // Cell size in device pixels; three CSS pixels until changed.
  function pixelRatio() { return window.devicePixelRatio || 1; }
  // The CSS size, saved as "cell".
  function cellCss() { return Math.max(1, Math.min(MAX_CSS_CELL, setting("cell", DEFAULT_CELL))); }
  // The device px drawn on screen.
  function cell() { return cellOverride || Math.max(1, Math.min(MAX_CELL, Math.round(cellCss() * pixelRatio()))); }
  // What the page shows: CSS px ("3 px"); a --cell size in device px shows its CSS equivalent.
  function cellShown() { return cellOverride ? Math.round(cellOverride / pixelRatio() * 10) / 10 : cellCss(); }

  function renderSendSettings() {
    var qrEc = setting("ec", "L");
    var limit = EC_LIMIT[qrEc];
    var frameBytes = Math.min(setting("frameBytes", 1000), limit);
    if (FRAME_BYTES.indexOf(frameBytes) < 0) frameBytes = FRAME_BYTES.filter(function (v) { return v <= limit; }).pop();
    settings.frameBytes = frameBytes;
    document.querySelectorAll("[data-for]").forEach(function (field) { field.hidden = field.getAttribute("data-for") !== layout(); });
    document.querySelectorAll("#layoutGroup button").forEach(function (button) {
      button.classList.toggle("is-on", button.getAttribute("data-layout") === layout());
    });
    $("frameBytes").querySelector(".select-value").textContent = count(frameBytes) + " bytes";
    $("frameRate").querySelector(".select-value").textContent = setting("fps", DEFAULT_FPS) + " fps";
    $("tileColor").setAttribute("aria-checked", setting("color", false) ? "true" : "false");
    $("tileRate").querySelector(".select-value").textContent = setting("fx2Fps", DEFAULT_FX2_FPS) + " fps";
    $("columns").querySelector(".select-value").textContent = columnsLabel(setting("columns", DEFAULT_COLUMNS));
    document.querySelectorAll("#ecGroup button").forEach(function (button) {
      button.classList.toggle("is-on", button.getAttribute("data-ec") === setting("ec", "L"));
    });
    document.querySelectorAll("#tileEcGroup button").forEach(function (button) {
      button.classList.toggle("is-on", button.getAttribute("data-ec") === setting("fx2Ec", "L"));
    });
    $("cellValue").textContent = cellShown() + " px";
    $("cellSmaller").disabled = cellShown() <= 1;
    $("cellLarger").disabled = cellShown() >= MAX_CSS_CELL || cell() >= MAX_CELL;
    document.querySelectorAll("#coarseGroup button").forEach(function (button) {
      button.classList.toggle("is-on", Number(button.getAttribute("data-coarse")) === setting("coarse", DEFAULT_COARSE));
    });
    var size = setting("qrSize", 900);
    $("qrSizeValue").textContent = count(size) + " px";
    $("qrSmaller").disabled = size <= 300;
    $("qrLarger").disabled = size >= 1200;
  }

  $("frameBytes").addEventListener("click", function () {
    var limit = EC_LIMIT[setting("ec", "L")], current = setting("frameBytes", 1000);
    showMenu(this, FRAME_BYTES.map(function (value) {
      return { value: value, label: count(value) + " bytes", on: value === current, disabled: value > limit };
    }), function (value) { save("frameBytes", value); renderSendSettings(); });
  });
  // Each method keeps its own frame rate and error correction.
  function rateMenu(owner, key, fallback) {
    var current = setting(key, fallback);
    showMenu(owner, FRAME_RATES.map(function (value) {
      return { value: value, label: value + " fps", on: value === current };
    }), function (value) { save(key, value); renderSendSettings(); });
  }
  $("frameRate").addEventListener("click", function () { rateMenu(this, "fps", DEFAULT_FPS); });
  $("tileRate").addEventListener("click", function () { rateMenu(this, "fx2Fps", DEFAULT_FX2_FPS); });
  document.querySelectorAll("#layoutGroup button").forEach(function (button) {
    button.addEventListener("click", function () { save("layout", button.getAttribute("data-layout")); renderSendSettings(); });
  });
  document.querySelectorAll("#coarseGroup button").forEach(function (button) {
    button.addEventListener("click", function () { save("coarse", Number(button.getAttribute("data-coarse"))); renderSendSettings(); });
  });
  $("tileColor").addEventListener("click", function () { save("color", !setting("color", false)); renderSendSettings(); });
  function changeCell(step) {
    cellOverride = null;
    save("cell", Math.max(1, Math.min(MAX_CSS_CELL, Math.round(cellShown()) + step)));
    renderSendSettings();
    if (state.sending && state.sending.fx2) { state.sending.cell = cell(); sizePresentation(); }
  }
  $("cellSmaller").addEventListener("click", function () { changeCell(-1); });
  $("cellLarger").addEventListener("click", function () { changeCell(1); });
  function columnsLabel(value) { return value === 1 ? "1 枚" : value + " 枚を横に"; }
  $("columns").addEventListener("click", function () {
    var current = setting("columns", DEFAULT_COLUMNS);
    showMenu(this, COLUMNS.map(function (value) {
      return { value: value, label: columnsLabel(value), on: value === current };
    }), function (value) { save("columns", value); renderSendSettings(); });
  });
  document.querySelectorAll("#ecGroup button").forEach(function (button) {
    button.addEventListener("click", function () { save("ec", button.getAttribute("data-ec")); renderSendSettings(); });
  });
  document.querySelectorAll("#tileEcGroup button").forEach(function (button) {
    button.addEventListener("click", function () { save("fx2Ec", button.getAttribute("data-ec")); renderSendSettings(); });
  });
  function changeQrSize(step) {
    save("qrSize", Math.max(300, Math.min(1200, setting("qrSize", 900) + step)));
    renderSendSettings();
    if (state.sending) sizePresentation();
  }
  $("qrSmaller").addEventListener("click", function () { changeQrSize(-50); });
  $("qrLarger").addEventListener("click", function () { changeQrSize(50); });

  // ---------------------------------------------------------------- send: QR on this screen

  $("showQr").addEventListener("click", function () {
    if (!state.folder || state.folder.selectedCount === 0 || state.starting) return;
    if (state.folder.selectedBytes > 16 * MB && layout() === "qr") {
      var blocks = Math.ceil(state.folder.selectedBytes / (setting("frameBytes", 1000) - 20));
      var perSecond = setting("fps", DEFAULT_FPS) * setting("columns", DEFAULT_COLUMNS) * setting("rows", 1);
      confirm("大きいファイルを送ります", bytes(state.folder.selectedBytes) + " あります。送り終えるまで少なくとも " + seconds(blocks / perSecond) + "かかります。", startSending);
      return;
    }
    startSending();
  });

  function startSending() {
    state.starting = true;
    updateSendButtons();
    job("send.start", { layout: layout(), frameBytes: setting("frameBytes", 1000), fps: fps(), ec: ec(), color: setting("color", false),
      columns: setting("columns", DEFAULT_COLUMNS), rows: setting("rows", 1) }).then(function (session) {
      present(session);
    }).catch(fail).then(function () {
      state.starting = false;
      updateSendButtons();
    });
  }

  // Each display tick shows columns x rows consecutive frames in one image and moves the sequence past them.
  function present(session) {
    var tiles = (session.columns || 1) * (session.rows || 1);
    state.sending = { session: session, sequence: 0, tiles: tiles, next: 0, interval: 1000 / session.framesPerSecond, scale: 1,
      fx2: session.layout === "fx2", cell: cell(), coarse: setting("coarse", DEFAULT_COARSE) };
    $("presentFrame").classList.toggle("is-fx2", state.sending.fx2);
    $("present").classList.toggle("is-fx2", state.sending.fx2);
    $("present").hidden = false;
    $("presentBar").style.opacity = "";
    native("send.presentation", { fullscreen: state.sending.fx2 });
    sizePresentation();
    requestAnimationFrame(tick);
    if (state.sending.fx2) $("present").focus(); else $("stopQr").focus();
  }

  var diagnosticFrames = 0;
  setInterval(function () {
    if (!state.sending) return;
    try { native("diagnostics", { sequence: state.sending.sequence, scale: state.sending.scale, frames: diagnosticFrames }); } catch (_) {}
    diagnosticFrames = 0;
  }, 5000);

  function showFrame() {
    var s = state.sending;
    diagnosticFrames++;
    // Measurements may draw the picture as a trapezoid (thousandths of the size).
    var keystone = (setting("keystoneX", 0) ? "&kx=" + setting("keystoneX", 0) * 10 : "") + (setting("keystoneY", 0) ? "&ky=" + setting("keystoneY", 0) * 10 : "");
    $("presentImage").src = s.fx2
      ? "app:fx2?t=" + s.session.token + "&s=" + s.sequence + "&w=" + s.width + "&h=" + s.height + "&m=" + s.cell + "&k=" + s.coarse + keystone
      : "app:qr?t=" + s.session.token + "&s=" + s.sequence + "&m=" + s.scale + "&c=" + (s.session.columns || 1) + "&r=" + (s.session.rows || 1) + keystone;
  }

  // The window's size and pixel ratio as the presentation was laid out for them.
  function viewKey() { return window.innerWidth + "x" + window.innerHeight + "@" + (window.devicePixelRatio || 1); }

  function tick(now) {
    var s = state.sending;
    if (!s) return;
    requestAnimationFrame(tick);
    // A window moved to another screen or resized (by the window manager too) is laid out again, even when no
    // resize event comes.
    if (s.view !== viewKey()) sizePresentation();
    if (s.next === 0) s.next = now + s.interval;
    if (now < s.next) return;
    s.sequence += s.fx2 ? s.patches : s.tiles;
    showFrame();
    s.next += s.interval;
    if (s.next <= now) s.next = now + s.interval; // never flash catch-up frames after a stall
  }

  // The QR image has whole device pixels per module and is placed on whole device pixels, so it is drawn one
  // image pixel per screen pixel: every module keeps the same sharp size. "QR の大きさ" is the size of one code;
  // several codes side by side shrink together when the window is too narrow for them.
  // Tile fills the window, from its top-left corner (whole device pixels), one image pixel per screen
  // pixel; the layout of that size says how many patches (sequence numbers) each picture carries.
  function sizeFx2() {
    // Controls overlay the data and hide while the pointer is away from the bottom edge.
    var s = state.sending, ratio = window.devicePixelRatio || 1, bar = 34;
    s.width = Math.floor(window.innerWidth * ratio);
    s.height = Math.floor(window.innerHeight * ratio);
    s.cell = cell(); // the same CSS size on a screen of another pixel ratio
    try {
      var layout = native("send.fx2layout", { width: s.width, height: s.height, cell: s.cell, coarse: s.coarse });
      s.patches = layout.patches;
    } catch (error) { fail(error); stopSending(); return; }
    var frame = $("presentFrame"), image = $("presentImage");
    frame.style.left = "0px";
    frame.style.top = "0px";
    image.style.width = s.width / ratio + "px";
    image.style.height = s.height / ratio + "px";
    $("presentBar").style.top = (s.height / ratio - bar) + "px";
    showFrame();
    $("presentSize").textContent = "セル " + cellShown() + " px";
    $("presentSmaller").disabled = cellShown() <= 1;
    $("presentLarger").disabled = cellShown() >= MAX_CSS_CELL || s.cell >= MAX_CELL;
  }

  function sizePresentation() {
    var s = state.sending;
    if (!s) return;
    s.view = viewKey();
    if (s.fx2) { sizeFx2(); return; }
    var side = s.session.side, ratio = window.devicePixelRatio || 1;
    var columns = s.session.columns || 1, rows = s.session.rows || 1, between = s.session.gap || 0;
    var unitWidth = columns * side + (columns - 1) * between, unitHeight = rows * side + (rows - 1) * between;
    var scale = Math.floor(setting("qrSize", 900) * ratio / side);
    scale = Math.min(scale, Math.floor((window.innerWidth - 48) * ratio / unitWidth), Math.floor((window.innerHeight - 110) * ratio / unitHeight));
    s.scale = Math.max(1, scale);
    var deviceWidth = unitWidth * s.scale, deviceHeight = unitHeight * s.scale;
    var cssWidth = deviceWidth / ratio, cssHeight = deviceHeight / ratio, gap = 18, bar = 34;
    var left = Math.floor((window.innerWidth * ratio - deviceWidth) / 2) / ratio;
    var top = Math.max(0, Math.floor((window.innerHeight * ratio - deviceHeight - (gap + bar) * ratio) / 2) / ratio);
    var frame = $("presentFrame"), image = $("presentImage");
    frame.style.left = left + "px";
    frame.style.top = top + "px";
    image.style.width = cssWidth + "px";
    image.style.height = cssHeight + "px";
    $("presentBar").style.top = (top + cssHeight + gap) + "px";
    showFrame();
    $("presentSize").textContent = count(setting("qrSize", 900)) + " px";
    $("presentSmaller").disabled = setting("qrSize", 900) <= 300;
    $("presentLarger").disabled = setting("qrSize", 900) >= 1200;
  }
  $("presentSmaller").addEventListener("click", function () { if (state.sending && state.sending.fx2) changeCell(-1); else changeQrSize(-50); });
  $("presentLarger").addEventListener("click", function () { if (state.sending && state.sending.fx2) changeCell(1); else changeQrSize(50); });

  function stopSending() {
    if (!state.sending) return;
    state.sending = null;
    native("send.presentation", { fullscreen: false });
    $("present").hidden = true;
    try { native("send.stop"); } catch (error) { /* the session ends with the window anyway */ }
    $("showQr").focus();
  }
  $("stopQr").addEventListener("click", stopSending);
  window.addEventListener("resize", function () { sizePresentation(); sizePreview(); closeMenu(); renderPath(); });

  // ---------------------------------------------------------------- confirm dialog

  var confirmed = null;
  function confirm(title, message, then) {
    $("confirmTitle").textContent = title;
    $("confirmText").textContent = message;
    confirmed = then;
    $("confirmDialog").hidden = false;
    $("confirmOk").focus();
  }
  function closeConfirm() { $("confirmDialog").hidden = true; confirmed = null; }
  $("confirmCancel").addEventListener("click", closeConfirm);
  $("confirmOk").addEventListener("click", function () { var then = confirmed; closeConfirm(); if (then) then(); });

  // ---------------------------------------------------------------- receive

  function loadCameras() {
    try { state.cameras = native("cameras").cameras; } catch (error) { state.cameras = []; fail(error); }
    var chosen = setting("camera", "");
    if (!state.cameras.some(function (c) { return c.id === chosen; })) chosen = state.cameras.length ? state.cameras[0].id : "";
    settings.camera = chosen;
    renderReceiveSettings();
  }

  function cameraName(id) {
    for (var i = 0; i < state.cameras.length; i++) if (state.cameras[i].id === id) return state.cameras[i].name;
    return "";
  }

  function renderReceiveSettings() {
    $("cameraField").hidden = state.cameras.length < 2;
    $("cameraSelect").querySelector(".select-value").textContent = cameraName(setting("camera", ""));
    var running = !!state.camera;
    document.querySelectorAll("#widthGroup button").forEach(function (b) { b.classList.toggle("is-on", Number(b.getAttribute("data-width")) === setting("cameraWidth", DEFAULT_CAMERA_WIDTH)); b.disabled = running; });
    document.querySelectorAll("#fpsGroup button").forEach(function (b) { b.classList.toggle("is-on", Number(b.getAttribute("data-fps")) === setting("cameraFps", 30)); b.disabled = running; });
    document.querySelectorAll("#workersGroup button").forEach(function (b) { b.classList.toggle("is-on", Number(b.getAttribute("data-workers")) === setting("workers", DEFAULT_WORKERS)); b.disabled = running; });
    $("cameraSelect").disabled = running;
    $("openWhenDone").setAttribute("aria-checked", setting("openWhenDone", true) ? "true" : "false");
    $("cameraButton").textContent = running ? "読むのをやめる" : "この PC のカメラで読む";
    $("cameraButton").disabled = !running && state.cameras.length === 0;
    $("phoneReadCamera").disabled = state.remotes === 0;
  }

  $("cameraSelect").addEventListener("click", function () {
    var current = setting("camera", "");
    showMenu(this, state.cameras.map(function (c) { return { value: c.id, label: c.name, on: c.id === current }; }), function (value) {
      save("camera", value); renderReceiveSettings();
    });
  });
  document.querySelectorAll("#widthGroup button").forEach(function (b) { b.addEventListener("click", function () { save("cameraWidth", Number(b.getAttribute("data-width"))); renderReceiveSettings(); }); });
  document.querySelectorAll("#fpsGroup button").forEach(function (b) { b.addEventListener("click", function () { save("cameraFps", Number(b.getAttribute("data-fps"))); renderReceiveSettings(); }); });
  document.querySelectorAll("#workersGroup button").forEach(function (b) { b.addEventListener("click", function () { save("workers", Number(b.getAttribute("data-workers"))); renderReceiveSettings(); }); });
  $("openWhenDone").addEventListener("click", function () { save("openWhenDone", !setting("openWhenDone", true)); renderReceiveSettings(); });

  $("cameraButton").addEventListener("click", function () {
    if (state.camera) stopReceiving(); else startReceiving();
  });

  function resetMeter() {
    $("receiveState").textContent = "待っています";
    $("receiveEta").textContent = "";
    $("receiveBlocks").textContent = "";
    $("receiveRate").textContent = "";
    $("receiveBar").style.width = "0%";
    $("receiveBar").classList.remove("is-done");
    $("receiveResult").hidden = true;
    $("quietHint").hidden = true;
  }

  function startReceiving() {
    resetMeter();
    try {
      native("receive.start", { camera: setting("camera", ""), width: setting("cameraWidth", DEFAULT_CAMERA_WIDTH), fps: setting("cameraFps", 30), workers: setting("workers", DEFAULT_WORKERS), openWhenDone: setting("openWhenDone", true) });
    } catch (error) { fail(error); return; }
    state.camera = { preview: -1, hintDismissed: false, timer: setInterval(pollCamera, 33) };
    $("previewIdle").textContent = "";
    $("receiveState").textContent = "QR を待っています";
    renderReceiveSettings();
  }

  // The whole camera picture, as large as the preview area allows.
  function sizePreview() {
    var camera = state.camera, image = $("previewImage");
    if (!camera || !camera.width) { image.style.width = "0px"; image.style.height = "0px"; return; }
    var box = $("preview"), scale = Math.min(box.clientWidth / camera.width, box.clientHeight / camera.height);
    image.style.width = Math.floor(camera.width * scale) + "px";
    image.style.height = Math.floor(camera.height * scale) + "px";
  }

  function stopReceiving(keepMeter) {
    var camera = state.camera;
    if (!camera) return;
    clearInterval(camera.timer);
    state.camera = null;
    try { native("receive.stop"); } catch (error) { /* already stopped */ }
    $("previewImage").removeAttribute("src");
    sizePreview();
    $("previewIdle").textContent = "カメラは止まっています";
    $("quietHint").hidden = true;
    $("receiveSummary").textContent = "";
    if (!keepMeter) resetMeter();
    renderReceiveSettings();
  }

  // Numbers people read while receiving (percent, time left, blocks, speed, the camera line) change at most once a
  // second; the status is still read every 33 ms and the bar follows every reading.
  var READABLE_MS = 1000;
  function readableNow(holder) {
    var now = Date.now();
    if (holder.shownAt && now - holder.shownAt < READABLE_MS) return false;
    holder.shownAt = now;
    return true;
  }

  function pollCamera() {
    var camera = state.camera;
    if (!camera) return;
    var show = readableNow(camera);
    var status;
    try { status = native("receive.status"); } catch (error) { stopReceiving(true); fail(error); return; }
    if (status.previewSequence !== camera.preview && status.previewSequence > 0) {
      camera.preview = status.previewSequence;
      $("previewImage").src = "app:camera?n=" + camera.preview;
    }
    if (status.width && (status.width !== camera.width || status.height !== camera.height)) {
      camera.width = status.width;
      camera.height = status.height;
      sizePreview();
    }
    if (status.width && show) $("receiveSummary").textContent = status.camera + " · " + status.width + "×" + status.height + " · " + Math.round(status.fps) + " fps" +
      (status.layout === "fx2" ? " · Tile " + count(status.patchesSeen) + " 区画" : "");
    var result = status.result;
    if (result && result.recognized) {
      // The sender changed and the receiver moved to the new transfer (OpticalReceiveService.SwitchAfter).
      if ((result.switches || 0) > (camera.switches || 0)) { camera.switches = result.switches; toast("別の送信に切り替えました"); }
      var percent = Math.max(0, Math.min(100, Math.floor(result.progress * 100)));
      $("receiveBar").style.width = percent + "%";
      if (show || result.complete) {
        $("receiveBlocks").textContent = count(result.solvedBlocks) + " / " + count(result.sourceBlocks) + " ブロック";
        $("receiveRate").textContent = (Math.round(result.kilobytesPerSecond * 10) / 10).toLocaleString("ja-JP") + " KB/s";
      }
      if (result.complete) {
        $("receiveState").textContent = "受け取りました";
        $("receiveEta").textContent = "合計 " + seconds(result.elapsedSeconds);
        $("receiveBar").classList.add("is-done");
        state.receivedPath = result.outputPath || "";
        $("receiveResultText").textContent = count(result.fileCount) + " ファイル · " + bytes(result.totalBytes) + "　" + (result.outputPath || "");
        $("receiveResult").hidden = false;
        stopReceiving(true);
        if (result.openError) toast(result.openError);
        return;
      }
      if (show) {
        $("receiveState").textContent = "受け取っています " + percent + "%";
        var remaining = result.progress > 0 ? result.elapsedSeconds * (1 - result.progress) / result.progress : 0;
        $("receiveEta").textContent = remaining > 0 ? "残り " + seconds(remaining) : "";
      }
    }
    if (status.error) { stopReceiving(true); $("receiveState").textContent = "止まりました"; toast(status.error); return; }
    $("quietHint").hidden = camera.hintDismissed || status.quietMs < 8000;
  }

  $("tipsButton").addEventListener("click", function () { $("tipsDialog").hidden = false; $("tipsClose").focus(); });
  $("tipsClose").addEventListener("click", function () {
    $("tipsDialog").hidden = true;
    if (state.camera) { state.camera.hintDismissed = true; $("quietHint").hidden = true; }
  });

  $("openReceived").addEventListener("click", function () { openFolder(state.receivedPath); });
  $("openOutput").addEventListener("click", function () { openFolder(state.info.outputRoot); });
  function openFolder(path) {
    try { native("openFolder", { path: path }); } catch (error) { fail(error); }
  }

  // ---------------------------------------------------------------- phone

  function loadPhone() {
    renderRemotes();
    $("pairUrl").textContent = "";
    $("pairQr").removeAttribute("src");
    $("pairQr").parentNode.classList.remove("is-ready");
    $("copyUrl").disabled = true;
    $("pairLead").textContent = "";
    job("remote.entry").then(function (entry) {
      // Whole device pixels per module, about 180 px wide.
      var image = $("pairQr"), ratio = window.devicePixelRatio || 1;
      var scale = Math.max(1, Math.floor(180 * ratio / entry.side));
      image.src = "app:pairing?m=" + scale + "&u=" + encodeURIComponent(entry.url);
      image.style.width = entry.side * scale / ratio + "px";
      image.style.height = entry.side * scale / ratio + "px";
      image.parentNode.classList.add("is-ready");
      $("pairUrl").textContent = entry.url;
      $("pairUrl").title = entry.url;
      $("copyUrl").disabled = false;
      $("pairLead").textContent = "スマホのカメラで読むと、この PC につながります。";
    }).catch(function (error) {
      $("pairLead").textContent = error.message;
    });
  }

  $("copyUrl").addEventListener("click", function () {
    var url = $("pairUrl").textContent;
    if (!url) return;
    navigator.clipboard.writeText(url).then(function () { toast("URL をコピーしました"); });
  });

  function renderRemotes() {
    var list = $("remoteList");
    var remotes = [];
    try { remotes = native("remote.list").remotes; } catch (error) { remotes = []; }
    list.replaceChildren();
    if (remotes.length === 0) { list.appendChild(text("list-empty", "つながっているスマホはありません")); return; }
    remotes.forEach(function (remote) {
      var row = document.createElement("div");
      row.className = "list-row";
      var dot = document.createElement("span"); dot.className = "list-dot";
      row.appendChild(dot);
      row.appendChild(text("", remote.name));
      list.appendChild(row);
    });
  }

  function sendToPhone(action, message) {
    try {
      // The phone shows and reads QR codes, with the QR settings.
      native("remote.command", { action: action, frameBytes: setting("frameBytes", 1000), fps: setting("fps", DEFAULT_FPS), ec: setting("ec", "L"), displaySize: setting("qrSize", 900) });
      toast(message);
    } catch (error) { fail(error); }
  }
  $("phoneShowQr").addEventListener("click", function () { sendToPhone("showQr", "スマホに QR を出します"); });
  // The phone's camera reads QR codes only; nothing here switches by itself, the message says what to choose.
  $("phoneReadCamera").addEventListener("click", function () {
    sendToPhone("readCamera", layout() === "qr" ? "スマホのカメラで読みます"
      : "スマホのカメラで読みます。スマホは QR だけを読むので、送り方の「表示」を QR にしてから出してください");
  });

  // ---------------------------------------------------------------- keyboard and background polling

  document.addEventListener("keydown", function (event) {
    if (event.key === "Tab" && state.sending && state.sending.fx2) {
      event.preventDefault();
      var controls = [$("presentSmaller"), $("presentLarger"), $("stopQr")];
      var index = controls.indexOf(document.activeElement), direction = event.shiftKey ? -1 : 1;
      if (index < 0) index = event.shiftKey ? 0 : -1;
      do { index = (index + direction + controls.length) % controls.length; } while (controls[index].disabled);
      $("presentBar").style.opacity = "1";
      controls[index].focus();
      return;
    }
    if (event.key !== "Escape") return;
    if (openMenu) { closeMenu(); return; }
    if (!$("tipsDialog").hidden) { $("tipsDialog").hidden = true; return; }
    if (!$("confirmDialog").hidden) { closeConfirm(); return; }
    if (state.sending) stopSending();
  });

  function poll() {
    var status;
    try { status = native("poll"); } catch (error) { return; }
    if (status.shutdown) { window.close(); return; }
    if (state.activation >= 0 && status.activation !== state.activation) {
      // Opened again from the context menu: show what was chosen there.
      if (state.sending) stopSending();
      try { loadFolder(); } catch (error) { fail(error); }
      showView("send");
    }
    state.activation = status.activation;
    if (status.remotes !== state.remotes) {
      state.remotes = status.remotes;
      $("phoneCount").hidden = state.remotes === 0;
      $("phoneCount").textContent = String(state.remotes);
      updateSendButtons();
      renderReceiveSettings();
      if (state.view === "phone") renderRemotes();
    }
  }

  // ---------------------------------------------------------------- start

  function start() {
    var values = stored("settings", {});
    settings = values && typeof values === "object" ? values : {};
    applyTheme(stored("theme", "dark") === "light" ? "light" : "dark");
    sidebar.style.width = sidebarWidth(stored("sidebar", 224)) + "px";
    try {
      state.info = native("init", { args: uiHost.arguments });
      $("outputPath").textContent = state.info.outputRoot;
      $("outputPath").title = state.info.outputRoot;
      loadFolder();
      loadCameras();
      if (state.info.pathError) toast(state.info.pathError);
      else if (state.info.serverError) toast("スマホとはつながりません: " + state.info.serverError);
    } catch (error) {
      fail(error);
    }
    renderSendSettings();
    renderReceiveSettings();
    poll();
    setInterval(poll, 500);
    setInterval(function () { if (state.view === "phone") renderRemotes(); }, 2000);
    autoSend();
    autoReceive();
  }

  // Measurement start-up (--send on the command line): the given settings apply to this window only, without
  // being saved, and the QR for the chosen file is shown at once.
  function autoSend() {
    var auto = state.info && state.info.autoSend;
    if (!auto) return;
    if (auto.layout) settings.layout = auto.layout;
    var fx2 = layout() === "fx2";
    if (auto.fps) settings[fx2 ? "fx2Fps" : "fps"] = auto.fps;
    if (auto.frameBytes) settings.frameBytes = auto.frameBytes;
    if (auto.ec) settings[fx2 ? "fx2Ec" : "ec"] = auto.ec;
    if (auto.qrSize) settings.qrSize = auto.qrSize;
    if (auto.columns) settings.columns = auto.columns;
    if (auto.rows) settings.rows = auto.rows;
    if (auto.cell) cellOverride = auto.cell;
    if (auto.color !== null && auto.color !== undefined) settings.color = auto.color;
    if (auto.coarse !== null && auto.coarse !== undefined) settings.coarse = auto.coarse;
    settings.keystoneX = auto.keystoneX || 0;
    settings.keystoneY = auto.keystoneY || 0;
    renderSendSettings();
    if (!state.folder || state.folder.selectedCount === 0) { toast("送るファイルがありません"); return; }
    startSending();
  }

  // Measurement start-up (--receive): the receive view starts the camera at once, with the given settings for this
  // window only.
  function autoReceive() {
    var auto = state.info && state.info.autoReceive;
    if (!auto) return;
    if (auto.width) settings.cameraWidth = auto.width;
    if (auto.fps) settings.cameraFps = auto.fps;
    if (auto.workers) settings.workers = auto.workers;
    showView("receive");
    if (auto.camera) {
      var wanted = auto.camera.toLowerCase(), found = state.cameras.filter(function (c) { return c.name.toLowerCase().indexOf(wanted) >= 0; });
      if (found.length !== 1) { toast("カメラ「" + auto.camera + "」が " + found.length + " 台見つかりました"); return; }
      settings.camera = found[0].id;
      renderReceiveSettings();
    }
    if (!state.camera) startReceiving();
  }

  loadSaved().then(start);
})();
