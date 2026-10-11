// Blazor Studio – Browser-Seite: Monaco, Designer-Interaktion, Tastenkürzel, Splitter.
(() => {
  'use strict';

  const studio = window.studio = {};

  // ================================================================
  // Allgemein
  // ================================================================

  studio.setTheme = (theme, accent, scheme) => {
    if (scheme) themeState.scheme = scheme;
    const root = document.documentElement;
    root.dataset.theme = theme;
    root.style.setProperty('--ac', accent);
    studio.monaco.applyTheme(theme, accent);
  };

  studio.prefersLight = () => window.matchMedia('(prefers-color-scheme: light)').matches;

  studio.focus = (el, select) => {
    if (!el) return;
    el.focus();
    if (select && el.select) el.select();
  };

  studio.focusSelector = (sel, select) => studio.focus(document.querySelector(sel), select);

  studio.scrollToBottom = (el) => { if (el) el.scrollTop = el.scrollHeight; };
  // Kontextmenü im sichtbaren Bereich halten (auch Untermenüs nach links klappen)
  studio.fitMenu = (id) => {
    const m = document.getElementById(id);
    if (!m) return;
    const r = m.getBoundingClientRect();
    if (r.right > innerWidth - 4) m.style.left = Math.max(4, innerWidth - r.width - 4) + 'px';
    if (r.bottom > innerHeight - 4) m.style.top = Math.max(4, innerHeight - r.height - 4) + 'px';
    m.classList.toggle('flip', m.getBoundingClientRect().right + 240 > innerWidth);
  };

  studio.rectOf = (el) => {
    if (!el || !el.getBoundingClientRect) return null;
    const r = el.getBoundingClientRect();
    return { x: r.left, y: r.top, width: r.width, height: r.height, viewportWidth: window.innerWidth, viewportHeight: window.innerHeight };
  };

  studio.scrollIntoView = (sel) => {
    const el = document.querySelector(sel);
    if (el) el.scrollIntoView({ block: 'nearest' });
  };

  studio.copyText = async (text) => { try { await navigator.clipboard.writeText(text); return true; } catch { return false; } };

  studio.readClipboard = async () => { try { return await navigator.clipboard.readText(); } catch { return ''; } };

  studio.elementRect = (sel) => {
    const el = document.querySelector(sel);
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return { x: r.left, y: r.top, width: r.width, height: r.height };
  };

  // ---------------- Tastenkürzel ----------------

  const typingAllowed = new Set([
    'ctrl+s', 'ctrl+shift+s', 'f5', 'shift+f5', 'ctrl+f5', 'ctrl+shift+f5', 'ctrl+shift+b', 'ctrl+p', 'ctrl+shift+p',
    'f10', 'f11', 'shift+f11', 'ctrl+w', 'ctrl+tab', 'ctrl+shift+tab', 'ctrl+n', 'ctrl+o', 'ctrl+comma', 'ctrl+j', 'ctrl+shift+n', 'alt+f2', 'ctrl+alt+x', 'alt+f12', 'alt+0', 'alt+9', 'ctrl+t', 'ctrl+shift+k', 'ctrl+shift+b', 'alt+insert', 'ctrl+shift+f', 'ctrl+shift+h',
  ]);
  const designOnly = new Set([
    'delete', 'backspace', 'ctrl+z', 'ctrl+y', 'ctrl+shift+z', 'ctrl+d', 'ctrl+c', 'ctrl+v', 'ctrl+x',
    'escape', 'alt+arrowup', 'alt+arrowdown', 'ctrl+k', 'enter', 'f2',
  ]);

  function comboOf(e) {
    let k = e.key.toLowerCase();
    if (k === ' ') k = 'space';
    if (k === ',') k = 'comma';
    const parts = [];
    if (e.ctrlKey || e.metaKey) parts.push('ctrl');
    if (e.shiftKey) parts.push('shift');
    if (e.altKey) parts.push('alt');
    if (!['control', 'shift', 'alt', 'meta'].includes(k)) parts.push(k);
    return parts.join('+');
  }

  function isTyping(t) {
    if (!t || !t.closest) return false;
    if (t.closest('.monaco-editor')) return 'monaco';
    if (t.isContentEditable) return 'edit';
    const tag = t.tagName;
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' ? 'field' : false;
  }

  studio.registerShortcuts = (dotnet) => {
    studio._shortcutDotnet = dotnet;
    window.addEventListener('error', (e) => { try { dotnet.invokeMethodAsync('OnJsError', (e.message || 'error') + ' @ ' + (e.filename || '') + ':' + (e.lineno || '')); } catch { } });
    window.addEventListener('unhandledrejection', (e) => { try { dotnet.invokeMethodAsync('OnJsError', 'Promise: ' + (e.reason && (e.reason.message || e.reason) || 'rejected')); } catch { } });
    window.addEventListener('keydown', (e) => {
      const combo = comboOf(e);
      if (e.target.closest && e.target.closest('.dd-pop, .dd') && ['escape', 'enter', 'arrowup', 'arrowdown'].includes(combo)) return;
      const typing = isTyping(e.target);
      let handle = false;
      if (typingAllowed.has(combo)) handle = true;
      else if (combo === 'f9' && typing !== 'field') handle = true;
      else if (!typing && designOnly.has(combo)) handle = true;
      else if (combo === 'escape' && typing === 'field' && e.target.closest('.palette, .dialog')) handle = true;
      if (!handle) return;
      // In Eingabefeldern Escape/Enter normal lassen, außer in Palette/Dialog
      e.preventDefault();
      e.stopPropagation();
      dotnet.invokeMethodAsync('OnShortcut', combo);
    }, true);

    window.addEventListener('wheel', (e) => {
      if (!e.ctrlKey) return;
      const stage = e.target.closest && e.target.closest('.stage');
      if (!stage) return;
      e.preventDefault();
      dotnet.invokeMethodAsync('OnShortcut', e.deltaY < 0 ? 'zoomin' : 'zoomout');
    }, { passive: false });

    window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', () => dotnet.invokeMethodAsync('OnSystemThemeChanged'));
  };

  // ---------------- Splitter ----------------

  studio.initSplitters = (dotnet) => {
    document.addEventListener('pointerdown', (e) => {
      const handle = e.target.closest && e.target.closest('[data-splitter]');
      if (!handle) return;
      e.preventDefault();
      const kind = handle.dataset.splitter;
      const shell = document.querySelector('.shell');
      const surface = handle.closest('.surface');
      handle.setPointerCapture(e.pointerId);
      document.body.classList.add('resizing', kind === 'side' ? 'resizing-x' : 'resizing-y');
      let value = 0;
      const move = (ev) => {
        if (kind === 'tool') {
          value = Math.round(Math.min(460, Math.max(180, ev.clientX - 6)));
          shell.style.setProperty('--tool-w', value + 'px');
        } else if (kind === 'side') {
          value = Math.round(Math.min(640, Math.max(260, window.innerWidth - ev.clientX - 6)));
          shell.style.setProperty('--side-w', value + 'px');
        } else if (kind === 'bottom') {
          value = Math.round(Math.min(window.innerHeight - 220, Math.max(90, window.innerHeight - ev.clientY - 30)));
          shell.style.setProperty('--bottom-h', value + 'px');
        } else if (kind === 'code' && surface) {
          const r = surface.getBoundingClientRect();
          value = Math.round(Math.min(r.height - 80, Math.max(80, r.bottom - ev.clientY)));
          surface.style.setProperty('--code-h', value + 'px');
        } else if (kind === 'layers') {
          const side = handle.closest('.side');
          const r = side.getBoundingClientRect();
          value = Math.round(Math.min(r.height - 160, Math.max(120, ev.clientY - r.top)));
          side.style.setProperty('--layers-h', value + 'px');
        }
      };
      const up = () => {
        handle.removeEventListener('pointermove', move);
        handle.removeEventListener('pointerup', up);
        document.body.classList.remove('resizing', 'resizing-x', 'resizing-y');
        if (value) dotnet.invokeMethodAsync('OnSplitterChanged', kind, value);
      };
      handle.addEventListener('pointermove', move);
      handle.addEventListener('pointerup', up);
    });
  };

  // ================================================================
  // Drag & Drop (Toolbox, Ebenen, Canvas)
  // ================================================================

  const drag = { kind: null, payload: null };
  studio.drag = drag;

  // Natives HTML5-Drag ist in WebView2 mit echter Maus unzuverlässig (OS-Drag, Abbruch bei Re-Render).
  // Toolbox, Ebenen und Fläche ziehen deshalb über Pointer-Events (siehe „Pointer-Drag“ unten).
  document.addEventListener('dragstart', (e) => {
    if (e.target.closest && (e.target.closest('[data-bs-palette]') || e.target.closest('[data-bs-outline]') || e.target.closest('.bs-page'))) e.preventDefault();
  });
  document.addEventListener('dragend', () => {
    drag.kind = null;
    drag.payload = null;
    document.querySelectorAll('.bs-drop-ind').forEach(x => x.style.display = 'none');
    document.querySelectorAll('.row.drop-before,.row.drop-after,.row.drop-inside').forEach(r => r.classList.remove('drop-before', 'drop-after', 'drop-inside'));
  });

  // ---------------- Ebenen-Baum ----------------

  studio.attachOutline = (el, dotnet) => {
    if (!el || el._bsOutline) return;
    el._bsOutline = true;
    let last = null;
    const clear = () => { if (last) last.classList.remove('drop-before', 'drop-after', 'drop-inside'); last = null; };
    const over = (target, clientY) => {
      const row = target.closest('[data-bs-outline]');
      clear();
      if (!row) return true;
      if (drag.kind === 'node' && isDescendantRow(el, drag.payload, row)) return false;
      const r = row.getBoundingClientRect();
      const y = (clientY - r.top) / r.height;
      const container = row.dataset.bsContainer === '1';
      const pos = container ? (y < .28 ? 'before' : y > .72 ? 'after' : 'inside') : (y < .5 ? 'before' : 'after');
      row.classList.add('drop-' + pos);
      row._bsPos = pos;
      last = row;
      return true;
    };
    const drop = (target) => {
      const row = target.closest('[data-bs-outline]');
      if (row && drag.kind === 'node' && isDescendantRow(el, drag.payload, row)) { clear(); return; }
      const pos = row ? row._bsPos || 'after' : 'inside';
      const id = row ? row.dataset.bsOutline : null;
      clear();
      dotnet.invokeMethodAsync('OnDrop', drag.kind, drag.payload, id, pos);
    };
    outlines.set(el, { over, drop, clear });
    el.addEventListener('dragover', (e) => {
      if (!drag.kind) return;
      e.preventDefault();
      if (!over(e.target, e.clientY)) e.dataTransfer.dropEffect = 'none';
    });
    el.addEventListener('dragleave', (e) => { if (!el.contains(e.relatedTarget)) clear(); });
    el.addEventListener('drop', (e) => {
      if (!drag.kind) return;
      e.preventDefault();
      drop(e.target);
    });
  };
  const outlines = new Map();

  function isDescendantRow(container, draggedId, row) {
    // Zeilen tragen data-bs-path (Ids der Vorfahren, mit | getrennt)
    if (row.dataset.bsOutline === draggedId) return true;
    return (row.dataset.bsPath || '').split('|').includes(draggedId);
  }

  // ---------------- Live-Designer (echte App im iframe) ----------------
  const liveFrames = new Map(); // iframe → { dotnet, prefix, selected, label, interact, zoom, lastOk, req }
  const liveSend = (frame, m) => { try { frame.contentWindow?.postMessage(Object.assign({ __bsHost: 1 }, m), '*'); } catch { } };
  window.addEventListener('message', (e) => {
    const m = e.data;
    if (!m || m.__bs !== 1) return;
    for (const [frame, st] of liveFrames) {
      if (frame.contentWindow !== e.source) continue;
      const d = st.dotnet;
      switch (m.type) {
        case 'ready':
          liveSend(frame, { type: 'config', prefix: st.prefix, interact: st.interact });
          liveSend(frame, { type: 'select', id: st.selected, label: st.label, scroll: false });
          d.invokeMethodAsync('OnLiveReady', m.url);
          break;
        case 'click': d.invokeMethodAsync('OnCanvasClick', m.id); break;
        case 'text': d.invokeMethodAsync('OnInlineText', m.id, m.text); break;
        case 'chip': d.invokeMethodAsync('OnChipAction', m.action); break;
        case 'drop': d.invokeMethodAsync('OnDrop', m.kind, m.payload, m.targetId, m.pos); break;
        case 'contextmenu': {
          const r = frame.getBoundingClientRect();
          d.invokeMethodAsync('OnCanvasContextMenu', m.id, r.left + m.x * st.zoom, r.top + m.y * st.zoom);
          break;
        }
        case 'key': studio._shortcutDotnet?.invokeMethodAsync('OnShortcut', m.combo); break;
        case 'hit-result': st.lastOk = m.ok; break;
      }
    }
  });

  studio.liveDesign = {
    attach(frame, dotnet, prefix, zoom) {
      if (!frame) return;
      const st = liveFrames.get(frame) || { selected: null, label: '', interact: false, lastOk: false, req: 0 };
      Object.assign(st, { dotnet, prefix, zoom: zoom || 1 });
      liveFrames.set(frame, st);
      liveSend(frame, { type: 'config', prefix, interact: st.interact });
    },
    detach(frame) { liveFrames.delete(frame); },
    select(frame, id, label) {
      const st = liveFrames.get(frame);
      if (!st) return;
      st.selected = id; st.label = label;
      liveSend(frame, { type: 'select', id, label });
    },
    config(frame, prefix, interact, zoom) {
      const st = liveFrames.get(frame);
      if (!st) return;
      Object.assign(st, { prefix, interact: !!interact, zoom: zoom || st.zoom });
      liveSend(frame, { type: 'config', prefix, interact: !!interact });
    },
    patch(frame, id, attrs) { liveSend(frame, { type: 'patch', id, attrs }); },
    state(frame, id, state) { liveSend(frame, { type: 'state', id, state }); },
    reload(frame) { liveSend(frame, { type: 'reload' }); },
    refresh(frame) { liveSend(frame, { type: 'refresh' }); },
  };

  // ---------------- Pointer-Drag ----------------
  // Ziehen aus Toolbox (data-bs-palette), Ebenen (data-bs-outline) und auf der Fläche (Elemente verschieben).
  const pd = { pending: null, active: false, ghost: null, stage: null, outline: null, suppressClick: false };

  function stageOf(el) {
    const st = el && el.closest ? el.closest('.stage') : null;
    return st && designers.has(st) ? st : null;
  }
  function outlineOf(el) {
    for (const [root, h] of outlines) if (root.contains(el)) return h;
    return null;
  }
  function endDrag() {
    for (const f of liveFrames.keys()) f.style.pointerEvents = '';
    if (pd.live) liveSend(pd.live, { type: 'hit-end', commit: false });
    pd.live = null;
    if (pd.ghost) pd.ghost.remove();
    pd.ghost = null;
    if (pd.stage) designers.get(pd.stage)?.clear?.();
    for (const h of outlines.values()) h.clear();
    pd.stage = null;
    pd.outline = null;
    pd.active = false;
    pd.pending = null;
    drag.kind = null;
    drag.payload = null;
    document.body.classList.remove('bs-dragging');
  }

  document.addEventListener('pointerdown', (e) => {
    if (e.button !== 0 || pd.active) return;
    const t = e.target;
    if (!t.closest) return;
    const pal = t.closest('[data-bs-palette]');
    if (pal) { pd.pending = { x: e.clientX, y: e.clientY, kind: 'component', payload: pal.dataset.bsPalette, label: pal.textContent.trim().split('\n')[0] }; return; }
    const row = t.closest('[data-bs-outline]');
    if (row && !t.closest('input, button')) { pd.pending = { x: e.clientX, y: e.clientY, kind: 'node', payload: row.dataset.bsOutline, label: (row.querySelector('.nm, .name, b, span') || row).textContent.trim() }; return; }
    const st = stageOf(t);
    if (st) {
      const s = designers.get(st);
      if (s.editing || t.closest('.bs-overlay')) return;
      const n = s.nodeAt ? s.nodeAt(t) : null;
      if (n && n.dataset.bsId) pd.pending = { x: e.clientX, y: e.clientY, kind: 'node', payload: n.dataset.bsId, label: n.dataset.bsLabel || n.tagName.toLowerCase() };
    }
  }, true);

  document.addEventListener('pointermove', (e) => {
    if (!pd.pending && !pd.active) return;
    if (!pd.active) {
      if ((e.buttons & 1) === 0) { pd.pending = null; return; }
      if (Math.hypot(e.clientX - pd.pending.x, e.clientY - pd.pending.y) < 6) return;
      pd.active = true;
      drag.kind = pd.pending.kind;
      drag.payload = pd.pending.payload;
      const g = document.createElement('div');
      g.className = 'bs-ghost' + (drag.kind === 'node' ? ' move' : '');
      g.textContent = pd.pending.label || drag.payload;
      document.body.appendChild(g);
      pd.ghost = g;
      document.body.classList.add('bs-dragging');
      window.getSelection()?.removeAllRanges();
      for (const f of liveFrames.keys()) f.style.pointerEvents = 'none';
    }
    e.preventDefault();
    pd.ghost.style.transform = `translate(${e.clientX + 12}px, ${e.clientY + 10}px)`;
    const target = document.elementFromPoint(e.clientX, e.clientY);
    // Über einem Live-iframe: Ablageposition berechnet die echte App
    // (iframes haben beim Ziehen pointer-events:none – daher über die Position erkennen)
    let live = null;
    for (const f of liveFrames.keys()) {
      const fr = f.getBoundingClientRect();
      if (f.isConnected && e.clientX >= fr.left && e.clientX <= fr.right && e.clientY >= fr.top && e.clientY <= fr.bottom) { live = f; break; }
    }
    if (pd.live && pd.live !== live) liveSend(pd.live, { type: 'hit-end', commit: false });
    pd.live = live;
    if (live) {
      const lst = liveFrames.get(live);
      const r = live.getBoundingClientRect();
      liveSend(live, { type: 'hit', x: (e.clientX - r.left) / lst.zoom, y: (e.clientY - r.top) / lst.zoom, kind: drag.kind, payload: drag.payload, req: ++lst.req });
      if (pd.stage) designers.get(pd.stage)?.clear?.();
      pd.stage = null;
      if (pd.outline) pd.outline.clear();
      pd.outline = null;
      pd.ghost.classList.toggle('no', !lst.lastOk);
      return;
    }
    const st = stageOf(target);
    if (pd.stage && pd.stage !== st) designers.get(pd.stage)?.clear?.();
    pd.stage = st;
    let ok = false;
    if (st) ok = designers.get(st).over(target, e.clientX, e.clientY);
    const ol = st ? null : outlineOf(target);
    if (pd.outline && pd.outline !== ol) pd.outline.clear();
    pd.outline = ol;
    if (ol) ok = ol.over(target, e.clientY);
    pd.ghost.classList.toggle('no', !ok);
  }, true);

  document.addEventListener('pointerup', (e) => {
    if (!pd.active) { pd.pending = null; return; }
    const target = document.elementFromPoint(e.clientX, e.clientY);
    try {
      if (pd.live) {
        liveSend(pd.live, { type: 'hit-end', commit: true, kind: drag.kind, payload: drag.payload });
        pd.live = null;
      }
      else if (pd.stage) designers.get(pd.stage).drop(target, e.clientX, e.clientY);
      else if (pd.outline) pd.outline.drop(target);
    } finally {
      pd.suppressClick = true;
      setTimeout(() => { pd.suppressClick = false; }, 50);
      endDrag();
    }
  }, true);

  // Nach dem Ablegen keinen Klick auslösen (würde sonst auswählen/einfügen)
  document.addEventListener('click', (e) => {
    if (pd.suppressClick) { e.stopPropagation(); e.preventDefault(); pd.suppressClick = false; }
  }, true);
  document.addEventListener('keydown', (e) => { if (pd.active && e.key === 'Escape') endDrag(); }, true);
  window.addEventListener('blur', () => { if (pd.active) endDrag(); });

  // ================================================================
  // Designer-Fläche
  // ================================================================

  const designers = new Map();

  studio.designer = {
    attach(stage, dotnet) {
      if (!stage || designers.has(stage)) return;
      const page = () => stage.querySelector('.bs-page');
      const overlay = document.createElement('div');
      overlay.className = 'bs-overlay';
      overlay.innerHTML =
        '<div class="bs-hover"><span class="bs-hover-label"></span></div>' +
        '<div class="bs-sel"><div class="bs-chip"><span class="bs-chip-label"></span>' +
        '<button data-act="parent" title="↑">&#8593;</button><button data-act="duplicate" title="⧉">&#10697;</button><button data-act="delete" title="✕">&#10005;</button></div>' +
        '<i class="h tl"></i><i class="h tr"></i><i class="h bl"></i><i class="h br"></i></div>' +
        '<div class="bs-drop-ind"><span></span></div>';
      stage.appendChild(overlay);
      const hoverBox = overlay.querySelector('.bs-hover');
      const hoverLabel = overlay.querySelector('.bs-hover-label');
      const selBox = overlay.querySelector('.bs-sel');
      const chipLabel = overlay.querySelector('.bs-chip-label');
      const ind = overlay.querySelector('.bs-drop-ind');
      const state = { dotnet, selId: null, hoverEl: null, raf: 0, editing: null, overlay };
      designers.set(stage, state);

      const nodeAt = (t) => {
        const el = t && t.closest ? t.closest('[data-bs-id]') : null;
        return el && page() && page().contains(el) ? el : null;
      };

      const place = (box, el) => {
        if (!el) { box.style.display = 'none'; return; }
        const sr = overlay.getBoundingClientRect();
        const r = el.getBoundingClientRect();
        if (r.width === 0 && r.height === 0) { box.style.display = 'none'; return; }
        box.style.display = 'block';
        box.style.left = (r.left - sr.left) + 'px';
        box.style.top = (r.top - sr.top) + 'px';
        box.style.width = r.width + 'px';
        box.style.height = r.height + 'px';
      };

      const tick = () => {
        const p = page();
        const sel = state.selId && p ? p.querySelector(`[data-bs-id="${state.selId}"]`) : null;
        place(selBox, sel);
        if (sel) selBox.classList.toggle('chip-below', sel.getBoundingClientRect().top - overlay.getBoundingClientRect().top < 30);
        place(hoverBox, state.hoverEl && state.hoverEl !== sel && document.contains(state.hoverEl) ? state.hoverEl : null);
        state.raf = requestAnimationFrame(tick);
      };
      state.raf = requestAnimationFrame(tick);

      overlay.addEventListener('click', (e) => {
        const b = e.target.closest('button[data-act]');
        if (!b) return;
        e.stopPropagation();
        dotnet.invokeMethodAsync('OnChipAction', b.dataset.act);
      });

      stage.addEventListener('mousedown', (e) => {
        if (state.editing) return;
        const n = nodeAt(e.target);
        if (n && /^(INPUT|TEXTAREA|SELECT|OPTION|BUTTON|A|SUMMARY|LABEL)$/.test(e.target.tagName)) e.preventDefault();
      });

      stage.addEventListener('click', (e) => {
        if (e.target.closest('.bs-overlay')) return;
        if (state.editing && state.editing.contains(e.target)) return;
        e.preventDefault();
        e.stopPropagation();
        const n = nodeAt(e.target);
        if (!n && !e.target.closest('.bs-page')) return;
        dotnet.invokeMethodAsync('OnCanvasClick', n ? n.dataset.bsId : null);
      }, true);

      stage.addEventListener('dblclick', (e) => {
        const n = nodeAt(e.target);
        if (!n || n.dataset.bsText !== '1') return;
        e.preventDefault();
        startEdit(state, n);
      }, true);

      stage.addEventListener('contextmenu', (e) => {
        const n = nodeAt(e.target);
        if (!n) return;
        e.preventDefault();
        dotnet.invokeMethodAsync('OnCanvasContextMenu', n.dataset.bsId, e.clientX, e.clientY);
      });

      stage.addEventListener('submit', (e) => e.preventDefault(), true);

      stage.addEventListener('mousemove', (e) => {
        if (drag.kind) return;
        const n = nodeAt(e.target);
        if (n !== state.hoverEl) {
          state.hoverEl = n;
          hoverLabel.textContent = n ? n.dataset.bsLabel || '' : '';
        }
      });
      stage.addEventListener('mouseleave', () => { state.hoverEl = null; });

      // ---- Canvas-Knoten ziehen ----
      stage.addEventListener('dragstart', (e) => {
        const n = nodeAt(e.target);
        if (!n || state.editing) { if (!drag.kind) e.preventDefault(); return; }
        drag.kind = 'node';
        drag.payload = n.dataset.bsId;
        e.dataTransfer.setData('text/plain', drag.payload);
        e.dataTransfer.effectAllowed = 'move';
        e.stopPropagation();
      });

      let dropInfo = null;
      state.over = (target, x, y) => {
        dropInfo = computeDrop(page(), nodeAt(target), x, y, drag);
        if (!dropInfo) { ind.style.display = 'none'; return false; }
        drawIndicator(overlay, ind, dropInfo);
        return true;
      };
      state.clear = () => { ind.style.display = 'none'; dropInfo = null; };
      state.drop = (target, x, y) => {
        ind.style.display = 'none';
        const info = dropInfo || computeDrop(page(), nodeAt(target), x, y, drag);
        dropInfo = null;
        if (!info) return;
        dotnet.invokeMethodAsync('OnDrop', drag.kind, drag.payload, info.targetId, info.pos);
      };
      state.nodeAt = nodeAt;
      stage.addEventListener('dragover', (e) => {
        if (!drag.kind) return;
        e.preventDefault();
        if (!state.over(e.target, e.clientX, e.clientY)) { e.dataTransfer.dropEffect = 'none'; return; }
        e.dataTransfer.dropEffect = drag.kind === 'node' ? 'move' : 'copy';
      });
      stage.addEventListener('dragleave', (e) => { if (!stage.contains(e.relatedTarget)) ind.style.display = 'none'; });
      stage.addEventListener('drop', (e) => {
        if (!drag.kind) return;
        e.preventDefault();
        state.drop(e.target, e.clientX, e.clientY);
        drag.kind = null;
      });
    },

    select(stage, id, label) {
      const s = designers.get(stage);
      if (!s) return;
      s.selId = id;
      s.overlay.querySelector('.bs-chip-label').textContent = label || '';
      if (id) {
        const el = stage.querySelector(`.bs-page [data-bs-id="${id}"]`);
        if (el) {
          const r = el.getBoundingClientRect();
          const sr = stage.getBoundingClientRect();
          if (r.bottom < sr.top + 20 || r.top > sr.bottom - 20) el.scrollIntoView({ block: 'center', behavior: 'smooth' });
        }
      }
    },

    setState(stage, id, st) {
      stage.querySelectorAll('[data-bs-state]').forEach(x => x.removeAttribute('data-bs-state'));
      if (!id || !st) return;
      const el = stage.querySelector(`.bs-page [data-bs-id="${id}"]`);
      if (el) el.setAttribute('data-bs-state', st);
    },

    detach(stage) {
      const s = designers.get(stage);
      if (!s) return;
      cancelAnimationFrame(s.raf);
      s.overlay.remove();
      designers.delete(stage);
    },
  };

  function startEdit(state, el) {
    if (state.editing) return;
    state.editing = el;
    const original = el.innerText;
    el.setAttribute('contenteditable', 'plaintext-only');
    el.classList.add('bs-editing');
    el.focus();
    const range = document.createRange();
    range.selectNodeContents(el);
    const sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
    const finish = (commit) => {
      el.removeEventListener('keydown', key);
      el.removeEventListener('blur', blur);
      el.removeAttribute('contenteditable');
      el.classList.remove('bs-editing');
      const text = el.innerText.replace(/\n+$/, '');
      state.editing = null;
      if (commit && text !== original) state.dotnet.invokeMethodAsync('OnInlineText', el.dataset.bsId, text);
      else el.innerText = original;
    };
    const key = (e) => {
      if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); finish(true); }
      if (e.key === 'Escape') { e.preventDefault(); finish(false); }
      e.stopPropagation();
    };
    const blur = () => finish(true);
    el.addEventListener('keydown', key);
    el.addEventListener('blur', blur);
  }

  function axisOf(el) {
    const parent = el.parentElement;
    if (!parent) return 'y';
    const cs = getComputedStyle(parent);
    if (cs.display.includes('flex')) return cs.flexDirection.startsWith('row') ? 'x' : 'y';
    if (cs.display.includes('grid')) return 'x';
    const own = getComputedStyle(el).display;
    return own.startsWith('inline') ? 'x' : 'y';
  }

  function computeDrop(page, el, x, y, d) {
    if (!page) return null;
    if (d.kind === 'node' && el) {
      const dragged = page.querySelector(`[data-bs-id="${d.payload}"]`);
      if (dragged && (dragged === el || dragged.contains(el))) return null;
    }
    if (!el) return { targetId: null, pos: 'inside', el: page, box: page.getBoundingClientRect(), axis: 'y' };
    const r = el.getBoundingClientRect();
    const axis = axisOf(el);
    const container = el.dataset.bsContainer === '1';
    const size = axis === 'x' ? r.width : r.height;
    const rel = axis === 'x' ? x - r.left : y - r.top;
    const edge = container ? Math.min(14, size * .25) : size / 2;
    let pos;
    if (container && rel > edge && rel < size - edge) pos = 'inside';
    else pos = rel < size / 2 ? 'before' : 'after';
    return { targetId: el.dataset.bsId, pos, el, box: r, axis };
  }

  function drawIndicator(overlay, ind, info) {
    const sr = overlay.getBoundingClientRect();
    const r = info.box;
    ind.style.display = 'block';
    ind.className = 'bs-drop-ind ' + info.pos + ' ' + info.axis;
    const label = ind.querySelector('span');
    label.textContent = info.el && info.el.dataset ? (info.el.dataset.bsLabel || '') : '';
    if (info.pos === 'inside') {
      Object.assign(ind.style, { left: r.left - sr.left + 'px', top: r.top - sr.top + 'px', width: r.width + 'px', height: r.height + 'px' });
    } else if (info.axis === 'x') {
      const xx = info.pos === 'before' ? r.left - 3 : r.right - 1;
      Object.assign(ind.style, { left: xx - sr.left + 'px', top: r.top - sr.top + 'px', width: '3px', height: r.height + 'px' });
    } else {
      const yy = info.pos === 'before' ? r.top - 3 : r.bottom - 1;
      Object.assign(ind.style, { left: r.left - sr.left + 'px', top: yy - sr.top + 'px', width: r.width + 'px', height: '3px' });
    }
  }

  // ================================================================
  // Monaco
  // ================================================================

  const editors = new Map();
  let themeState = { theme: 'dark', accent: '#4c8dff', scheme: 'rider' };
  const themeName = () => (themeState.scheme === 'rider' ? 'rider-' : 'bs-') + (themeState.theme === 'light' ? 'light' : 'dark');

  const monacoReady = new Promise((resolve) => {
    const start = () => {
      if (typeof window.require !== 'function') { setTimeout(start, 30); return; }
      window.require(['vs/editor/editor.main'], () => {
        registerProviders();
        defineThemes();
        resolve();
      });
    };
    start();
  });

  // Farben wie das Rider-Standardschema (Rider Dark / Rider Light)
  function defineRiderThemes(a) {
    const dark = {
      bg: '262626', fg: 'BDBDBD', kw: '6C95EB', str: 'C9A26D', num: 'ED94C0', com: '85C46C', cls: 'C191FF', struct: 'E1BFFF',
      meth: '39CC9B', mem: '66C3CC', esc: 'D688D4', pre: '787878', tag: '6C95EB', attr: 'BDBDBD', delim: '8A8A8A', razor: 'ED94C0', doc: '608B4E',
    };
    const light = {
      bg: 'FFFFFF', fg: '000000', kw: '0033B3', str: '067D17', num: '1750EB', com: '8C8C8C', cls: '6F2DA8', struct: '6F2DA8',
      meth: '00627A', mem: '871094', esc: '0037A6', pre: '9E880D', tag: '0033B3', attr: '174AD4', delim: '808080', razor: 'B200B2', doc: '8C8C8C',
    };
    for (const [name, c, base] of [['rider-dark', dark, 'vs-dark'], ['rider-light', light, 'vs']]) {
      monaco.editor.defineTheme(name, {
        base, inherit: true,
        rules: [
          { token: '', foreground: c.fg },
          { token: 'keyword', foreground: c.kw }, { token: 'string', foreground: c.str }, { token: 'number', foreground: c.num },
          { token: 'comment', foreground: c.com }, { token: 'type', foreground: c.cls }, { token: 'identifier', foreground: c.fg },
          { token: 'tag', foreground: c.tag }, { token: 'metatag', foreground: c.razor }, { token: 'attribute.name', foreground: c.attr },
          { token: 'attribute.value', foreground: c.str }, { token: 'delimiter', foreground: c.fg }, { token: 'delimiter.bracket', foreground: c.fg }, { token: 'delimiter.parenthesis', foreground: c.fg }, { token: 'delimiter.curly', foreground: c.fg }, { token: 'delimiter.square', foreground: c.fg }, { token: 'delimiter.angle', foreground: c.fg }, { token: 'operator', foreground: c.fg },
          // semantische Tokens (Roslyn)
          { token: 'namespace', foreground: c.fg }, { token: 'class', foreground: c.cls }, { token: 'record', foreground: c.cls },
          { token: 'interface', foreground: c.cls }, { token: 'delegate', foreground: c.cls }, { token: 'typeParameter', foreground: c.cls },
          { token: 'struct', foreground: c.struct }, { token: 'enum', foreground: c.struct }, { token: 'enumMember', foreground: c.mem },
          { token: 'method', foreground: c.meth }, { token: 'property', foreground: c.mem }, { token: 'field', foreground: c.mem },
          { token: 'event', foreground: c.mem }, { token: 'constant', foreground: c.mem }, { token: 'parameter', foreground: c.fg },
          { token: 'variable', foreground: c.fg }, { token: 'controlKeyword', foreground: c.kw }, { token: 'stringEscape', foreground: c.esc },
          { token: 'xmlDocTag', foreground: c.doc }, { token: 'preprocessor', foreground: c.pre }, { token: 'label', foreground: c.fg },
          { token: 'field.static', foreground: c.mem, fontStyle: 'italic' }, { token: 'property.static', foreground: c.mem, fontStyle: 'italic' },
          { token: 'method.static', foreground: c.meth, fontStyle: 'italic' }, { token: 'method.extension', foreground: c.meth },
        ],
        colors: base === 'vs-dark' ? {
          'editor.background': '#' + c.bg, 'editor.foreground': '#' + c.fg, 'editorGutter.background': '#' + c.bg,
          'editorLineNumber.foreground': '#5F5F5F', 'editorLineNumber.activeForeground': '#A3A3A3',
          'editor.lineHighlightBackground': '#2E2E2E', 'editor.lineHighlightBorder': '#00000000',
          'editor.selectionBackground': '#214283', 'editor.inactiveSelectionBackground': '#2A3A52',
          'editorCursor.foreground': '#CFCFCF', 'editorIndentGuide.background1': '#373737', 'editorBracketMatch.background': '#3B514D',
          'editorBracketMatch.border': '#00000000', 'editorWidget.background': '#2B2B2B', 'editorWidget.border': '#3C3C3C',
          'editorSuggestWidget.background': '#2B2B2B', 'editorSuggestWidget.border': '#3C3C3C', 'editorSuggestWidget.selectedBackground': '#2F65CA',
          'editorHoverWidget.background': '#2B2B2B', 'editorHoverWidget.border': '#3C3C3C', 'focusBorder': '#' + a,
          'editor.wordHighlightBackground': '#344134', 'editor.findMatchHighlightBackground': '#32593D', 'scrollbarSlider.background': '#ffffff18',
          'editorOverviewRuler.border': '#00000000',
          'editorBracketHighlight.foreground1': '#' + c.fg, 'editorBracketHighlight.foreground2': '#' + c.fg, 'editorBracketHighlight.foreground3': '#' + c.fg,
          'editorBracketHighlight.foreground4': '#' + c.fg, 'editorBracketHighlight.foreground5': '#' + c.fg, 'editorBracketHighlight.foreground6': '#' + c.fg,
          'editorBracketHighlight.unexpectedBracket.foreground': '#FF6B68',
        } : {
          'editor.background': '#FFFFFF', 'editor.foreground': '#000000', 'editorLineNumber.foreground': '#ADADAD',
          'editorLineNumber.activeForeground': '#555555', 'editor.lineHighlightBackground': '#F5F8FE', 'editor.lineHighlightBorder': '#00000000',
          'editorBracketHighlight.foreground1': '#000000', 'editorBracketHighlight.foreground2': '#000000', 'editorBracketHighlight.foreground3': '#000000',
          'editorBracketHighlight.foreground4': '#000000', 'editorBracketHighlight.foreground5': '#000000', 'editorBracketHighlight.foreground6': '#000000',
          'editor.selectionBackground': '#A6D2FF', 'editorCursor.foreground': '#000000', 'focusBorder': '#' + a,
          'editorSuggestWidget.selectedBackground': '#D5E1FF',
        },
      });
    }
  }

  function defineThemes() {
    defineRiderThemes(themeState.accent.replace('#', ''));
    const a = themeState.accent.replace('#', '');
    monaco.editor.defineTheme('bs-dark', {
      base: 'vs-dark', inherit: true,
      rules: [
        { token: 'tag', foreground: '7ee787' }, { token: 'metatag', foreground: 'ffa657' },
        { token: 'attribute.name', foreground: '79c0ff' }, { token: 'attribute.value', foreground: 'a5d6ff' },
        { token: 'string', foreground: 'a5d6ff' }, { token: 'keyword', foreground: 'ff7b72' },
        { token: 'type', foreground: 'ffa657' }, { token: 'number', foreground: '79c0ff' },
        { token: 'comment', foreground: '8b949e', fontStyle: 'italic' }, { token: 'delimiter', foreground: '8b949e' },
        { token: 'identifier', foreground: 'e6edf3' },
      ],
      colors: {
        'editor.background': '#1a1a1d', 'editor.foreground': '#d6d6dc', 'editorGutter.background': '#1a1a1d',
        'editorLineNumber.foreground': '#55555d', 'editorLineNumber.activeForeground': '#a0a0a8',
        'editor.lineHighlightBackground': '#ffffff08', 'editor.lineHighlightBorder': '#00000000',
        'editorCursor.foreground': '#' + a, 'editor.selectionBackground': '#' + a + '40',
        'editor.inactiveSelectionBackground': '#' + a + '22', 'editorWidget.background': '#222227',
        'editorWidget.border': '#3a3a41', 'editorSuggestWidget.background': '#222227', 'editorSuggestWidget.border': '#3a3a41',
        'editorSuggestWidget.selectedBackground': '#' + a + '30', 'editorHoverWidget.background': '#222227',
        'editorHoverWidget.border': '#3a3a41', 'scrollbarSlider.background': '#ffffff18', 'scrollbarSlider.hoverBackground': '#ffffff28',
        'editorIndentGuide.background1': '#ffffff0d', 'editorBracketMatch.border': '#' + a + '80', 'focusBorder': '#' + a,
        'editorOverviewRuler.border': '#00000000',
      },
    });
    monaco.editor.defineTheme('bs-light', {
      base: 'vs', inherit: true,
      rules: [
        { token: 'tag', foreground: '116329' }, { token: 'metatag', foreground: '953800' },
        { token: 'attribute.name', foreground: '0550ae' }, { token: 'attribute.value', foreground: '0a3069' },
        { token: 'string', foreground: '0a3069' }, { token: 'keyword', foreground: 'cf222e' },
        { token: 'type', foreground: '953800' }, { token: 'number', foreground: '0550ae' },
        { token: 'comment', foreground: '6e7781', fontStyle: 'italic' }, { token: 'delimiter', foreground: '6e7781' },
      ],
      colors: {
        'editor.background': '#ffffff', 'editorGutter.background': '#ffffff', 'editorLineNumber.foreground': '#b0b0b8',
        'editor.lineHighlightBackground': '#00000006', 'editor.lineHighlightBorder': '#00000000',
        'editorCursor.foreground': '#' + a, 'editor.selectionBackground': '#' + a + '30', 'focusBorder': '#' + a,
        'editorSuggestWidget.selectedBackground': '#' + a + '22',
      },
    });
  }

  const kindMap = () => ({
    Element: monaco.languages.CompletionItemKind.Property,
    Component: monaco.languages.CompletionItemKind.Class,
    Attribute: monaco.languages.CompletionItemKind.Field,
    Parameter: monaco.languages.CompletionItemKind.Field,
    Event: monaco.languages.CompletionItemKind.Event,
    Directive: monaco.languages.CompletionItemKind.Keyword,
    Class: monaco.languages.CompletionItemKind.Color,
    CssProperty: monaco.languages.CompletionItemKind.Property,
    CssValue: monaco.languages.CompletionItemKind.Value,
    Member: monaco.languages.CompletionItemKind.Variable,
    Method: monaco.languages.CompletionItemKind.Method,
    Keyword: monaco.languages.CompletionItemKind.Keyword,
    Snippet: monaco.languages.CompletionItemKind.Snippet,
    Token: monaco.languages.CompletionItemKind.Constant,
  });

  function holderForModel(model) {
    for (const h of editors.values()) if (h.editor.getModel() === model) return h;
    return null;
  }

  function registerProviders() {
    const K = monaco.languages.CompletionItemKind;
    const csKinds = {
      'cs.class': K.Class, 'cs.struct': K.Struct, 'cs.interface': K.Interface, 'cs.enum': K.Enum, 'cs.enumMember': K.EnumMember,
      'cs.delegate': K.Function, 'cs.extension': K.Method, 'cs.method': K.Method, 'cs.property': K.Property, 'cs.field': K.Field,
      'cs.event': K.Event, 'cs.constant': K.Constant, 'cs.local': K.Variable, 'cs.parameter': K.Variable, 'cs.namespace': K.Module,
      'cs.typeParameter': K.TypeParameter, 'cs.keyword': K.Keyword, 'cs.snippet': K.Snippet, 'cs.text': K.Text,
    };
    const provider = (language, triggers) => monaco.languages.registerCompletionItemProvider(language, {
      triggerCharacters: triggers,
      provideCompletionItems: async (model, position, context) => {
        const h = holderForModel(model);
        if (!h) return { suggestions: [] };
        const explicit = context.triggerKind === monaco.languages.CompletionTriggerKind.Invoke;
        const res = await h.dotnet.invokeMethodAsync('GetCompletions', model.getValue(), model.getOffsetAt(position), explicit);
        if (!res || !res.items) return { suggestions: [] };
        const km = kindMap();
        const toRange = (s, e) => {
          const a = model.getPositionAt(s), b = model.getPositionAt(Math.max(s, e));
          // Monaco verlangt einzeilige Ersetzungsbereiche, die die Cursorposition enthalten
          if (a.lineNumber !== position.lineNumber) return new monaco.Range(position.lineNumber, position.column, position.lineNumber, position.column);
          const endCol = b.lineNumber === position.lineNumber ? Math.max(b.column, position.column) : position.column;
          return new monaco.Range(a.lineNumber, Math.min(a.column, position.column), position.lineNumber, endCol);
        };
        const defRange = toRange(res.replaceStart, res.replaceEnd);
        return {
          suggestions: res.items.map(i => {
            const s = {
              label: i.importNs ? { label: i.label, description: i.importNs } : i.label,
              kind: csKinds[i.kind] ?? km[i.kind] ?? K.Text,
              insertText: i.insertText,
              insertTextRules: i.isSnippet ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
              detail: i.detail || undefined,
              documentation: i.documentation || undefined,
              sortText: i.sortText || undefined,
              filterText: i.filterText || undefined,
              preselect: i.preselect || undefined,
              range: i.rangeStart != null ? toRange(i.rangeStart, i.rangeEnd) : defRange,
            };
            if (i.edit) {
              const p = model.getPositionAt(i.edit.offset);
              s.additionalTextEdits = [{ range: new monaco.Range(p.lineNumber, p.column, p.lineNumber, p.column), text: i.edit.text }];
            }
            if (i.id != null) s._id = i.id;
            return s;
          }),
        };
      },
      resolveCompletionItem: async (item) => {
        if (item._id == null || item.documentation) return item;
        const h = [...editors.values()].find(x => x.editor.hasTextFocus()) ?? [...editors.values()][0];
        if (!h) return item;
        try {
          const d = await h.dotnet.invokeMethodAsync('DescribeCompletion', item._id);
          if (d) item.documentation = { value: '```csharp\n' + d.split('\n')[0] + '\n```\n' + d.split('\n').slice(1).join('\n') };
        } catch { }
        return item;
      },
    });
    provider('razor', ['<', ' ', '"', '@', ':', '-', ';', '/', '.', '(']);
    provider('csharp', ['.', '(', '<', '[']);

    const hover = {
      provideHover: async (model, position) => {
        const h = holderForModel(model);
        if (!h) return null;
        const word = model.getWordAtPosition(position);
        if (!word) return null;
        // Ausdruck links vom Wort erweitern (a.b.c)
        const line = model.getLineContent(position.lineNumber);
        let start = word.startColumn - 1;
        while (start > 0 && /[\w.]/.test(line[start - 1])) start--;
        const expr = line.substring(start, word.endColumn - 1);
        const text = await h.dotnet.invokeMethodAsync('OnHover', expr);
        if (!text) return null;
        return {
          range: new monaco.Range(position.lineNumber, start + 1, position.lineNumber, word.endColumn),
          contents: [{ value: '```\n' + text + '\n```' }],
        };
      },
    };
    monaco.languages.registerHoverProvider('csharp', hover);
    monaco.languages.registerHoverProvider('razor', hover);

    // Semantische Einfärbung über Roslyn (wie Rider)
    const legend = { tokenTypes: [
      'namespace', 'class', 'struct', 'interface', 'enum', 'enumMember', 'typeParameter', 'delegate', 'method',
      'property', 'field', 'event', 'parameter', 'variable', 'keyword', 'string', 'number', 'comment', 'regexp', 'operator',
      'label', 'constant', 'record', 'xmlDocTag', 'preprocessor', 'controlKeyword', 'stringEscape'], tokenModifiers: ['static', 'declaration', 'extension'] };
    const semantic = {
      getLegend: () => legend,
      provideDocumentSemanticTokens: async (model) => {
        const h = holderForModel(model);
        if (!h) return null;
        try {
          const data = await h.dotnet.invokeMethodAsync('GetSemanticTokens', model.getValue());
          return data ? { data: new Uint32Array(data) } : null;
        } catch { return null; }
      },
      releaseDocumentSemanticTokens: () => { },
    };
    monaco.languages.registerDocumentSemanticTokensProvider('csharp', semantic);
    monaco.languages.registerDocumentSemanticTokensProvider('razor', semantic);
  }

  studio.monaco = {
    ready: () => monacoReady,

    applyTheme(theme, accent) {
      themeState = { ...themeState, theme, accent };
      if (!window.monaco || !monaco.editor) return;
      defineThemes();
      monaco.editor.setTheme(themeName());
    },

    async create(el, id, dotnet, value, language, opts) {
      await monacoReady;
      if (editors.has(id)) studio.monaco.dispose(id);
      const editor = monaco.editor.create(el, {
        value,
        language,
        theme: themeName(),
        'semanticHighlighting.enabled': true,
        automaticLayout: true,
        fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, monospace',
        fontLigatures: true,
        fontSize: opts.fontSize || 13,
        lineHeight: 20,
        minimap: { enabled: !!opts.minimap },
        scrollBeyondLastLine: false,
        renderLineHighlight: 'all',
        smoothScrolling: true,
        cursorSmoothCaretAnimation: 'on',
        cursorBlinking: 'smooth',
        padding: { top: 8, bottom: 8 },
        glyphMargin: !!opts.glyphMargin,
        lineNumbersMinChars: 3,
        folding: true,
        tabSize: 4,
        insertSpaces: true,
        wordWrap: opts.wordWrap ? 'on' : 'off',
        bracketPairColorization: { enabled: false },
        guides: { bracketPairs: false, indentation: true },
        suggest: { showStatusBar: false, preview: true, snippetsPreventQuickSuggestions: false },
        quickSuggestions: { other: true, strings: true, comments: false },
        stickyScroll: { enabled: true },
        scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
        fixedOverflowWidgets: true,
      });
      const h = { editor, dotnet, suppress: false, timer: 0, cursorTimer: 0, decorations: editor.createDecorationsCollection(), dbg: editor.createDecorationsCollection(), sel: editor.createDecorationsCollection() };
      editors.set(id, h);

      editor.onDidChangeModelContent(() => {
        if (h.suppress) return;
        clearTimeout(h.timer);
        h.timer = setTimeout(() => dotnet.invokeMethodAsync('OnCodeChanged', editor.getValue()), 260);
      });
      editor.onDidChangeCursorPosition((e) => {
        clearTimeout(h.cursorTimer);
        h.cursorTimer = setTimeout(() => {
          const offset = editor.getModel().getOffsetAt(e.position);
          dotnet.invokeMethodAsync('OnCursor', offset, e.position.lineNumber, e.position.column, e.source === 'mouse' || e.source === 'keyboard');
        }, 160);
      });
      editor.onMouseDown((e) => {
        const t = e.target.type;
        if (t === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN || t === monaco.editor.MouseTargetType.GUTTER_LINE_DECORATIONS) {
          dotnet.invokeMethodAsync('OnGutterClick', e.target.position.lineNumber);
        }
      });
      // Nach "." sofort Vorschläge (auch wenn Monaco den Trigger wegen offener Liste/Snippet verschluckt)
      editor.onDidType((text) => {
        if (text !== '.' || !['csharp', 'razor'].includes(editor.getModel()?.getLanguageId())) return;
        const pos = editor.getPosition();
        const before = editor.getModel().getLineContent(pos.lineNumber).slice(0, pos.column - 2);
        if (/\d$/.test(before) && !/[A-Za-z_]\w*\d$/.test(before)) return; // Zahl wie 1.5
        setTimeout(() => editor.trigger('dot', 'editor.action.triggerSuggest', {}), 0);
      });

      // Smartes Semikolon wie in Rider/VS: "Foo(x|)" + ; → "Foo(x);"
      editor.onKeyDown((e) => {
        if (e.browserEvent.key !== ';' || e.ctrlKey || e.altKey) return;
        if (!['csharp', 'razor'].includes(editor.getModel()?.getLanguageId())) return;
        const sels = editor.getSelections();
        if (!sels || sels.length !== 1 || !sels[0].isEmpty()) return;
        const pos = editor.getPosition();
        const model = editor.getModel();
        const line = model.getLineContent(pos.lineNumber);
        const before = line.slice(0, pos.column - 1);
        const after = line.slice(pos.column - 1);
        // Nur wenn hinter dem Cursor ausschließlich schließende Klammern stehen
        const m = /^([)\]]+)\s*$/.exec(after);
        if (!m) return;
        // nicht in for(;;), Strings oder Kommentaren
        if (/\bfor\s*\(/.test(before) || /\/\/|\/\*/.test(before)) return;
        if (/^\s*(@?(if|while|switch|foreach|using|lock|catch|fixed)|else\s+if)\s*\(/.test(before)) return;
        const quotes = (before.replace(/\\./g, '').match(/"/g) || []).length;
        if (quotes % 2 === 1) return;
        // Klammern müssen sich auf der Zeile ausgleichen, sonst gehört das ; wirklich hinein
        let depth = 0;
        for (const c of before) { if (c === '(' || c === '[') depth++; else if (c === ')' || c === ']') depth--; }
        if (depth !== m[1].length) return;
        e.preventDefault();
        e.stopPropagation();
        const endCol = pos.column + m[1].length;
        editor.executeEdits('smart-semicolon', [{ range: new monaco.Range(pos.lineNumber, endCol, pos.lineNumber, endCol), text: ';' }]);
        editor.setPosition({ lineNumber: pos.lineNumber, column: endCol + 1 });
        editor.trigger('smart', 'hideSuggestWidget', {});
      });

      // Strg+K im Editor öffnet die Befehlspalette
      editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyK, () => dotnet.invokeMethodAsync('OnEditorShortcut', 'ctrl+k'));
      editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyF, () => dotnet.invokeMethodAsync('OnEditorShortcut', 'ctrl+shift+f'));
      editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyH, () => dotnet.invokeMethodAsync('OnEditorShortcut', 'ctrl+shift+h'));
      editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyB, () => dotnet.invokeMethodAsync('OnEditorShortcut', 'ctrl+shift+b'));
      editor.addCommand(monaco.KeyCode.F9, () => dotnet.invokeMethodAsync('OnGutterClick', editor.getPosition().lineNumber));

      // Navigation wie in Rider: F12, Strg+Klick, Shift+F12, Strg+F12, F2
      const navLang = () => ['csharp', 'razor'].includes(editor.getModel()?.getLanguageId());
      const nav = (kind, position) => {
        if (!navLang()) return;
        const model = editor.getModel();
        const offset = model.getOffsetAt(position || editor.getPosition());
        dotnet.invokeMethodAsync('OnNavigate', kind, model.getValue(), offset);
      };
      const navActions = [
        ['bs.definition', 'Gehe zu Definition', [monaco.KeyCode.F12], 'definition', 1],
        ['bs.usages', 'Verwendungen suchen', [monaco.KeyMod.Shift | monaco.KeyCode.F12, monaco.KeyMod.Alt | monaco.KeyCode.F7], 'usages', 2],
        ['bs.implementation', 'Gehe zu Implementierung', [monaco.KeyMod.CtrlCmd | monaco.KeyCode.F12], 'implementation', 3],
        ['bs.rename', 'Umbenennen …', [monaco.KeyCode.F2, monaco.KeyMod.Shift | monaco.KeyCode.F6], 'rename', 4],
      ];
      const labels = (opts && opts.navLabels) || {};
      for (const [aid, label, keys, kind, order] of navActions) {
        editor.addAction({
          id: aid,
          label: labels[kind] || label,
          keybindings: keys,
          contextMenuGroupId: '0_navigation',
          contextMenuOrder: order,
          precondition: null,
          run: () => nav(kind),
        });
      }
      // Strg+Klick → Definition, Strg+Hover unterstreicht den Bezeichner
      const linkDeco = editor.createDecorationsCollection();
      editor.onMouseDown((e) => {
        if (!(e.event.ctrlKey || e.event.metaKey) || e.event.leftButton === false) return;
        if (e.target.type !== monaco.editor.MouseTargetType.CONTENT_TEXT || !e.target.position) return;
        e.event.preventDefault();
        e.event.stopPropagation();
        linkDeco.clear();
        nav('definition', e.target.position);
      });
      editor.onMouseMove((e) => {
        if (!(e.event.ctrlKey || e.event.metaKey) || !navLang() || e.target.type !== monaco.editor.MouseTargetType.CONTENT_TEXT || !e.target.position) { linkDeco.clear(); return; }
        const w = editor.getModel().getWordAtPosition(e.target.position);
        if (!w) { linkDeco.clear(); return; }
        linkDeco.set([{ range: new monaco.Range(e.target.position.lineNumber, w.startColumn, e.target.position.lineNumber, w.endColumn), options: { inlineClassName: 'bs-nav-link' } }]);
      });
      editor.onMouseLeave(() => linkDeco.clear());
      editor.onKeyUp((e) => { if (e.keyCode === monaco.KeyCode.Ctrl) linkDeco.clear(); });
    },

    // Bereich markieren und in die Mitte scrollen (Gehe zu Definition, Treffer)
    revealRange(id, start, length) {
      const h = editors.get(id);
      if (!h) return;
      const model = h.editor.getModel();
      const a = model.getPositionAt(start), b = model.getPositionAt(start + Math.max(0, length));
      h.editor.setSelection(monaco.Range.fromPositions(a, b));
      h.editor.revealRangeInCenterIfOutsideViewport(monaco.Range.fromPositions(a, b));
      h.editor.focus();
    },

    setValue(id, value) {
      const h = editors.get(id);
      if (!h) return;
      const model = h.editor.getModel();
      const old = model.getValue();
      if (old === value) return;
      // nur den geänderten Mittelteil ersetzen, damit Cursor und Scrollposition bleiben
      let s = 0;
      const max = Math.min(old.length, value.length);
      while (s < max && old.charCodeAt(s) === value.charCodeAt(s)) s++;
      let eo = old.length, en = value.length;
      while (eo > s && en > s && old.charCodeAt(eo - 1) === value.charCodeAt(en - 1)) { eo--; en--; }
      const range = monaco.Range.fromPositions(model.getPositionAt(s), model.getPositionAt(eo));
      h.suppress = true;
      try {
        h.editor.pushUndoStop();
        h.editor.executeEdits('designer', [{ range, text: value.substring(s, en), forceMoveMarkers: true }]);
        h.editor.pushUndoStop();
      } finally {
        h.suppress = false;
      }
    },

    getValue(id) {
      const h = editors.get(id);
      return h ? h.editor.getValue() : null;
    },

    setLanguage(id, lang) {
      const h = editors.get(id);
      if (h) monaco.editor.setModelLanguage(h.editor.getModel(), lang);
    },

    setOptions(id, opts) {
      const h = editors.get(id);
      if (h) h.editor.updateOptions({ fontSize: opts.fontSize, minimap: { enabled: !!opts.minimap }, readOnly: !!opts.readOnly });
    },

    revealLine(id, line, focus) {
      const h = editors.get(id);
      if (!h) return;
      h.editor.revealLineInCenterIfOutsideViewport(line);
      h.editor.setPosition({ lineNumber: line, column: 1 });
      if (focus) h.editor.focus();
    },

    // Bereich des ausgewählten Knotens markieren
    highlightRange(id, start, end, reveal) {
      const h = editors.get(id);
      if (!h) return;
      const model = h.editor.getModel();
      if (start < 0) { h.sel.clear(); return; }
      const a = model.getPositionAt(start), b = model.getPositionAt(end);
      h.sel.set([
        { range: new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column), options: { className: 'bs-node-range', isWholeLine: false } },
        { range: new monaco.Range(a.lineNumber, 1, b.lineNumber, 1), options: { isWholeLine: true, linesDecorationsClassName: 'bs-node-gutter' } },
      ]);
      if (reveal) h.editor.revealRangeInCenterIfOutsideViewport(new monaco.Range(a.lineNumber, 1, b.lineNumber, 1), 1);
    },

    setMarkers(id, markers) {
      const h = editors.get(id);
      if (!h) return;
      const model = h.editor.getModel();
      monaco.editor.setModelMarkers(model, 'blazor-studio', markers.map(m => {
        const a = model.getPositionAt(m.start), b = model.getPositionAt(Math.max(m.end, m.start + 1));
        return {
          severity: m.error ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,
          message: m.message,
          startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column,
        };
      }));
    },

    setLineMarkers(id, markers) {
      const h = editors.get(id);
      if (!h) return;
      const model = h.editor.getModel();
      monaco.editor.setModelMarkers(model, 'build', markers.map(m => ({
        severity: m.error ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,
        message: m.message,
        startLineNumber: m.line, startColumn: m.column, endLineNumber: m.line, endColumn: model.getLineMaxColumn(Math.min(m.line, model.getLineCount())),
      })));
    },

    // Haltepunkte, aktuelle Zeile und Inline-Werte
    setDebug(id, breakpoints, currentLine, inlineValues) {
      const h = editors.get(id);
      if (!h) return;
      const decos = breakpoints.map(l => ({
        range: new monaco.Range(l, 1, l, 1),
        options: { glyphMarginClassName: 'bs-bp', isWholeLine: true, className: l === currentLine ? '' : 'bs-bp-line', stickiness: 1 },
      }));
      if (currentLine > 0) {
        decos.push({ range: new monaco.Range(currentLine, 1, currentLine, 1), options: { isWholeLine: true, className: 'bs-cur-line', glyphMarginClassName: breakpoints.includes(currentLine) ? 'bs-bp bs-cur' : 'bs-cur' } });
        h.editor.revealLineInCenterIfOutsideViewport(currentLine);
      }
      const model = h.editor.getModel();
      for (const iv of inlineValues || []) {
        if (iv.line < 1 || iv.line > model.getLineCount()) continue;
        const col = model.getLineMaxColumn(iv.line);
        decos.push({ range: new monaco.Range(iv.line, col, iv.line, col), options: { after: { content: '  ' + iv.text, inlineClassName: 'bs-inline-value' } } });
      }
      h.dbg.set(decos);
    },

    focus(id) { const h = editors.get(id); if (h) h.editor.focus(); },

    layout(id) { const h = editors.get(id); if (h) h.editor.layout(); },

    action(id, actionId) {
      const h = editors.get(id);
      if (!h) return;
      h.editor.focus();
      const a = h.editor.getAction(actionId);
      if (a) a.run();
    },

    dispose(id) {
      const h = editors.get(id);
      if (!h) return;
      clearTimeout(h.timer);
      h.editor.getModel()?.dispose();
      h.editor.dispose();
      editors.delete(id);
    },
  };
})();
