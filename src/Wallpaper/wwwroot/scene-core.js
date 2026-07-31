/* Shared scene renderer used by both the wallpaper runtime (scene.html) and the editor preview
 * (editor.html). A scene is JSON: { name, background, layers:[ {type:image|video|text|particles,
 * src|text, x,y (0-1 centre), scale, opacity, rotate, drift, parallax, color, fontSize,
 * audio:{target:scale|opacity|glow, band:low|mid|high|level|beat, amount} } ] }.
 * Audio bindings and pointer parallax are driven by the BeeXWallpaper SDK when present.
 */
(function () {
  function bandValue(audio, band) {
    const b = audio.bands;
    let s = 0;
    switch (band) {
      case 'low': for (let i = 0; i < 16; i++) s += b[i]; return s / 16;
      case 'mid': for (let i = 16; i < 44; i++) s += b[i]; return s / 28;
      case 'high': for (let i = 44; i < 64; i++) s += b[i]; return s / 20;
      case 'beat': return audio.beat ? 1 : 0;
      default: return audio.level;
    }
  }

  function makeParticles(canvas, layer) {
    const ctx = canvas.getContext('2d');
    const n = Math.max(10, Math.min(600, layer.count || 120));
    const col = layer.color || '#ffb347';
    const ps = [];
    for (let i = 0; i < n; i++)
      ps.push({ x: Math.random(), y: Math.random(), s: .5 + Math.random() * 2, v: .01 + Math.random() * .05, ph: Math.random() * 6.28 });
    return function step(dt, t, energy) {
      const w = canvas.width = canvas.clientWidth, h = canvas.height = canvas.clientHeight;
      ctx.clearRect(0, 0, w, h);
      ctx.fillStyle = col;
      for (const p of ps) {
        p.y -= p.v * dt * (1 + energy * 3);
        if (p.y < -.02) { p.y = 1.02; p.x = Math.random(); }
        const x = p.x + Math.sin(t * .6 + p.ph) * .01;
        ctx.globalAlpha = .3 + energy * .7;
        ctx.beginPath();
        ctx.arc(x * w, p.y * h, p.s * (1 + energy * 2), 0, 6.2832);
        ctx.fill();
      }
      ctx.globalAlpha = 1;
    };
  }

  window.SceneCore = {
    /** Renders scene into container (position:relative). Returns { dispose() }. */
    render(container, scene) {
      container.innerHTML = '';
      container.style.background = scene.background || '#0d1321';
      const W = window.BeeXWallpaper;
      const items = [];

      for (const layer of scene.layers || []) {
        let el, particleStep = null;
        if (layer.type === 'image') { el = document.createElement('img'); el.src = layer.src || ''; el.draggable = false; }
        else if (layer.type === 'video') {
          el = document.createElement('video');
          el.src = layer.src || ''; el.muted = true; el.loop = true; el.autoplay = true; el.playsInline = true;
        }
        else if (layer.type === 'text') {
          el = document.createElement('div');
          el.textContent = layer.text || 'Text';
          el.style.color = layer.color || '#ffffff';
          el.style.font = `600 ${layer.fontSize || 48}px 'Segoe UI',system-ui,sans-serif`;
          el.style.whiteSpace = 'pre-wrap';
          el.style.textShadow = '0 2px 18px rgba(0,0,0,.45)';
        }
        else if (layer.type === 'particles') {
          el = document.createElement('canvas');
          el.style.width = '100%'; el.style.height = '100%';
          particleStep = makeParticles(el, layer);
        }
        else continue;

        el.style.position = 'absolute';
        el.style.pointerEvents = 'none';
        if (layer.type !== 'particles') {
          el.style.left = ((layer.x ?? .5) * 100) + '%';
          el.style.top = ((layer.y ?? .5) * 100) + '%';
          el.style.maxWidth = 'none';
        } else { el.style.left = '0'; el.style.top = '0'; }
        container.appendChild(el);
        items.push({ layer, el, particleStep, glow: 0 });
      }

      function apply(t) {
        const audio = W ? W.audio : { bands: new Float32Array(64), beat: false, level: 0 };
        const ptr = W ? W.pointer : { x: .5, y: .5 };
        for (const it of items) {
          const L = it.layer;
          let scale = L.scale ?? 1, opacity = L.opacity ?? 1, glow = 0;
          if (L.audio && L.audio.target) {
            const v = bandValue(audio, L.audio.band || 'low') * (L.audio.amount ?? .3);
            if (L.audio.target === 'scale') scale *= 1 + v;
            else if (L.audio.target === 'opacity') opacity = Math.max(0, Math.min(1, opacity - (L.audio.amount ?? .3) + v * 2));
            else glow = v * 60;
          }
          if (it.particleStep) { it.el.style.opacity = opacity; continue; }
          const dx = (ptr.x - .5) * (L.parallax || 0) * 80 + Math.sin(t * .5) * (L.drift || 0) * 30;
          const dy = (ptr.y - .5) * (L.parallax || 0) * 80;
          it.el.style.transform = `translate(-50%,-50%) translate(${dx}px,${dy}px) rotate(${L.rotate || 0}deg) scale(${scale})`;
          it.el.style.opacity = opacity;
          it.el.style.filter = glow > 1 ? `drop-shadow(0 0 ${glow}px rgba(255,190,80,.85))` : '';
        }
      }

      let disposed = false;
      if (W) {
        W.onTime((dt, t) => {
          if (disposed) return;
          apply(t);
          for (const it of items)
            if (it.particleStep) {
              const e = it.layer.audio ? bandValue(W.audio, it.layer.audio.band || 'low') : W.audio.level;
              it.particleStep(dt, t, e);
            }
        });
        W.onPause(() => items.forEach(it => { if (it.el.tagName === 'VIDEO') { try { it.el.pause(); } catch (e) { } } }));
        W.onResume(() => items.forEach(it => { if (it.el.tagName === 'VIDEO') { try { it.el.play(); } catch (e) { } } }));
      }
      apply(0);
      return { dispose() { disposed = true; container.innerHTML = ''; } };
    }
  };
})();
