"use strict";

/* ---------- bridge to the app ---------- */

const bridge = (() => {
  const webview = window.chrome && window.chrome.webview;
  const listeners = {};
  const emit = (name, data) => (listeners[name] || []).forEach((fn) => fn(data));
  const on = (name, fn) => (listeners[name] = listeners[name] || []).push(fn);

  if (!webview) {
    // Opened in a normal browser: run against sample data so the interface can be previewed.
    return { on, call: (method, params) => window.mockCall(method, params || {}, emit) };
  }

  let seq = 0;
  const pending = new Map();
  webview.addEventListener("message", (e) => {
    const m = e.data;
    if (m.event) return emit(m.event, m.data);
    const waiter = pending.get(m.id);
    if (!waiter) return;
    pending.delete(m.id);
    if (m.ok) waiter.resolve(m.result);
    else waiter.reject(new Error(m.error || "Ocurrió un error."));
  });

  return {
    on,
    call(method, params) {
      return new Promise((resolve, reject) => {
        const id = String(++seq);
        pending.set(id, { resolve, reject });
        webview.postMessage({ id, method, params: params || {} });
      });
    },
  };
})();

/* ---------- small helpers ---------- */

function el(tag, attrs, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === null || v === undefined || v === false) continue;
    if (k === "class") node.className = v;
    else if (k === "html") node.innerHTML = v;
    else if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
    else if (k === "checked" || k === "disabled" || k === "value" || k === "hidden") node[k] = v;
    else node.setAttribute(k, v === true ? "" : v);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child.nodeType ? child : document.createTextNode(String(child)));
  }
  return node;
}

const nf = new Intl.NumberFormat("es-AR");
const fmt = (n) => nf.format(Math.round(n || 0));

function duration(minutes) {
  const total = Math.round(minutes || 0);
  if (total < 60) return { big: String(total), unit: "min", rest: "" };
  return { big: String(Math.floor(total / 60)), unit: "h", rest: `${total % 60} min` };
}

function seconds(s) {
  if (s < 60) return `${Math.round(s)} s`;
  return `${Math.floor(s / 60)} min ${Math.round(s % 60)} s`;
}

const ICONS = {
  home: '<path d="M4 11.5 12 4l8 7.5V20a1 1 0 0 1-1 1h-4.5v-6h-5v6H5a1 1 0 0 1-1-1z"/>',
  history: '<path d="M4 12a8 8 0 1 0 2.6-5.9M4 4v4h4M12 8v4.5l3 1.8"/>',
  book: '<path d="M5 4.5A1.5 1.5 0 0 1 6.5 3H19v15H6.5A1.5 1.5 0 0 0 5 19.5zM5 19.5A1.5 1.5 0 0 0 6.5 21H19M9 8h6"/>',
  bolt: '<path d="M13 3 5 13.5h6L10 21l8.5-11H12.5z"/>',
  check: '<path d="M4 7h10M4 12h6M4 17h8M14.5 15.5l2.5 2.5 4.5-5"/>',
  gear: '<circle cx="12" cy="12" r="3"/><path d="M12 3v2.5M12 18.5V21M3 12h2.5M18.5 12H21M5.6 5.6l1.8 1.8M16.6 16.6l1.8 1.8M5.6 18.4l1.8-1.8M16.6 7.4l1.8-1.8"/>',
  copy: '<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V6a2 2 0 0 1 2-2h8"/>',
  trash: '<path d="M4 7h16M9 7V4.5h6V7M6.5 7l1 13h9l1-13M10 11v5.5M14 11v5.5"/>',
  retry: '<path d="M20 12a8 8 0 1 1-2.6-5.9M20 4v4h-4"/>',
  eye: '<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="2.6"/>',
  edit: '<path d="M4 20h4L19 9l-4-4L4 16zM13.5 6.5l4 4"/>',
};

