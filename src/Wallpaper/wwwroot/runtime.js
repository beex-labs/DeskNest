/* BeeX DeskNest wallpaper runtime SDK.
 * Injected into every wallpaper page (built-in, scene, or imported web wallpaper).
 * Exposes window.BeeXWallpaper and a Wallpaper Engine HTML compatibility shim
 * (wallpaperPropertyListener / wallpaperRegisterAudioListener) so many WE web
 * wallpapers run unmodified. The host posts JSON messages: fps / pause / resume /
 * audio / pointer / monitor / props. The rAF loop is gated to the target fps so
 * the governor's frame cap actually saves power.
 */
(function () {
  if (window.BeeXWallpaper) return; // idempotent (page may include it AND host injects it)

  const cbs = { time: [], audio: [], pointer: [], resize: [], pause: [], resume: [], property: [], volume: [], mute: [] };
  let fps = 60, paused = false, t = 0, last = -1;

  const api = {
    audio: { bands: new Float32Array(64), beat: false, level: 0 },
    pointer: { x: 0.5, y: 0.5, down: false },
    monitor: { width: innerWidth, height: innerHeight, dpi: 1 },
    volume: 1,
    muted: false,
    get fps() { return fps; },
    get paused() { return paused; },
    onTime(f) { cbs.time.push(f); },
    onAudio(f) { cbs.audio.push(f); },
    onPointer(f) { cbs.pointer.push(f); },
    onResize(f) { cbs.resize.push(f); },
    onPause(f) { cbs.pause.push(f); },
    onResume(f) { cbs.resume.push(f); },
    onProperty(f) { cbs.property.push(f); },
    onVolume(f) { cbs.volume.push(f); },
    onMuted(f) { cbs.mute.push(f); },
  };
  window.BeeXWallpaper = api;

  // ---- Wallpaper Engine HTML shim -----------------------------------------
  let weAudioListeners = [];
  window.wallpaperRegisterAudioListener = function (f) { if (typeof f === 'function') weAudioListeners.push(f); };
  // window.wallpaperPropertyListener is assigned by WE wallpapers themselves; we just read it.

  // ---- fps-gated rAF loop ---------------------------------------------------
  function loop(now) {
    requestAnimationFrame(loop);
    if (paused || fps <= 0) { last = now; return; }
    if (last < 0) last = now;
    const minMs = 1000 / Math.max(1, Math.min(fps, 240));
    if (now - last < minMs - 0.5) return;
    const dt = Math.min((now - last) / 1000, 0.25);
    last = now;
    t += dt;
    for (const f of cbs.time) { try { f(dt, t); } catch (e) { } }
  }
  requestAnimationFrame(loop);

  // ---- host message pump ----------------------------------------------------
  function dispatchProps(map) {
    for (const k in map) for (const f of cbs.property) { try { f(k, map[k]); } catch (e) { } }
    try {
      const l = window.wallpaperPropertyListener;
      if (l && typeof l.applyUserProperties === 'function') {
        const weProps = {};
        for (const k in map) weProps[k] = { value: map[k] };
        l.applyUserProperties(weProps);
      }
    } catch (e) { }
  }

  window.chrome && chrome.webview && chrome.webview.addEventListener('message', function (e) {
    const m = e.data || {};
    switch (m.type) {
      case 'fps': fps = m.value | 0; break;
      case 'pause':
        if (!paused) { paused = true; for (const f of cbs.pause) { try { f(); } catch (e2) { } } }
        break;
      case 'resume':
        if (paused) { paused = false; last = -1; for (const f of cbs.resume) { try { f(); } catch (e2) { } } }
        break;
      case 'audio': {
        if (m.bands && m.bands.length) api.audio.bands.set(m.bands);
        api.audio.beat = !!m.beat;
        api.audio.level = +m.level || 0;
        for (const f of cbs.audio) { try { f(api.audio.bands, api.audio.beat, api.audio.level); } catch (e2) { } }
        if (weAudioListeners.length) {
          // WE delivers 128 values (64 left + 64 right); duplicate our mono bands.
          const we = new Array(128);
          for (let i = 0; i < 64; i++) { we[i] = api.audio.bands[i]; we[64 + i] = api.audio.bands[i]; }
          for (const f of weAudioListeners) { try { f(we); } catch (e2) { } }
        }
        break;
      }
      case 'pointer':
        api.pointer = { x: +m.x || 0, y: +m.y || 0, down: !!m.down };
        for (const f of cbs.pointer) { try { f(api.pointer.x, api.pointer.y, api.pointer.down); } catch (e2) { } }
        break;
      case 'monitor':
        api.monitor = { width: m.width | 0, height: m.height | 0, dpi: +m.dpi || 1 };
        for (const f of cbs.resize) { try { f(api.monitor.width, api.monitor.height, api.monitor.dpi); } catch (e2) { } }
        break;
      case 'volume':
        api.volume = Math.min(1, Math.max(0, +m.value || 0));
        for (const f of cbs.volume) { try { f(api.volume); } catch (e2) { } }
        break;
      case 'mute':
        api.muted = !!m.value;
        for (const f of cbs.mute) { try { f(api.muted); } catch (e2) { } }
        break;
      case 'props': if (m.map) dispatchProps(m.map); break;
    }
  });

  addEventListener('resize', function () {
    for (const f of cbs.resize) { try { f(innerWidth, innerHeight, api.monitor.dpi); } catch (e) { } }
  });

  function announce() { try { chrome.webview.postMessage({ type: 'ready' }); } catch (e) { } }
  if (document.readyState === 'complete' || document.readyState === 'interactive') announce();
  else addEventListener('DOMContentLoaded', announce);
})();
