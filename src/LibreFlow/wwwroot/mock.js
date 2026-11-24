"use strict";
// Sample data used only when the panel is opened in a normal browser (design preview).
// Inside the app the real bridge answers and this file does nothing.
(() => {
  if (window.chrome && window.chrome.webview) return;

  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const daily = [];
  for (let k = 181; k >= 0; k--) {
    const d = new Date(today);
    d.setDate(d.getDate() - k);
    const weekend = d.getDay() === 0 || d.getDay() === 6;
    const seed = Math.abs(Math.sin(k * 12.9898) * 43758.5453) % 1;
    daily.push({ date: d.toISOString(), words: seed < (weekend ? 0.6 : 0.18) ? 0 : Math.round(seed * (weekend ? 300 : 900)) });
  }

  const settings = {
    provider: "groq", baseUrl: "https://api.groq.com/openai/v1", asrModel: "whisper-large-v3", llmModel: "openai/gpt-oss-120b",
    language: "auto", cleanup: true, toneByApp: true, micDevice: "", sounds: true, muteWhileDictating: true, pasteMethod: "clipboard",
    restoreClipboard: true, trailingSpace: true, showBar: true, typingWpm: 40, autocorrectEnabled: true, autocorrectTypos: true,
    autocorrectAccents: true, autocorrectCapitalize: true, autocorrectOnEnter: true, autocorrectExcludedApps: ["code"], onboardingDone: false,
  };
  const st = { hasKey: new URLSearchParams(location.search).get("key") !== "0", autostart: true };
  const state = () => ({ settings, hasKey: st.hasKey, autostart: st.autostart, version: "1.0.0", mics: ["Micrófono (Realtek Audio)", "Auriculares (AirPods)"],
    presets: [], lexiconReady: true, dataDir: "C:\Users\tu-usuario\AppData\Roaming\LibreFlow" });

  const at = (minutesAgo) => new Date(Date.now() - minutesAgo * 60000).toISOString();
  let history = [
    { id: 9, timestamp: at(4), text: "Dale, mañana te mando el presupuesto así lo revisás tranquilo.", rawText: "eh dale mañana te mando el este el presupuesto así lo revisás tranquilo", words: 10, duration: 5.2, app: "whatsapp", mode: "ptt", status: "ok", totalMs: 840, audioPath: "x" },
    { id: 8, timestamp: at(38), text: "Necesito que el reporte incluya tres cosas:\n1. Las ventas del mes\n2. Los leads nuevos\n3. El costo por lead", rawText: "necesito que el reporte incluya tres cosas primero las ventas del mes segundo los leads nuevos y tercero el costo por lead", words: 20, duration: 9.8, app: "chrome", mode: "handsfree", status: "ok", totalMs: 1210, audioPath: "x" },
    { id: 7, timestamp: at(95), text: "No hay conexión con el servicio de transcripción.", rawText: "", words: 0, duration: 6.1, app: "code", mode: "ptt", status: "failed", error: "No hay conexión con el servicio de transcripción.", totalMs: 0, audioPath: "x" },
    { id: 6, timestamp: at(60 * 26), text: "Agendemos la reunión para el jueves a las 15.", rawText: "agendemos la reunión para el jueves a las tres de la tarde", words: 9, duration: 4.0, app: "outlook", mode: "ptt", status: "raw", error: "No se pudo limpiar el texto", totalMs: 610, audioPath: null },
    { id: 5, timestamp: at(60 * 27), text: "Perfecto, gracias.", rawText: "perfecto gracias", words: 2, duration: 1.4, app: "slack", mode: "ptt", status: "ok", totalMs: 520, audioPath: null },
  ];
  let dict = [
    { id: 1, phrase: "Vercel", replaceFrom: "bercel, versel", starred: true, source: "manual" },
    { id: 2, phrase: "n8n", replaceFrom: "n ocho n, ene ocho ene", starred: false, source: "manual" },
    { id: 3, phrase: "Supabase", replaceFrom: null, starred: false, source: "manual" },
  ];
  let snips = [
    { id: 1, trigger: "mi mail", expansion: "nombre@ejemplo.com" },
    { id: 2, trigger: "link de la reunión", expansion: "https://meet.ejemplo.com/mi-sala" },
  ];
  let learned = ["groq", "kubectl"];
  const corrections = {
    total: 1284, today: 37, thisWeek: 212, undone: 9,
    top: [["qeu", "que", 96], ["tambien", "también", 71], ["ohla", "hola", 40], ["asi", "así", 38], ["pro", "por", 22]].map(([original, corrected, count]) => ({ original, corrected, count })),
  };
  const recent = [["ohla", "hola", "whatsapp", 2, false], ["hol acomo", "hola como", "whatsapp", 2, false], ["tambien", "también", "chrome", 14, false], ["claidad", "calidad", "claude", 40, false], ["kubectl", "kubect", "chrome", 55, true]]
    .map(([original, corrected, app, m, undone], k) => ({ id: k, timestamp: at(m), original, corrected, kind: "Typo", app, undone }));

  window.mockCall = async (method, p, emit) => {
    switch (method) {
      case "state.get": return state();
      case "settings.set": Object.assign(settings, p.patch); return state();
      case "settings.setApiKey": st.hasKey = !!p.key; return state();
      case "autostart.set": st.autostart = p.enabled; return state();
      case "engine.test": return { models: ["whisper-large-v3", "whisper-large-v3-turbo", "openai/gpt-oss-120b", "openai/gpt-oss-20b", "llama-3.3-70b-versatile"] };
      case "insights.get": {
        const totalWords = daily.reduce((a, d) => a + d.words, 0);
        return {
          typingWpm: settings.typingWpm, corrections,
          insights: { totalWords, totalSeconds: totalWords / 2.1, dictations: 2140, averageWpm: 126, minutesSaved: totalWords / settings.typingWpm - totalWords / 126,
            minutesSavedThisWeek: 74, dayStreak: 6, weekStreak: 19, wordsToday: 640, wordsThisWeek: 4310, daily,
            topApps: [["chrome", 21400], ["whatsapp", 15200], ["claude", 9800], ["code", 4100], ["slack", 2300], ["outlook", 900]].map(([app, words]) => ({ app, words, count: 10 })) },
        };
      }
      case "history.list": {
        const q = (p.q || "").toLowerCase();
        const items = history.filter((h) => !q || h.text.toLowerCase().includes(q));
        return { items, total: items.length };
      }
      case "history.delete": history = history.filter((h) => h.id !== p.id); return null;
      case "history.clear": history = []; return null;
      case "history.copy": return true;
      case "history.retry": return history[0];
      case "dict.list": return dict;
      case "dict.save": if (p.id) dict = dict.map((d) => (d.id === p.id ? { ...d, ...p } : d)); else dict.push({ id: Date.now(), source: "manual", ...p }); return dict;
      case "dict.delete": dict = dict.filter((d) => d.id !== p.id); return dict;
      case "snip.list": return snips;
      case "snip.save": if (p.id) snips = snips.map((s) => (s.id === p.id ? { ...s, ...p } : s)); else snips.push({ ...p, id: Date.now() }); return snips;
      case "snip.delete": snips = snips.filter((s) => s.id !== p.id); return snips;
      case "ac.get": return { recent, learned, stats: corrections };
      case "ac.learned.add": learned = [...new Set([...learned, p.word.toLowerCase()])]; return learned;
      case "ac.learned.remove": learned = learned.filter((w) => w !== p.word); return learned;
      default: return null;
    }
  };
})();
