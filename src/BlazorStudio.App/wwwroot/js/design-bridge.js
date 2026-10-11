// Blazor Studio – Design-Brücke. Läuft in der echten App (Schatten-Kopie) im Designer-iframe.
// Zuordnung: HTML-Elemente tragen data-bs-id, Komponenten liegen zwischen <template data-bs-s/-e>.
// Kommunikation mit dem Designer ausschließlich über postMessage.
(() => {
  'use strict';
  if (window.__bsDesign || window.parent === window) return;
  window.__bsDesign = true;

  const cfg = { prefix: '', interact: false, selected: null, label: '' };
  const post = (m) => window.parent.postMessage(Object.assign({ __bs: 1 }, m), '*');

  // ---------- Overlay in eigenem Shadow-DOM (App-CSS beeinflusst es nicht) ----------
  const host = document.createElement('bs-design-overlay');
  host.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:2147483647;';
  const root = host.attachShadow({ mode: 'open' });
  root.innerHTML = `
    <style>
      .box{position:fixed;display:none;box-sizing:border-box;pointer-events:none}
      .hover{border:1px dashed #4c8dff;background:rgba(76,141,255,.06)}
      .hover span{position:absolute;left:-1px;top:-18px;font:600 10.5px/16px system-ui,sans-serif;color:#fff;background:#4c8dff;padding:0 5px;border-radius:3px 3px 0 0;white-space:nowrap}
      .sel{border:2px solid #4c8dff;box-shadow:0 0 0 1px rgba(255,255,255,.4)}
      .chip{position:absolute;left:-2px;top:-24px;display:flex;align-items:center;gap:1px;background:#4c8dff;border-radius:5px 5px 5px 0;padding:0 2px 0 7px;font:600 11px/22px system-ui,sans-serif;color:#fff;pointer-events:auto;white-space:nowrap}
      .sel.below .chip{top:auto;bottom:-24px;border-radius:0 5px 5px 5px}
      .chip button{all:unset;cursor:pointer;width:20px;height:20px;display:grid;place-items:center;border-radius:4px;font-size:12px}
      .chip button:hover{background:rgba(255,255,255,.22)}
      .h{position:absolute;width:6px;height:6px;background:#fff;border:1.5px solid #4c8dff;border-radius:2px}
      .tl{left:-4px;top:-4px}.tr{right:-4px;top:-4px}.bl{left:-4px;bottom:-4px}.br{right:-4px;bottom:-4px}
      .ind{background:#4c8dff;border-radius:2px;box-shadow:0 0 0 2px rgba(76,141,255,.25)}
      .ind.inside{background:rgba(76,141,255,.12);border:2px dashed #4c8dff}
      .ind span{position:absolute;left:4px;top:-17px;font:600 10px/14px system-ui;color:#fff;background:#4c8dff;padding:0 4px;border-radius:3px;white-space:nowrap}
      .ghost{position:fixed;left:0;top:0;padding:4px 10px;border-radius:7px;background:#1e1e22;color:#eee;border:1px solid #4c8dff;font:600 12px system-ui;display:none}
      .slots{position:fixed;display:none;gap:3px;align-items:center;padding:3px;border-radius:7px;background:#1e1e22;border:1px solid #4c8dff;box-shadow:0 4px 14px rgba(0,0,0,.35);font:600 11px/18px system-ui,sans-serif}
      .slots b{color:#9fb7ff;padding:0 4px 0 3px;font-weight:600}
      .slots i{font-style:normal;color:#ddd;padding:1px 8px;border-radius:5px;border:1px dashed #4c8dff88;white-space:nowrap}
      .slots i.on{background:#4c8dff;color:#fff;border-style:solid}
      .slots i.missing{opacity:.75}
    </style>
    <div class="slots"></div>
    <div class="box hover"><span></span></div>
    <div class="box sel"><div class="chip"><b class="lbl"></b>
      <button data-act="parent" title="Übergeordnetes Element">&#8593;</button>
      <button data-act="duplicate" title="Duplizieren">&#10697;</button>
      <button data-act="delete" title="Löschen">&#10005;</button></div>
      <i class="h tl"></i><i class="h tr"></i><i class="h bl"></i><i class="h br"></i></div>
    <div class="box ind"><span></span></div>
    <div class="ghost"></div>`;
  const hoverBox = root.querySelector('.hover');
  const hoverLabel = hoverBox.querySelector('span');
  const selBox = root.querySelector('.sel');
  const selLabel = root.querySelector('.lbl');
  const ind = root.querySelector('.ind');
  const indLabel = ind.querySelector('span');
  const ghost = root.querySelector('.ghost');
  const slotBar = root.querySelector('.slots');

  // Leere Container sichtbar machen (sonst 0 px hoch und nicht zu treffen)
  const pageStyle = document.createElement('style');
  pageStyle.id = 'bs-design-style';
  pageStyle.textContent = `html:not(.bs-interact) [data-bs-empty]{min-height:46px!important;min-width:90px;outline:1.5px dashed rgba(76,141,255,.55)!important;outline-offset:-3px;background-color:rgba(76,141,255,.04)!important;position:relative}
    html:not(.bs-interact) [data-bs-empty]::after{content:attr(data-bs-empty);position:absolute;inset:0;display:grid;place-items:center;font:500 11px system-ui,sans-serif;color:rgba(76,141,255,.85);pointer-events:none;white-space:nowrap}`;
  const mountStyle = () => { if (!pageStyle.isConnected) (document.head || document.documentElement).appendChild(pageStyle); };
  mountStyle();
  const mount = () => { if (!host.isConnected) (document.body || document.documentElement).appendChild(host); };
  mount();
  new MutationObserver(mount).observe(document.documentElement, { childList: true, subtree: false });

  root.querySelector('.chip').addEventListener('click', (e) => {
    const b = e.target.closest('button[data-act]');
    if (!b) return;
    e.preventDefault(); e.stopPropagation();
    post({ type: 'chip', action: b.dataset.act });
  });

  // ---------- Zuordnung DOM → Designer-Knoten ----------
  const own = (id) => id && id.startsWith(cfg.prefix);

  /** Innerster Knoten der aktiven Datei für ein DOM-Element: {id, el} oder null. */
  function nodeOf(start) {
    let cur = start && start.nodeType === 3 ? start.parentElement : start;
    while (cur && cur !== document.documentElement) {
      if (cur.dataset && own(cur.dataset.bsId)) return { id: cur.dataset.bsId, el: cur, kind: 'el' };
      // Liegt cur zwischen Komponenten-Markern?
      let depth = 0;
      for (let s = cur.previousSibling; s; s = s.previousSibling) {
        if (s.nodeType !== 1 || s.tagName !== 'TEMPLATE') continue;
        if (s.dataset.bsE) depth++;
        else if (s.dataset.bsS) {
          if (depth > 0) depth--;
          else if (own(s.dataset.bsS)) return { id: s.dataset.bsS, el: cur, kind: 'comp' };
        }
      }
      cur = cur.parentElement;
    }
    return null;
  }

  /** Alle DOM-Rechtecke eines Knotens (bei Komponenten: alles zwischen den Markern). */
  function rectsOf(id) {
    const el = document.querySelector(`[data-bs-id="${CSS.escape(id)}"]`);
    if (el) return [el.getBoundingClientRect()];
    const s = document.querySelector(`template[data-bs-s="${CSS.escape(id)}"]`);
    if (!s) return [];
    const rects = [];
    for (let n = s.nextSibling; n; n = n.nextSibling) {
      if (n.nodeType === 1 && n.tagName === 'TEMPLATE' && n.dataset.bsE === id) break;
      if (n.nodeType === 1) {
        const r = n.getBoundingClientRect();
        if (r.width || r.height) rects.push(r);
      } else if (n.nodeType === 3 && n.textContent.trim()) {
        const range = document.createRange();
        range.selectNodeContents(n);
        for (const r of range.getClientRects()) rects.push(r);
      }
    }
    return rects;
  }

  function unionRect(rects) {
    if (!rects.length) return null;
    let l = Infinity, t = Infinity, r = -Infinity, b = -Infinity;
    for (const x of rects) { l = Math.min(l, x.left); t = Math.min(t, x.top); r = Math.max(r, x.right); b = Math.max(b, x.bottom); }
    return { left: l, top: t, right: r, bottom: b, width: r - l, height: b - t };
  }

  function place(box, rect) {
    if (!rect || (!rect.width && !rect.height)) { box.style.display = 'none'; return; }
    Object.assign(box.style, { display: 'block', left: rect.left + 'px', top: rect.top + 'px', width: rect.width + 'px', height: rect.height + 'px' });
  }

  function labelOf(n) {
    if (!n) return '';
    if (n.kind === 'comp') {
      const s = document.querySelector(`template[data-bs-s="${CSS.escape(n.id)}"]`);
      return s?.dataset.bsName || 'Komponente';
    }
    const el = n.el;
    return el.tagName.toLowerCase() + (el.classList.length ? '.' + [...el.classList].slice(0, 2).join('.') : '');
  }

  let hover = null;
  function tick() {
    mount(); // Blazor-Rendering kann das Overlay aus dem body entfernen
    const sel = cfg.selected ? unionRect(rectsOf(cfg.selected)) : null;
    place(selBox, sel);
    if (sel) selBox.classList.toggle('below', sel.top < 28);
    place(hoverBox, hover && hover.id !== cfg.selected ? unionRect(rectsOf(hover.id)) : null);
    requestAnimationFrame(tick);
  }
  requestAnimationFrame(tick);

  // ---------- Ereignisse (im Design-Modus abfangen, damit die App nicht reagiert) ----------
  const designOn = () => !cfg.interact;

  window.addEventListener('mousemove', (e) => {
    if (!designOn() || drag) return;
    const n = nodeOf(e.target);
    if ((n && n.id) !== (hover && hover.id)) {
      hover = n;
      hoverLabel.textContent = labelOf(n);
    }
  }, true);
  document.addEventListener('mouseleave', () => { hover = null; });

  const swallow = (e) => { if (!designOn() || editing) return; e.preventDefault(); e.stopImmediatePropagation(); };
  for (const t of ['mousedown', 'mouseup', 'pointerup', 'submit', 'input', 'change', 'focusin', 'auxclick']) {
    window.addEventListener(t, (e) => { if (t === 'focusin' && designOn() && !editing && e.target.blur) e.target.blur(); else swallow(e); }, true);
  }

  window.addEventListener('click', (e) => {
    if (!designOn() || editing) return;
    if (e.composedPath().includes(host)) return;
    e.preventDefault(); e.stopImmediatePropagation();
    if (suppressClick) { suppressClick = false; return; }
    const n = nodeOf(e.target);
    post({ type: 'click', id: n ? n.id : null });
  }, true);

  window.addEventListener('dblclick', (e) => {
    if (!designOn()) return;
    e.preventDefault(); e.stopImmediatePropagation();
    const n = nodeOf(e.target);
    if (n && n.kind === 'el' && [...n.el.childNodes].every(c => c.nodeType === 3) && n.el.textContent.trim()) startEdit(n);
  }, true);

  window.addEventListener('contextmenu', (e) => {
    if (!designOn()) return;
    const n = nodeOf(e.target);
    if (!n) return;
    e.preventDefault(); e.stopImmediatePropagation();
    post({ type: 'contextmenu', id: n.id, x: e.clientX, y: e.clientY });
  }, true);

  // Tastenkürzel an die IDE weiterreichen (Entf, Strg+Z, Strg+D …)
  window.addEventListener('keydown', (e) => {
    if (!designOn() || editing) return;
    const k = e.key.toLowerCase();
    const combo = [e.ctrlKey || e.metaKey ? 'ctrl' : '', e.shiftKey ? 'shift' : '', e.altKey ? 'alt' : '', k].filter(Boolean).join('+');
    e.preventDefault(); e.stopImmediatePropagation();
    post({ type: 'key', combo });
  }, true);

  // ---------- Inline-Text bearbeiten ----------
  let editing = null;
  function startEdit(n) {
    const el = n.el;
    editing = el;
    const original = el.textContent;
    el.setAttribute('contenteditable', 'plaintext-only');
    el.style.outline = '2px solid #f5b95b';
    el.focus();
    const range = document.createRange();
    range.selectNodeContents(el);
    const s = getSelection(); s.removeAllRanges(); s.addRange(range);
    const finish = (commit) => {
      el.removeEventListener('keydown', key, true);
      el.removeEventListener('blur', blur, true);
      el.removeAttribute('contenteditable');
      el.style.outline = '';
      editing = null;
      const text = el.textContent;
      if (commit && text !== original) post({ type: 'text', id: n.id, text });
      else el.textContent = original;
    };
    const key = (e) => {
      e.stopImmediatePropagation();
      if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); finish(true); }
      else if (e.key === 'Escape') { e.preventDefault(); finish(false); }
    };
    const blur = () => finish(true);
    el.addEventListener('keydown', key, true);
    el.addEventListener('blur', blur, true);
  }

  // ---------- Ablegen: Position berechnen (gleiche Logik wie im Designer) ----------
  function axisOf(el) {
    const parent = el.parentElement;
    if (!parent) return 'y';
    const cs = getComputedStyle(parent);
    if (cs.display.includes('flex')) return cs.flexDirection.startsWith('row') ? 'x' : 'y';
    if (cs.display.includes('grid')) return 'x';
    return getComputedStyle(el).display.startsWith('inline') ? 'x' : 'y';
  }

  const LEAF = ['img', 'input', 'br', 'hr', 'textarea', 'select', 'button', 'a', 'span', 'label', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'p', 'svg', 'video', 'audio', 'iframe'];
  const startOf = (id) => document.querySelector(`template[data-bs-s="${CSS.escape(id)}"]`);
  const isContainer = (n) => n.kind === 'el'
    ? !LEAF.includes(n.el.tagName.toLowerCase())
    : startOf(n.id)?.dataset.bsContainer === '1' && !(n.el && LEAF.includes(n.el.tagName.toLowerCase()));

  /** RenderFragment-Parameter (Slots) einer Komponente, wenn es mehr als nur ChildContent gibt. */
  function slotsOf(n) {
    if (!n || n.kind !== 'comp') return null;
    const list = (startOf(n.id)?.dataset.bsSlots || '').split(',').filter(Boolean);
    return list.length > 1 || (list.length === 1 && list[0] !== 'ChildContent') ? list : null;
  }

  /** Nächste Komponente mit Slots: der Knoten selbst oder ein umgebender. */
  function slotHostOf(n) {
    for (let cur = n, i = 0; cur && i < 12; i++) {
      if (slotsOf(cur)) return cur;
      const up = cur.kind === 'comp' ? startOf(cur.id)?.parentElement : cur.el.parentElement;
      cur = up ? nodeOf(up) : null;
    }
    return null;
  }

  // Slot-Leiste beim Ziehen über einer Komponente mit mehreren RenderFragments
  let bar = null; // { host, slots, rect, chips:[{slot, rect}], zone }
  function showBar(host) {
    const rect = unionRect(rectsOf(host.id));
    if (!rect) { hideBar(); return; }
    if (!bar || bar.host.id !== host.id) {
      const s = startOf(host.id);
      const present = (s?.dataset.bsPresent || '').split(',');
      slotBar.innerHTML = '<b>' + (s?.dataset.bsName || '') + '</b>' + slotsOf(host).map(x => `<i data-slot="${x}" class="${present.includes(x) || x === 'ChildContent' ? '' : 'missing'}">${x}</i>`).join('');
      bar = { host };
    }
    slotBar.style.display = 'flex';
    const h = slotBar.offsetHeight || 26;
    const top = rect.top - h - 4 >= 0 ? rect.top - h - 4 : rect.top + 4;
    Object.assign(slotBar.style, { left: Math.max(4, rect.left) + 'px', top: top + 'px' });
    const br = slotBar.getBoundingClientRect();
    bar.rect = rect;
    bar.chips = [...slotBar.querySelectorAll('i')].map(i => ({ slot: i.dataset.slot, el: i, rect: i.getBoundingClientRect() }));
    bar.zone = { left: Math.min(rect.left, br.left) - 6, top: Math.min(rect.top, br.top) - 6, right: Math.max(rect.right, br.right) + 6, bottom: Math.max(rect.bottom, br.bottom) + 6 };
  }
  function hideBar() { bar = null; slotBar.style.display = 'none'; }
  const inside = (x, y, r) => r && x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;

  /** Oberste eigene Knoten der Seite (nicht in anderen eigenen Knoten verschachtelt). */
  function topNodes() {
    const list = [];
    for (const el of document.querySelectorAll('[data-bs-id]')) {
      if (!own(el.dataset.bsId)) continue;
      const parent = el.parentElement ? nodeOf(el.parentElement) : null;
      if (!parent) list.push({ id: el.dataset.bsId, el, kind: 'el' });
    }
    for (const s of document.querySelectorAll('template[data-bs-s]')) {
      if (!own(s.dataset.bsS)) continue;
      const parent = s.parentElement ? nodeOf(s.parentElement) : null;
      if (!parent) list.push({ id: s.dataset.bsS, el: s.nextElementSibling || s.parentElement, kind: 'comp' });
    }
    return list;
  }

  /** Kein eigener Knoten unter dem Zeiger (z. B. Abstand im Layout): nächstgelegenen Knoten der Seite nehmen. */
  function nearestDrop(x, y, dragged) {
    let best = null, bestDist = Infinity;
    for (const n of topNodes()) {
      if (n.id === dragged) continue;
      const r = unionRect(rectsOf(n.id));
      if (!r) continue;
      const dy = y < r.top ? r.top - y : y > r.bottom ? y - r.bottom : 0;
      const dx = x < r.left ? r.left - x : x > r.right ? x - r.right : 0;
      const d = dy * 2 + dx; // vertikale Nähe zählt mehr
      if (d < bestDist) { bestDist = d; best = { n, r }; }
    }
    if (!best) return null;
    const pos = y < best.r.top + best.r.height / 2 ? 'before' : 'after';
    return { targetId: best.n.id, pos, rect: best.r, axis: 'y', label: labelOf(best.n) };
  }

  function computeDrop(x, y, dragged) {
    const r = computeDropCore(x, y, dragged);
    if (bar) for (const c of bar.chips) c.el.classList.toggle('on', !!r && r.pos === 'inside:' + c.slot && r.targetId === bar.host.id);
    return r;
  }

  function computeDropCore(x, y, dragged) {
    // Über einem Slot-Chip: genau in diesen RenderFragment-Parameter
    if (bar && bar.host.id !== dragged) {
      const chip = bar.chips.find(c => inside(x, y, c.rect));
      if (chip) return { targetId: bar.host.id, pos: 'inside:' + chip.slot, rect: bar.rect, axis: 'y', label: '' }; // die Leiste zeigt das Ziel schon
    }
    const target = document.elementFromPoint(x, y);
    const n = nodeOf(target);
    // Leiste zeigen für die nächste Komponente mit Slots (bleibt stehen, solange der Zeiger in ihrer Nähe ist)
    const host = n ? slotHostOf(n) : null;
    if (host && host.id !== dragged) showBar(host);
    else if (!(bar && inside(x, y, bar.zone))) hideBar();
    if (!n) return nearestDrop(x, y, dragged) || { targetId: null, pos: 'inside', rect: unionRect([document.body.getBoundingClientRect()]), axis: 'y', label: 'body' };
    if (dragged && (n.id === dragged)) return null;
    const rect = unionRect(rectsOf(n.id));
    if (!rect) return null;
    const axis = n.kind === 'el' ? axisOf(n.el) : axisOf(n.el);
    const container = isContainer(n);
    const size = axis === 'x' ? rect.width : rect.height;
    const rel = axis === 'x' ? x - rect.left : y - rect.top;
    const edge = container ? Math.min(14, size * .25) : size / 2;
    const pos = container && rel > edge && rel < size - edge ? 'inside' : rel < size / 2 ? 'before' : 'after';
    return { targetId: n.id, pos, rect, axis, label: labelOf(n) };
  }

  function drawIndicator(info) {
    if (!info) { ind.style.display = 'none'; hideBar(); return; }
    const r = info.rect;
    ind.className = 'box ind ' + (info.pos.startsWith('inside') ? 'inside' : info.pos);
    indLabel.textContent = info.label || '';
    indLabel.style.display = info.label ? '' : 'none';
    if (info.pos.startsWith('inside')) place(ind, r);
    else if (info.axis === 'x') place(ind, { left: (info.pos === 'before' ? r.left - 2 : r.right - 1), top: r.top, width: 3, height: r.height });
    else place(ind, { left: r.left, top: (info.pos === 'before' ? r.top - 2 : r.bottom - 1), width: r.width, height: 3 });
  }

  // Ziehen von außen (Toolbox/Ebenen im Designer)
  let lastHit = null;
  function onHit(m) {
    lastHit = computeDrop(m.x, m.y, m.kind === 'node' ? m.payload : null);
    drawIndicator(lastHit);
    post({ type: 'hit-result', req: m.req, ok: !!lastHit });
  }

  // Elemente in der Seite verschieben
  let drag = null, pending = null, suppressClick = false;
  window.addEventListener('pointerdown', (e) => {
    if (!designOn() || editing || e.button !== 0) return;
    if (e.composedPath().includes(host)) return;
    const n = nodeOf(e.target);
    if (n) pending = { x: e.clientX, y: e.clientY, n };
    e.preventDefault(); e.stopImmediatePropagation();
  }, true);
  window.addEventListener('pointermove', (e) => {
    if (!pending && !drag) return;
    if (!drag) {
      if ((e.buttons & 1) === 0 || Math.hypot(e.clientX - pending.x, e.clientY - pending.y) < 6) return;
      drag = pending.n;
      ghost.textContent = labelOf(drag);
      ghost.style.display = 'block';
      hover = null;
    }
    ghost.style.transform = `translate(${e.clientX + 12}px, ${e.clientY + 10}px)`;
    lastHit = computeDrop(e.clientX, e.clientY, drag.id);
    drawIndicator(lastHit);
    e.preventDefault(); e.stopImmediatePropagation();
  }, true);
  window.addEventListener('pointerup', (e) => {
    if (drag) {
      if (lastHit) post({ type: 'drop', kind: 'node', payload: drag.id, targetId: lastHit.targetId, pos: lastHit.pos });
      suppressClick = true;
      setTimeout(() => { suppressClick = false; }, 60);
    }
    drag = null; pending = null; lastHit = null;
    ghost.style.display = 'none';
    drawIndicator(null);
    hideBar();
  }, true);

  // ---------- Nachrichten vom Designer ----------
  window.addEventListener('message', (e) => {
    const m = e.data;
    if (!m || m.__bsHost !== 1) return;
    switch (m.type) {
      case 'config':
        Object.assign(cfg, { prefix: m.prefix ?? cfg.prefix, interact: !!m.interact });
        document.documentElement.classList.toggle('bs-interact', cfg.interact);
        markEmpty();
        if (cfg.interact) { hover = null; place(hoverBox, null); }
        break;
      case 'select':
        cfg.selected = m.id || null;
        selLabel.textContent = m.label || '';
        if (m.id && m.scroll !== false) {
          const r = unionRect(rectsOf(m.id));
          if (r && (r.bottom < 0 || r.top > innerHeight)) scrollBy({ top: r.top - innerHeight / 3, behavior: 'smooth' });
        }
        break;
      case 'hit': onHit(m); break;
      case 'hit-end':
        if (m.commit && lastHit) post({ type: 'drop', kind: m.kind, payload: m.payload, targetId: lastHit.targetId, pos: lastHit.pos });
        lastHit = null;
        drawIndicator(null);
        hideBar();
        break;
      case 'patch': {
        // Sofort-Vorschau für Style/Klasse, bis Hot Reload den echten Stand liefert
        const el = document.querySelector(`[data-bs-id="${CSS.escape(m.id)}"]`);
        if (!el) break;
        for (const [k, v] of Object.entries(m.attrs || {})) {
          if (v === null) el.removeAttribute(k); else el.setAttribute(k, v);
        }
        break;
      }
      case 'state':
        // :hover/:focus erzwingen wie im Designer
        document.querySelectorAll('[data-bs-state]').forEach(x => x.removeAttribute('data-bs-state'));
        if (m.id && m.state) document.querySelector(`[data-bs-id="${CSS.escape(m.id)}"]`)?.setAttribute('data-bs-state', m.state);
        break;
      case 'reload': location.reload(); break;
      case 'refresh': {
        // Neu rendern ohne Neuladen: erweiterte Blazor-Navigation auf dieselbe Seite (übernimmt Hot-Reload-Änderungen)
        const y = scrollY;
        const url = location.pathname + location.search + location.hash;
        try {
          if (window.Blazor && typeof Blazor.navigateTo === 'function') Blazor.navigateTo(url, { forceLoad: false, replaceHistoryEntry: true });
          else location.reload();
        } catch { location.reload(); }
        setTimeout(() => scrollTo(0, y), 250);
        setTimeout(() => scrollTo(0, y), 700);
        break;
      }
    }
  });

  /** Leere eigene Container (HTML und Komponenten mit ChildContent) bekommen data-bs-empty. */
  function markEmpty() {
    mountStyle();
    const want = new Map();
    if (designOn() && cfg.prefix) {
      const blank = (el) => ![...el.childNodes].some(c => c.nodeType === 1 && c.tagName !== 'TEMPLATE' || c.nodeType === 3 && c.textContent.trim());
      for (const el of document.querySelectorAll('[data-bs-id]')) {
        if (!own(el.dataset.bsId) || LEAF.includes(el.tagName.toLowerCase())) continue;
        if (blank(el) && (el.hasAttribute('data-bs-empty') || el.getBoundingClientRect().height < 30)) want.set(el, el.tagName.toLowerCase());
      }
      for (const t of document.querySelectorAll('template[data-bs-s][data-bs-container="1"]')) {
        if (!own(t.dataset.bsS)) continue;
        const els = [];
        for (let n = t.nextSibling; n && !(n.nodeType === 1 && n.tagName === 'TEMPLATE' && n.dataset.bsE === t.dataset.bsS); n = n.nextSibling)
          if (n.nodeType === 1) els.push(n);
        if (els.length === 1 && !LEAF.includes(els[0].tagName.toLowerCase()) && blank(els[0]) && (els[0].hasAttribute('data-bs-empty') || els[0].getBoundingClientRect().height < 30)) want.set(els[0], t.dataset.bsName || '');
      }
    }
    for (const el of document.querySelectorAll('[data-bs-empty]')) if (!want.has(el)) el.removeAttribute('data-bs-empty');
    for (const [el, name] of want) {
      const label = (name ? name + ' · ' : '') + 'Elemente hierher ziehen';
      if (el.getAttribute('data-bs-empty') !== label) el.setAttribute('data-bs-empty', label);
    }
  }
  setInterval(markEmpty, 400);
  // Für Tests/Fehlersuche (Designer-Brücke läuft nur im Schatten-Projekt)
  window.__bsDebug = { computeDrop, nodeOf, rectsOf, slotHostOf, slotsOf };

  post({ type: 'ready', url: location.pathname + location.search });
  // Nach Blazor-Navigation (enhanced nav) erneut melden
  let lastUrl = location.href;
  setInterval(() => { if (location.href !== lastUrl) { lastUrl = location.href; post({ type: 'ready', url: location.pathname + location.search }); } }, 500);
})();