const icon = (name) =>
  el("span", { html: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">${ICONS[name]}</svg>`, style: "display:contents" });

let toastTimer;
function toast(message, isError) {
  const node = document.getElementById("toast");
  node.textContent = message;
  node.className = "toast show" + (isError ? " err" : "");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (node.className = "toast"), isError ? 5200 : 2400);
}

async function call(method, params) {
  try {
    return await bridge.call(method, params);
  } catch (err) {
    toast(err.message, true);
    throw err;
  }
}

/* ---------- app shell ---------- */

const PAGES = [
  { id: "inicio", label: "Inicio", icon: "home", render: renderHome },
  { id: "historial", label: "Historial", icon: "history", render: renderHistory },
  { id: "diccionario", label: "Diccionario", icon: "book", render: renderDictionary },
  { id: "atajos", label: "Atajos de texto", icon: "bolt", render: renderSnippets },
  { id: "autocorrector", label: "Autocorrector", icon: "check", render: renderAutocorrect },
  { id: "ajustes", label: "Ajustes", icon: "gear", render: renderSettings },
];

let state = null;
let current = "inicio";
let renderToken = 0;

const pageNode = document.getElementById("page");

function buildNav() {
  const nav = document.getElementById("nav");
  nav.replaceChildren(
    ...PAGES.map((p) =>
      el("button", { class: "nav-item" + (p.id === current ? " active" : ""), onclick: () => go(p.id) }, icon(p.icon), p.label)
    )
  );
}

async function go(id) {
  current = PAGES.some((p) => p.id === id) ? id : "inicio";
  buildNav();
  pageNode.scrollTop = 0;
  await refresh();
}

async function refresh() {
  const token = ++renderToken;
  const page = PAGES.find((p) => p.id === current);
  const content = await page.render();
  if (token !== renderToken) return; // a newer navigation won
  const scroll = pageNode.scrollTop;
  pageNode.replaceChildren(...content.filter(Boolean));
  pageNode.scrollTop = scroll;
}

function applyState(next) {
  state = next;
  document.getElementById("keyBanner").hidden = state.hasKey;
  document.getElementById("version").textContent = "Versión " + state.version;
  const quick = document.getElementById("acQuick");
  quick.checked = state.settings.autocorrectEnabled;
  document.getElementById("acQuickState").textContent = state.settings.autocorrectEnabled ? "activado" : "apagado";
}

async function loadState() {
  applyState(await call("state.get"));
}

async function setSettings(patch) {
  applyState(await call("settings.set", { patch }));
}

let refreshTimer;
function refreshSoon(pages) {
  if (!pages.includes(current)) return;
  // Do not redraw under someone who is typing in a form.
  const active = document.activeElement;
  if (active && ["INPUT", "TEXTAREA", "SELECT"].includes(active.tagName) && pageNode.contains(active)) return;
  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(refresh, 250);
}

/* ---------- Inicio ---------- */

async function renderHome() {
  const { insights: i, corrections: c, typingWpm } = await call("insights.get");

  if (!i.dictations && !c.total) {
    return [
      el("h1", {}, "Inicio"),
      el("p", { class: "lead" }, "Acá vas a ver cuánto tiempo ahorrás dictando y qué corrige el autocorrector."),
      el("div", { class: "empty" },
        el("strong", {}, "Todavía no hay nada para mostrar"),
        el("span", {}, "Poné el cursor en cualquier caja de texto, mantené ", el("kbd", {}, "Ctrl"), " + ", el("kbd", {}, "Win"), " y hablá. Al soltar, el texto aparece.")),
    ];
  }

  const saved = duration(i.minutesSaved);
  const week = duration(i.minutesSavedThisWeek);
  const speedup = typingWpm > 0 && i.averageWpm > 0 ? i.averageWpm / typingWpm : 0;

  const hero = el("div", { class: "card hero" },
    el("div", {},
      el("div", { class: "label" }, "Tiempo ahorrado dictando"),
      el("div", { class: "big num" }, saved.big, el("em", {}, saved.unit), saved.rest ? [saved.rest.split(" ")[0], el("em", {}, "min")] : null),
      el("div", { class: "muted small" }, `Comparado con escribir las mismas ${fmt(i.totalWords)} palabras a ${typingWpm} palabras por minuto.`)),
    el("div", { class: "side-stat" },
      el("b", { class: "num" }, week.rest ? `${week.big} h ${week.rest}` : `${week.big} min`),
      el("span", { class: "muted small" }, "ahorrados esta semana")));

  const stats = el("div", { class: "grid c4" },
    stat("Palabras dictadas", fmt(i.totalWords), `${fmt(i.wordsThisWeek)} esta semana`),
    stat("Velocidad al dictar", `${fmt(i.averageWpm)} ppm`, speedup >= 1.1 ? `${speedup.toFixed(1).replace(".", ",")}× más rápido que tipear` : "palabras por minuto"),
    stat("Racha", `${i.dayStreak} ${i.dayStreak === 1 ? "día" : "días"}`, `${i.weekStreak} ${i.weekStreak === 1 ? "semana seguida" : "semanas seguidas"}`),
    stat("Dictados", fmt(i.dictations), `${fmt(i.wordsToday)} palabras hoy`));

  const last30 = i.daily.slice(-30);
  const activity = el("div", { class: "card" },
    el("h2", {}, "Palabras por día"),
    barChart(last30),
    el("div", { class: "axis" }, el("span", {}, dayLabel(last30[0].date)), el("span", {}, "hoy")));

  const heat = el("div", { class: "card" },
    el("h2", {}, "Últimos 6 meses"),
    heatmap(i.daily),
    el("div", { class: "muted small", style: "margin-top:12px" }, "Cada cuadro es un día; más claro, más palabras dictadas."));

  const maxApp = Math.max(1, ...i.topApps.map((a) => a.words));
  const apps = el("div", { class: "card" },
    el("h2", {}, "Dónde dictás más"),
    i.topApps.length
      ? el("div", { class: "rows" }, i.topApps.slice(0, 6).map((a) =>
          el("div", { class: "bar-row" },
            el("span", { class: "name" }, appName(a.app)),
            el("span", { class: "track" }, el("span", { class: "fill", style: `display:block;width:${(a.words / maxApp) * 100}%` })),
            el("span", { class: "val num" }, fmt(a.words)))))
      : el("div", { class: "muted small" }, "Aparece después de tus primeros dictados."));

  const fixes = el("div", { class: "card" },
    el("h2", {}, "Autocorrector"),
    el("div", { class: "grid", style: "grid-template-columns:repeat(3,1fr);margin-bottom:14px" },
      mini("Hoy", c.today), mini("Esta semana", c.thisWeek), mini("En total", c.total)),
    c.top.length
      ? el("div", {}, c.top.slice(0, 5).map((p) =>
          el("div", { class: "pair" },
            el("span", {}, el("span", { class: "from" }, p.original), el("span", { class: "arrow" }, "→"), el("span", { class: "to" }, p.corrected)),
            el("span", { class: "muted num small" }, `${p.count}×`))))
      : el("div", { class: "muted small" }, "Cuando corrija algo, acá vas a ver tus errores más repetidos."));

  return [
    el("h1", {}, "Inicio"),
    el("p", { class: "lead" }, "Tu actividad con el dictado y el autocorrector."),
    hero, stats,
    el("div", { class: "grid c2" }, activity, heat),
    el("div", { class: "grid c2" }, apps, fixes),
  ];
}

const stat = (k, v, s) => el("div", { class: "card stat" }, el("div", { class: "k" }, k), el("div", { class: "v num" }, v), el("div", { class: "s" }, s));
const mini = (k, v) => el("div", {}, el("div", { class: "muted small" }, k), el("div", { class: "num", style: "font:600 20px var(--display)" }, fmt(v)));

function dayLabel(iso) {
  return new Date(iso).toLocaleDateString("es-AR", { day: "numeric", month: "short" });
}

function appName(process) {
  if (!process) return "Otra app";
  const known = { chrome: "Chrome", msedge: "Edge", firefox: "Firefox", code: "VS Code", cursor: "Cursor", claude: "Claude", whatsapp: "WhatsApp",
    "whatsapp.root": "WhatsApp", slack: "Slack", discord: "Discord", telegram: "Telegram", winword: "Word", excel: "Excel", outlook: "Outlook",
    notepad: "Bloc de notas", windowsterminal: "Terminal", notion: "Notion", obsidian: "Obsidian", teams: "Teams", applicationframehost: "App de Windows" };
  return known[process.toLowerCase()] || process.charAt(0).toUpperCase() + process.slice(1);
}

function barChart(days) {
  const max = Math.max(1, ...days.map((d) => d.words));
  const w = 100 / days.length;
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("class", "chart");
  svg.setAttribute("viewBox", "0 0 100 40");
  svg.setAttribute("preserveAspectRatio", "none");
  days.forEach((d, idx) => {
    const h = d.words ? Math.max(1.2, (d.words / max) * 38) : 0.8;
    const rect = document.createElementNS("http://www.w3.org/2000/svg", "rect");
    rect.setAttribute("x", (idx * w + w * 0.18).toFixed(2));
    rect.setAttribute("y", (40 - h).toFixed(2));
    rect.setAttribute("width", (w * 0.64).toFixed(2));
    rect.setAttribute("height", h.toFixed(2));
    rect.setAttribute("rx", "0.5");
    if (d.words) rect.setAttribute("class", "on");
    const title = document.createElementNS("http://www.w3.org/2000/svg", "title");
    title.textContent = `${dayLabel(d.date)}: ${fmt(d.words)} palabras`;
    rect.append(title);
    svg.append(rect);
  });
  return svg;
}

function heatmap(days) {
  const grid = el("div", { class: "heat" });
  const max = Math.max(1, ...days.map((d) => d.words));
  // Start the first column on Monday.
  const lead = (new Date(days[0].date).getDay() + 6) % 7;
  for (let k = 0; k < lead; k++) grid.append(el("i", { class: "pad" }));
  for (const d of days) {
    const level = d.words === 0 ? 0 : Math.min(4, 1 + Math.floor((d.words / max) * 3.999));
    grid.append(el("i", { "data-l": level, title: `${dayLabel(d.date)}: ${fmt(d.words)} palabras` }));
  }
  return grid;
}

/* ---------- Historial ---------- */

const historyView = { q: "", limit: 50, open: new Set() };

async function renderHistory() {
  const { items, total } = await call("history.list", { q: historyView.q, offset: 0, limit: historyView.limit });

  const search = el("input", { type: "search", placeholder: "Buscar en lo que dictaste…", value: historyView.q });
  let timer;
  search.addEventListener("input", () => {
    clearTimeout(timer);
    timer = setTimeout(async () => {
      historyView.q = search.value;
      historyView.limit = 50;
      await refresh();
      const again = pageNode.querySelector('input[type="search"]');
      if (again) { again.focus(); again.setSelectionRange(again.value.length, again.value.length); }
    }, 220);
  });

  const head = el("div", { class: "head" },
    el("div", {}, el("h1", {}, "Historial"), el("p", { class: "lead" }, "Todo lo que dictaste, guardado solo en esta computadora.")),
    total ? el("button", { class: "btn danger", onclick: clearHistory }, "Borrar todo") : null);

  if (!items.length) {
    return [head, el("div", { class: "toolbar" }, search),
      el("div", { class: "empty" }, el("strong", {}, historyView.q ? "Nada coincide con esa búsqueda" : "Todavía no dictaste nada"),
        el("span", {}, historyView.q ? "Probá con otra palabra." : "Tus dictados van a aparecer acá."))];
  }

  const list = [];
  let lastDay = "";
  for (const h of items) {
    const when = new Date(h.timestamp);
    const day = when.toLocaleDateString("es-AR", { weekday: "long", day: "numeric", month: "long" });
    if (day !== lastDay) {
      list.push(el("div", { class: "day" }, isToday(when) ? "Hoy" : day.charAt(0).toUpperCase() + day.slice(1)));
      lastDay = day;
    }
    list.push(historyEntry(h, when));
  }

  const more = items.length < total
    ? el("div", { class: "more" }, el("button", { class: "btn", onclick: () => { historyView.limit += 50; refresh(); } }, `Ver más (${fmt(total - items.length)} restantes)`))
    : null;

  return [head, el("div", { class: "toolbar" }, search), ...list, more];
}

function isToday(d) {
  const n = new Date();
  return d.getFullYear() === n.getFullYear() && d.getMonth() === n.getMonth() && d.getDate() === n.getDate();
}

function historyEntry(h, when) {
  const failed = h.status === "failed";
  const empty = h.status === "empty";
  const text = failed ? h.error || "No se pudo transcribir" : empty ? "(no se escuchó nada)" : h.text;
  const showRaw = historyView.open.has(h.id);
  const changed = h.rawText && h.rawText.trim() !== h.text.trim();

  const meta = el("div", { class: "meta" },
    h.app ? el("span", {}, appName(h.app)) : null,
    !failed && !empty ? el("span", {}, `${h.words} ${h.words === 1 ? "palabra" : "palabras"}`) : null,
    el("span", {}, seconds(h.duration)),
    h.totalMs ? el("span", {}, `respondió en ${(h.totalMs / 1000).toFixed(1).replace(".", ",")} s`) : null,
    h.mode === "handsfree" ? el("span", {}, "manos libres") : null,
    h.status === "raw" ? el("span", { class: "chip warn", title: h.error || "" }, "sin limpiar") : null);

  const actions = el("div", { class: "actions" },
    !failed && !empty ? el("button", { class: "btn ghost", title: "Copiar", onclick: async () => { await call("history.copy", { id: h.id }); toast("Copiado"); } }, icon("copy")) : null,
    changed && !failed ? el("button", { class: "btn ghost", title: "Ver lo que se escuchó, antes de limpiarlo", onclick: () => { showRaw ? historyView.open.delete(h.id) : historyView.open.add(h.id); refresh(); } }, icon("eye")) : null,
    h.audioPath ? el("button", { class: "btn ghost", title: "Transcribir de nuevo", onclick: (e) => retry(h.id, e.currentTarget) }, icon("retry")) : null,
    el("button", { class: "btn ghost", title: "Borrar", onclick: async () => { await call("history.delete", { id: h.id }); refresh(); } }, icon("trash")));

  return el("div", { class: "entry" + (failed ? " failed" : "") },
    el("div", { class: "time num" }, when.toLocaleTimeString("es-AR", { hour: "2-digit", minute: "2-digit", hour12: false })),
    el("div", {}, el("div", { class: "text" + (empty ? " muted" : "") }, text), showRaw ? el("div", { class: "raw" }, h.rawText) : null, meta),
    actions);
}

async function retry(id, button) {
  button.disabled = true;
  try {
    await call("history.retry", { id });
    toast("Transcripto de nuevo");
  } catch {
    /* the error toast is already on screen */
  }
  refresh();
}

async function clearHistory() {
  if (!confirm("¿Borrar todo el historial? No se puede deshacer, y las estadísticas vuelven a cero.")) return;
  await call("history.clear");
  toast("Historial borrado");
  refresh();
}

/* ---------- Diccionario ---------- */

let editingWord = null;

async function renderDictionary() {
  const items = await call("dict.list");
  const editing = items.find((d) => d.id === editingWord) || null;

  const phrase = el("input", { type: "text", placeholder: "Vercel", value: editing ? editing.phrase : "" });
  const heard = el("input", { type: "text", placeholder: "bercel, versel", value: editing ? editing.replaceFrom || "" : "" });
  const save = async () => {
    if (!phrase.value.trim()) return phrase.focus();
    await call("dict.save", { id: editing ? editing.id : 0, phrase: phrase.value, replaceFrom: heard.value, starred: editing ? editing.starred : false });
    editingWord = null;
    toast("Guardado");
    refresh();
  };
  [phrase, heard].forEach((i) => i.addEventListener("keydown", (e) => e.key === "Enter" && save()));

  const form = el("div", { class: "card" },
    el("div", { class: "form-row two" },
      el("label", { class: "field" }, el("span", {}, "Palabra o nombre, escrito como lo querés"), phrase),
      el("label", { class: "field" }, el("span", {}, "Si suele escucharse mal, cómo sale (opcional)"), heard),
      el("div", { class: "inline" },
        el("button", { class: "btn primary", onclick: save }, editing ? "Guardar" : "Agregar"),
        editing ? el("button", { class: "btn", onclick: () => { editingWord = null; refresh(); } }, "Cancelar") : null)));

  const list = items.length
    ? el("div", { class: "card" }, el("table", { class: "table" }, items.map((d) =>
        el("tr", {},
          el("td", { style: "width:26px" }, el("button", { class: "star" + (d.starred ? " on" : ""), title: "Destacar", onclick: async () => { await call("dict.save", { id: d.id, phrase: d.phrase, replaceFrom: d.replaceFrom || "", starred: !d.starred }); refresh(); } }, "★")),
          el("td", {}, el("div", { class: "word" }, d.phrase), d.replaceFrom ? el("div", { class: "sub" }, `reemplaza a: ${d.replaceFrom}`) : null),
          el("td", {}, el("span", { class: "acts" },
            el("button", { class: "btn ghost", title: "Editar", onclick: () => { editingWord = d.id; refresh(); } }, icon("edit")),
            el("button", { class: "btn ghost", title: "Borrar", onclick: async () => { await call("dict.delete", { id: d.id }); refresh(); } }, icon("trash"))))))))
    : el("div", { class: "empty" }, el("strong", {}, "Tu diccionario está vacío"),
        el("span", {}, "Agregá nombres propios, marcas y términos de tu trabajo para que se transcriban bien."));

  return [
    el("h1", {}, "Diccionario"),
    el("p", { class: "lead" }, "Palabras que el dictado tiene que escribir exactamente así. El autocorrector tampoco las va a tocar cuando las escribas con el teclado."),
    form, list,
  ];
}

/* ---------- Atajos de texto ---------- */

let editingSnippet = null;

async function renderSnippets() {
  const items = await call("snip.list");
  const editing = items.find((s) => s.id === editingSnippet) || null;

  const trigger = el("input", { type: "text", placeholder: "mi mail", value: editing ? editing.trigger : "" });
  const expansion = el("textarea", { placeholder: "nombre@ejemplo.com", rows: 1 });
  expansion.value = editing ? editing.expansion : "";
  const save = async () => {
    if (!trigger.value.trim() || !expansion.value) return (trigger.value.trim() ? expansion : trigger).focus();
    await call("snip.save", { id: editing ? editing.id : 0, trigger: trigger.value, expansion: expansion.value });
    editingSnippet = null;
    toast("Guardado");
    refresh();
  };

  const form = el("div", { class: "card" },
    el("div", { class: "form-row snip" },
      el("label", { class: "field" }, el("span", {}, "Cuando digas"), trigger),
      el("label", { class: "field" }, el("span", {}, "Escribir"), expansion),
      el("div", { class: "inline" },
        el("button", { class: "btn primary", onclick: save }, editing ? "Guardar" : "Agregar"),
        editing ? el("button", { class: "btn", onclick: () => { editingSnippet = null; refresh(); } }, "Cancelar") : null)));

  const list = items.length
    ? el("div", { class: "card" }, el("table", { class: "table" }, items.map((s) =>
        el("tr", {},
          el("td", { style: "width:32%" }, el("div", { class: "word" }, `“${s.trigger}”`)),
          el("td", {}, el("div", { class: "sub", style: "white-space:pre-wrap;user-select:text" }, s.expansion)),
          el("td", {}, el("span", { class: "acts" },
            el("button", { class: "btn ghost", title: "Editar", onclick: () => { editingSnippet = s.id; refresh(); } }, icon("edit")),
            el("button", { class: "btn ghost", title: "Borrar", onclick: async () => { await call("snip.delete", { id: s.id }); refresh(); } }, icon("trash"))))))))
    : el("div", { class: "empty" }, el("strong", {}, "Todavía no tenés atajos"),
        el("span", {}, "Ideal para tu mail, tu dirección, un link que pasás seguido o una respuesta que repetís."));

  return [
    el("h1", {}, "Atajos de texto"),
    el("p", { class: "lead" }, "Decís una frase corta al dictar y Libre Flow escribe el texto completo que guardaste."),
    form, list,
  ];
}

/* ---------- Autocorrector ---------- */

async function renderAutocorrect() {
  const data = await call("ac.get");
  const s = state.settings;

  const toggle = (key, title, help) =>
    el("div", { class: "setting" },
      el("div", { class: "txt" }, el("b", {}, title), el("span", {}, help)),
      el("div", { class: "ctl" }, el("input", { type: "checkbox", class: "switch", checked: s[key], onchange: (e) => setSettings({ [key]: e.target.checked }).then(refresh) })));

  const options = el("div", { class: "card" },
    toggle("autocorrectEnabled", "Autocorrector activado", "Corrige mientras escribís con el teclado, en cualquier programa. También se prende y apaga con Shift + Alt + A o tocando “Aa” en la barrita."),
    toggle("autocorrectTypos", "Errores de tipeo y espacios", "ohla → hola, hol acomo → hola como, porfavor → por favor."),
    toggle("autocorrectAccents", "Tildes que no admiten duda", "tambien → también, dia → día. No toca palabras como “como” o “esta”, que existen con y sin tilde."),
    toggle("autocorrectCapitalize", "Mayúscula al empezar una oración", "Después de un punto, de un signo de pregunta o de un Enter."),
    toggle("autocorrectOnEnter", "Corregir también al apretar Enter", "Solo si la línea tiene más de una palabra, para no tocar contraseñas ni comandos."));

  const appInput = el("input", { type: "text", placeholder: "Nombre del programa, por ejemplo: code" });
  const addApp = async () => {
    const name = appInput.value.trim().toLowerCase().replace(/\.exe$/, "");
    if (!name) return;
    await setSettings({ autocorrectExcludedApps: [...new Set([...s.autocorrectExcludedApps, name])] });
    refresh();
  };
  appInput.addEventListener("keydown", (e) => e.key === "Enter" && addApp());
  const excluded = el("div", { class: "card" },
    el("h2", {}, "Programas donde no corregir"),
    el("p", { class: "muted small", style: "margin:-6px 0 14px" }, "Por defecto corrige en todos. Si en algún programa te molesta (un editor de código, una terminal), sumalo acá."),
    s.autocorrectExcludedApps.length
      ? el("div", { class: "chips", style: "margin-bottom:14px" }, s.autocorrectExcludedApps.map((a) =>
          el("span", { class: "chip" }, a, el("button", { title: "Quitar", onclick: async () => { await setSettings({ autocorrectExcludedApps: s.autocorrectExcludedApps.filter((x) => x !== a) }); refresh(); } }, "×"))))
      : null,
    el("div", { class: "inline" }, appInput, el("button", { class: "btn", onclick: addApp }, "Agregar")));

  const wordInput = el("input", { type: "text", placeholder: "Una palabra que no querés que corrija" });
  const addWord = async () => {
    if (!wordInput.value.trim()) return;
    await call("ac.learned.add", { word: wordInput.value });
    refresh();
  };
  wordInput.addEventListener("keydown", (e) => e.key === "Enter" && addWord());
  const learned = el("div", { class: "card" },
    el("h2", {}, "Palabras aprendidas"),
    el("p", { class: "muted small", style: "margin:-6px 0 14px" }, "Si apretás Backspace justo después de una corrección, se deshace. A la segunda vez con la misma palabra, la aprende y no la vuelve a tocar."),
    data.learned.length
      ? el("div", { class: "chips", style: "margin-bottom:14px" }, data.learned.map((w) =>
          el("span", { class: "chip good" }, w, el("button", { title: "Olvidar", onclick: async () => { await call("ac.learned.remove", { word: w }); refresh(); } }, "×"))))
      : null,
    el("div", { class: "inline" }, wordInput, el("button", { class: "btn", onclick: addWord }, "Agregar")));

  const recent = el("div", { class: "card" },
    el("div", { class: "head", style: "margin-bottom:6px" },
      el("h2", { style: "margin:0" }, "Últimas correcciones"),
      data.recent.length ? el("button", { class: "btn ghost", onclick: async () => { await call("ac.clear"); refresh(); } }, "Borrar registro") : null),
    data.recent.length
      ? el("div", {}, data.recent.slice(0, 25).map((r) =>
          el("div", { class: "pair" },
            el("span", {}, el("span", { class: "from" }, r.original), el("span", { class: "arrow" }, "→"), el("span", { class: "to" }, r.corrected),
              r.undone ? el("span", { class: "chip warn", style: "margin-left:10px" }, "deshecha") : null),
            el("span", { class: "muted small" }, `${appName(r.app)} · ${new Date(r.timestamp).toLocaleTimeString("es-AR", { hour: "2-digit", minute: "2-digit", hour12: false })}`))))
      : el("div", { class: "muted small" }, "Todavía no corrigió nada. Probá escribir “ohla” y apretar espacio."));

  return [
    el("h1", {}, "Autocorrector"),
    el("p", { class: "lead" }, state.lexiconReady
      ? "Funciona en esta computadora, sin internet: lo que escribís con el teclado nunca sale de acá."
      : "Cargando los diccionarios… en unos segundos empieza a corregir."),
    options, excluded, learned, recent,
  ];
}

/* ---------- Ajustes ---------- */

let engineStatus = { text: "", kind: "" };
let knownModels = [];

async function renderSettings() {
  const s = state.settings;

  const row = (title, help, control) =>
    el("div", { class: "setting" }, el("div", { class: "txt" }, el("b", {}, title), help ? el("span", {}, help) : null), el("div", { class: "ctl" }, control));
  const sw = (key) => el("input", { type: "checkbox", class: "switch", checked: s[key], onchange: (e) => setSettings({ [key]: e.target.checked }) });
  const select = (key, options) => {
    const node = el("select", { onchange: (e) => setSettings({ [key]: e.target.value }).then(refresh) },
      options.map(([value, label]) => el("option", { value }, label)));
    node.value = s[key];
    return node;
  };

  /* engine */
  const seg = el("div", { class: "seg" }, [["groq", "Groq"], ["openai", "OpenAI"], ["custom", "Otro"]].map(([id, label]) =>
    el("button", { class: s.provider === id ? "on" : "", onclick: async () => { engineStatus = { text: "", kind: "" }; knownModels = []; await setSettings({ provider: id }); refresh(); } }, label)));

  const keyInput = el("input", { type: "password", autocomplete: "off", spellcheck: "false", placeholder: state.hasKey ? "Clave guardada · pegá otra para cambiarla" : "Pegá tu clave acá" });
  const status = el("div", { class: "status " + engineStatus.kind }, engineStatus.text);
  const test = async () => {
    status.className = "status";
    status.textContent = "Probando la conexión…";
    try {
      const { models } = await bridge.call("engine.test");
      knownModels = models;
      engineStatus = { text: `Conexión correcta. El servicio ofrece ${models.length} modelos.`, kind: "ok" };
    } catch (err) {
      engineStatus = { text: err.message, kind: "err" };
    }
    refresh();
  };
  const saveKey = async () => {
    if (!keyInput.value.trim()) return keyInput.focus();
    applyState(await call("settings.setApiKey", { key: keyInput.value }));
    keyInput.value = "";
    await test();
  };
  keyInput.addEventListener("keydown", (e) => e.key === "Enter" && saveKey());

  const steps = s.provider === "groq"
    ? el("ol", { class: "steps" },
        el("li", {}, "Entrá a ", el("a", { href: "https://console.groq.com/keys", target: "_blank" }, "console.groq.com/keys"), " y creá una cuenta gratis."),
        el("li", {}, "Tocá “Create API Key” y copiá la clave."),
        el("li", {}, "Pegala acá abajo. Se guarda cifrada solo en esta computadora."))
    : s.provider === "openai"
      ? el("ol", { class: "steps" },
          el("li", {}, "Entrá a ", el("a", { href: "https://platform.openai.com/api-keys", target: "_blank" }, "platform.openai.com/api-keys"), " (necesita saldo cargado)."),
          el("li", {}, "Creá una clave y pegala acá abajo."))
      : el("p", { class: "muted small", style: "margin:0 0 16px" }, "Cualquier servicio con una API compatible con la de OpenAI.");

  const modelField = (key, label, hint) => {
    const listId = "models-" + key;
    const input = el("input", { type: "text", value: s[key], list: listId, spellcheck: "false", onchange: (e) => setSettings({ [key]: e.target.value.trim() }) });
    return el("label", { class: "field" }, el("span", {}, label), input, el("datalist", { id: listId }, knownModels.map((m) => el("option", { value: m }))),
      hint ? el("span", { class: "small", style: "color:var(--faint)" }, hint) : null);
  };

  const engine = el("div", { class: "card" },
    el("div", { class: "head", style: "margin-bottom:14px" }, el("h2", { style: "margin:0" }, "Motor de transcripción"), seg),
    steps,
    el("div", { class: "inline" }, keyInput,
      el("button", { class: "btn primary", onclick: saveKey }, "Guardar clave"),
      state.hasKey ? el("button", { class: "btn", onclick: test }, "Probar conexión") : null),
    status,
    s.provider === "custom"
      ? el("label", { class: "field", style: "margin-top:8px" }, el("span", {}, "Dirección de la API"),
          el("input", { type: "text", value: s.baseUrl, spellcheck: "false", onchange: (e) => setSettings({ baseUrl: e.target.value.trim() }) }))
      : null,
    el("div", { class: "grid", style: "grid-template-columns:1fr 1fr;margin-top:14px" },
      modelField("asrModel", "Modelo que transcribe la voz", s.provider === "groq" ? "whisper-large-v3 es el más preciso; whisper-large-v3-turbo, el más rápido." : null),
      modelField("llmModel", "Modelo que limpia el texto", "Tocá “Probar conexión” para ver la lista disponible.")));

  const dictation = el("div", { class: "card" },
    el("h2", {}, "Dictado"),
    row("Idioma", "En automático detecta español o inglés en cada dictado.", select("language", [["auto", "Automático (español e inglés)"], ["es", "Siempre español"], ["en", "Siempre inglés"]])),
    row("Limpiar el texto con IA", "Quita muletillas y repeticiones, ordena la puntuación y respeta cuando te corregís al hablar. Apagado, pega lo que se escuchó tal cual.", sw("cleanup")),
    row("Adaptar el tono al programa", "Más relajado en chats, más prolijo en mails, literal en herramientas técnicas.", sw("toneByApp")),
    row("Micrófono", null, (() => {
      const node = el("select", { onchange: (e) => setSettings({ micDevice: e.target.value }) },
        el("option", { value: "" }, "El predeterminado de Windows"), state.mics.map((m) => el("option", { value: m }, m)));
      node.value = s.micDevice;
      return node;
    })()),
    row("Sonidos al empezar y terminar", null, sw("sounds")),
    row("Silenciar la compu mientras dictás", "Baja la música o el video para que no se cuele en el micrófono, y lo devuelve al terminar.", sw("muteWhileDictating")));

  const paste = el("div", { class: "card" },
    el("h2", {}, "Cómo se escribe el texto"),
    row("Método", "Pegar es instantáneo. Tipear sirve para los pocos programas que bloquean el pegado.", select("pasteMethod", [["clipboard", "Pegar (recomendado)"], ["type", "Tipear letra por letra"]])),
    row("Devolver lo que tenías copiado", "Después de pegar el dictado, el portapapeles vuelve a tener lo que habías copiado antes.", sw("restoreClipboard")),
    row("Agregar un espacio al final", "Así el próximo dictado no queda pegado al anterior.", sw("trailingSpace")));

  const wpm = el("input", { type: "number", min: 10, max: 200, value: s.typingWpm, onchange: (e) => setSettings({ typingWpm: Number(e.target.value) || 40 }) });
  const general = el("div", { class: "card" },
    el("h2", {}, "General"),
    row("Iniciar con Windows", "Arranca solo, mostrando únicamente la barrita y el ícono junto al reloj.",
      el("input", { type: "checkbox", class: "switch", checked: state.autostart, onchange: async (e) => applyState(await call("autostart.set", { enabled: e.target.checked })) })),
    row("Mostrar la barrita flotante", "El logo chiquito abajo en el centro de la pantalla.", sw("showBar")),
    row("Tu velocidad al tipear", "En palabras por minuto. Se usa para calcular el tiempo ahorrado; el promedio de una persona es 40.", wpm));

  const keys = el("div", { class: "card" },
    el("h2", {}, "Atajos de teclado"),
    shortcut(["Ctrl", "Win"], "Mantener apretado para dictar; soltar para escribir"),
    shortcut(["Ctrl", "Win"], "Dos toques rápidos: manos libres. Otro Ctrl + Win termina", "×2"),
    shortcut(["Esc"], "Cancelar el dictado"),
    shortcut(["Shift", "Alt", "Z"], "Pegar el último dictado"),
    shortcut(["Shift", "Alt", "A"], "Prender o apagar el autocorrector"),
    shortcut(["Backspace"], "Justo después de una corrección: deshacerla"));

  const data = el("div", { class: "card" },
    el("h2", {}, "Datos"),
    row("Carpeta de datos", state.dataDir, el("button", { class: "btn", onclick: () => call("app.openData") }, "Abrir carpeta")),
    row("Cerrar Libre Flow", "Deja de dictar y de corregir hasta que lo abras de nuevo.", el("button", { class: "btn danger", onclick: () => call("app.quit") }, "Salir")));

  return [
    el("h1", {}, "Ajustes"),
    el("p", { class: "lead" }, "Los cambios se guardan solos."),
    engine, dictation, paste, general, keys, data,
  ];
}

function shortcut(keys, text, suffix) {
  return el("div", { class: "setting" },
    el("div", { class: "txt" }, el("b", {}, text)),
    el("div", { class: "ctl inline", style: "gap:6px" }, keys.flatMap((k, idx) => (idx ? ["+", el("kbd", {}, k)] : [el("kbd", {}, k)])), suffix ? el("span", { class: "muted small" }, suffix) : null));
}

/* ---------- start ---------- */

document.getElementById("acQuick").addEventListener("change", async (e) => {
  await setSettings({ autocorrectEnabled: e.target.checked });
  refreshSoon(["autocorrector"]);
});
document.querySelector("[data-go]").addEventListener("click", () => go("ajustes"));

bridge.on("navigate", (d) => go(d.page));
bridge.on("history.changed", () => refreshSoon(["inicio", "historial"]));
bridge.on("corrections.changed", () => refreshSoon(["inicio", "autocorrector"]));
bridge.on("settings.changed", async () => {
  await loadState();
  refreshSoon(["autocorrector", "ajustes"]);
});

(async () => {
  await loadState();
  buildNav();
  await refresh();
  bridge.call("ui.ready").catch(() => {});
})();
