// ═══════════════════════════════════════════════════════════════════
//  D3-EDITOR.JS — RGBL Eğri Editörü
// ═══════════════════════════════════════════════════════════════════

window._es = 1;

const CHANNELS = {
    R:  { color: '#f87171', dashed: false, width: 2,   editable: true  },
    G:  { color: '#4ade80', dashed: false, width: 2,   editable: true  },
    B:  { color: '#60a5fa', dashed: false, width: 2,   editable: true  },
    L:  { color: '#e2e8f0', dashed: true,  width: 3.5, editable: true  },
    T:  { color: '#fbbf24', dashed: false, width: 2.5, editable: true  },
    LT: { color: '#c084fc', dashed: true,  width: 2,   editable: false },
};
const EDITABLE_CHS = Object.keys(CHANNELS).filter(ch => CHANNELS[ch].editable);

let _nid = 0;
function mkNode(x, y, fixed = false) { return { id: ++_nid, x, y, fixed }; }

function defaultNodes() {
    return {
        R: [mkNode(0, 20, true), mkNode(100, 88, true)],
        G: [mkNode(0, 15, true), mkNode(100, 82, true)],
        B: [mkNode(0, 28, true), mkNode(100, 68, true)],
        L: [mkNode(0, 10, true), mkNode(50, 55), mkNode(100, 95, true)],
        T: [mkNode(0, 3,  true), mkNode(50, 100), mkNode(100, 3, true)],
    };
}

const LS_KEY = 'rgbl_calibration';

function saveToLocalStorage() {
    const data = {};
    EDITABLE_CHS.forEach(ch => {
        data[ch] = nodes[ch].map(n => ({ x: n.x, y: n.y, fixed: n.fixed }));
    });
    try { localStorage.setItem(LS_KEY, JSON.stringify(data)); } catch(e) {}
}

function loadFromLocalStorage() {
    try {
        const raw = localStorage.getItem(LS_KEY);
        if (!raw) return null;
        const data = JSON.parse(raw);
        if (!data.T && data.LT) data.T = data.LT;
        const result = {};
        let valid = true;
        EDITABLE_CHS.forEach(ch => {
            if (!data[ch] || !Array.isArray(data[ch]) || data[ch].length < 2) {
                valid = false; return;
            }
            result[ch] = data[ch].map(n => mkNode(
                parseFloat(n.x) || 0, parseFloat(n.y) || 0, !!n.fixed));
        });
        return valid ? result : null;
    } catch(e) { return null; }
}

let nodes = loadFromLocalStorage() || defaultNodes();
let calcMode = localStorage.getItem('rgbl_calc_mode') || 'absolute';

let _ltDirty = true;
let _ltPoints = [];
function _invalidateLT() { _ltDirty = true; }

const margin = { top: 20, right: 20, bottom: 40, left: 50 };
const wrap = document.getElementById('d3-wrap');
let W = (wrap ? wrap.clientWidth : 480) - margin.left - margin.right;
let H = (wrap ? wrap.clientHeight : 340) - margin.top - margin.bottom;
if (W < 100) W = 410;
if (H < 100) H = 280;

const svg = d3.select('#d3-wrap')
    .append('svg')
    .attr('width', W + margin.left + margin.right)
    .attr('height', H + margin.top + margin.bottom);

const g = svg.append('g')
    .attr('transform', `translate(${margin.left},${margin.top})`);

const xSc = d3.scaleLinear().domain([0, 100]).range([0, W]);
const ySc = d3.scaleLinear().domain([0, 100]).range([H, 0]);

const grid = g.append('g').attr('class', 'grid');
function _drawGrid() {
    grid.selectAll('line').remove();
    [0, 25, 50, 75, 100].forEach(v => {
        grid.append('line')
            .attr('x1', xSc(v)).attr('x2', xSc(v)).attr('y1', 0).attr('y2', H)
            .style('stroke', '#334155').style('stroke-width', '1')
            .style('stroke-dasharray', v === 0 || v === 100 ? 'none' : '4,4');
        grid.append('line')
            .attr('x1', 0).attr('x2', W).attr('y1', ySc(v)).attr('y2', ySc(v))
            .style('stroke', '#334155').style('stroke-width', '1')
            .style('stroke-dasharray', v === 0 || v === 100 ? 'none' : '4,4');
    });
}
_drawGrid();

const xAxisEl = g.append('g').attr('transform', `translate(0,${H})`).call(d3.axisBottom(xSc).ticks(5));
xAxisEl.selectAll('text').style('fill', '#94a3b8').style('font-size', '11px');
const yAxisEl = g.append('g').call(d3.axisLeft(ySc).ticks(5));
yAxisEl.selectAll('text').style('fill', '#94a3b8').style('font-size', '11px');

const xAxisLabel = g.append('text')
    .attr('x', W / 2).attr('y', H + 35)
    .attr('text-anchor', 'middle').style('fill', '#64748b').style('font-size', '11px')
    .text('Ortam Işığı (%)');

g.append('text')
    .attr('transform', 'rotate(-90)').attr('x', -H / 2).attr('y', -38)
    .attr('text-anchor', 'middle').style('fill', '#64748b').style('font-size', '11px')
    .text('Çıkış (%)');

function resize() {
    if (!wrap) return;
    const nw = wrap.clientWidth - margin.left - margin.right;
    const nh = wrap.clientHeight - margin.top - margin.bottom;
    if (nw < 50 || nh < 50) return;
    W = nw; H = nh;
    svg.attr('width', W + margin.left + margin.right)
       .attr('height', H + margin.top + margin.bottom);
    xSc.range([0, W]); ySc.range([H, 0]);
    _drawGrid();
    xAxisEl.attr('transform', `translate(0,${H})`).call(d3.axisBottom(xSc).ticks(5));
    xAxisEl.selectAll('text').style('fill', '#94a3b8').style('font-size', '11px');
    yAxisEl.call(d3.axisLeft(ySc).ticks(5));
    yAxisEl.selectAll('text').style('fill', '#94a3b8').style('font-size', '11px');
    xAxisLabel.attr('x', W / 2).attr('y', H + 35);
    xhLine.attr('y2', H);
    yhLine.attr('x2', W);
    _invalidateLT();
    renderAll();
}
if (wrap) new ResizeObserver(() => resize()).observe(wrap);

const pathLayer = g.append('g').attr('class', 'paths');
const nodeLayer = g.append('g').attr('class', 'nodes');
const crossLayer = g.append('g').attr('class', 'crosshairs');

const curvePaths = {};
Object.keys(CHANNELS).forEach(ch => {
    curvePaths[ch] = pathLayer.append('path')
        .attr('fill', 'none')
        .attr('stroke', CHANNELS[ch].color)
        .attr('stroke-width', CHANNELS[ch].width)
        .attr('stroke-dasharray', CHANNELS[ch].dashed ? '7,4' : 'none')
        .attr('opacity', 0)
        .style('pointer-events', 'none');
});

const lineGen = d3.line().x(d => xSc(d.x)).y(d => ySc(d.y)).curve(d3.curveCatmullRom.alpha(0.5));
const lineGenMono = d3.line().x(d => xSc(d.x)).y(d => ySc(d.y)).curve(d3.curveMonotoneX);

const xhLine = crossLayer.append('line').attr('y1', 0).attr('y2', H)
    .style('stroke', 'rgba(255,255,255,0.4)').style('stroke-width', '1.2')
    .style('stroke-dasharray', '3,3').style('pointer-events', 'none');

const yhLine = crossLayer.append('line').attr('x1', 0).attr('x2', W)
    .style('stroke', 'rgba(255,255,255,0.4)').style('stroke-width', '1.2')
    .style('stroke-dasharray', '3,3').style('pointer-events', 'none');

const xValueMarker = document.getElementById('x-value-marker');
const yValueMarker = document.getElementById('y-value-marker');

let activeCh = 'L';
let isTimeMode = false;

function getSimMode() {
    const el = document.querySelector('input[name="simMode"]:checked');
    return el ? el.value : 'sensor';
}

function getLT(xVal) {
    return sampleAtX('L', sampleAtX('T', xVal));
}

function sampleAtX(ch, xVal) {
    
    // // LT ÖZEL DURUMU Eklemeye gerek yok çünkü kendi fonksiyonu var. (zorunlu, çünkü nodes.LT yok)
    // if (ch === 'LT') {
    //     // LT(x) = L(T(x))
    //     const tAmb = sampleAtX('T', xVal);
    //     return sampleAtX('L', tAmb);
    // }
    

    const points = nodes[ch];
    if (!points || points.length < 2) return Math.max(0, Math.min(100, xVal));
    
    const sorted = [...points].sort((a, b) => a.x - b.x);
    xVal = Math.max(0, Math.min(100, xVal));
    
    if (xVal <= sorted[0].x) return Math.max(0, Math.min(100, sorted[0].y));
    if (xVal >= sorted[sorted.length - 1].x) return Math.max(0, Math.min(100, sorted[sorted.length - 1].y));
    
    for (let i = 0; i < sorted.length - 1; i++) {
        const p1 = sorted[i];
        const p2 = sorted[i + 1];
        if (xVal >= p1.x && xVal <= p2.x) {
            const t = (xVal - p1.x) / (p2.x - p1.x);
            const y = p1.y + t * (p2.y - p1.y);
            return Math.max(0, Math.min(100, y));
        }
    }
    return Math.max(0, Math.min(100, xVal));
}

function _renderLT() {
    if (_ltDirty) {
        _ltPoints = [];
        // console.log("LT yeniden hesaplanıyor..."); // DEBUG
        for (let xi = 0; xi <= 100; xi += 2) {
            const tAmb = sampleAtX('T', xi);
            const lVal = sampleAtX('L', tAmb);
            _ltPoints.push({ x: xi, y: lVal });
        }
        // console.log("LT noktaları:", _ltPoints.slice(0, 5)); // DEBUG
        _ltDirty = false;
    }
    
    curvePaths['LT']
        .datum(_ltPoints)
        .attr('d', d3.line().x(d => xSc(d.x)).y(d => ySc(d.y)).curve(d3.curveLinear))
        .attr('opacity', 0.85);
}

function clampLMonotone(node, newY) {
    const sorted = [...nodes.L].sort((a, b) => a.x - b.x);
    const idx = sorted.findIndex(n => n.id === node.id);
    const minY = idx > 0 ? Math.max(0, sorted[idx - 1].y) : 0;
    const maxY = idx < sorted.length - 1 ? Math.min(100, sorted[idx + 1].y) : 100;
    return Math.max(minY, Math.min(maxY, newY));
}

function renderAll() {
    xAxisLabel.text(isTimeMode ? 'Saat (0h → 24h)' : 'Ortam Işığı (%)');

    EDITABLE_CHS.forEach(ch => {
        const gen = ch === 'L' ? lineGenMono : lineGen;
        const sorted = [...nodes[ch]].sort((a, b) => a.x - b.x);
        curvePaths[ch].datum(sorted).attr('d', gen).attr('opacity', 1);
    });

    _renderLT();

    EDITABLE_CHS.forEach(ch => {
        const isActive = ch === activeCh;
        const sorted = [...nodes[ch]].sort((a, b) => a.x - b.x);

        const sel = nodeLayer.selectAll(`.node-${ch}`).data(sorted, d => d.id);

        sel.enter()
            .append('circle')
            .attr('class', `node-dot node-${ch}`)
            .style('fill', CHANNELS[ch].color)
            .style('stroke', '#1e293b').style('stroke-width', '2')
            .style('cursor', d => d.fixed ? 'ns-resize' : 'grab')
            .call(makeDrag(ch))
            .on('contextmenu', (ev, d) => {
                ev.preventDefault();
                if (d.fixed || nodes[ch].length <= 2) return;
                nodes[ch] = nodes[ch].filter(n => n.id !== d.id);
                if (ch === 'L' || ch === 'T') _invalidateLT();
                saveToLocalStorage();
                renderAll();
                window._updateSimulation();
            })
            .merge(sel)
            .attr('cx', d => xSc(d.x))
            .attr('cy', d => ySc(d.y))
            .attr('r', d => isActive ? 7 : 5);

        sel.exit().remove();
    });

    document.querySelectorAll('.ch-btn').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.ch === activeCh);
    });
}

function makeDrag(ch) {
    return d3.drag()
        .on('start', function(event) { d3.select(this).style('cursor', 'grabbing'); })
        .on('drag', function(event, d) {
            const [px, py] = d3.pointer(event, g.node());
            const mx = xSc.invert(px);
            const my = Math.max(0, Math.min(100, ySc.invert(py)));

            if (d.fixed) {
                d.y = (ch === 'L') ? clampLMonotone(d, my) : my;
            } else {
                d.x = Math.max(1, Math.min(99, mx));
                d.y = (ch === 'L') ? clampLMonotone(d, my) : my;
            }
            if (ch === 'L' || ch === 'T') _invalidateLT();
            renderAll();
            window._updateSimulation();
        })
        .on('end', function(event, d) {
            d3.select(this).style('cursor', d.fixed ? 'ns-resize' : 'grab');
            saveToLocalStorage();
        });
}

svg.on('click', function(ev) {
    if (!CHANNELS[activeCh] || !CHANNELS[activeCh].editable) return;
    const [px, py] = d3.pointer(ev, g.node());
    const nx = xSc.invert(px);
    const ny = ySc.invert(py);
    if (nx < 1 || nx > 99 || ny < 0 || ny > 100) return;
    if (nodes[activeCh].some(n => Math.abs(n.x - nx) < 3)) return;
    nodes[activeCh].push(mkNode(nx, ny, false));
    if (activeCh === 'L' || activeCh === 'T') _invalidateLT();
    saveToLocalStorage();
    renderAll();
    window._updateSimulation();
});

let currentAmbient = 50;

svg.on('mousemove', function(ev) {
    const [px, py] = d3.pointer(ev, g.node());
    currentAmbient = Math.max(0, Math.min(100, xSc.invert(px)));
    const currentY = Math.max(0, Math.min(100, ySc.invert(py)));
    
    xhLine.attr('x1', xSc(currentAmbient)).attr('x2', xSc(currentAmbient));
    yhLine.attr('y1', ySc(currentY)).attr('y2', ySc(currentY));
    
    if (xValueMarker) {
        const svgRect = svg.node().getBoundingClientRect();
        const markerX = svgRect.left + margin.left + xSc(currentAmbient);
        const markerY = svgRect.top + margin.top + H + 5;
        xValueMarker.style.display = 'block';
        xValueMarker.style.left = markerX + 'px';
        xValueMarker.style.top = markerY + 'px';
        xValueMarker.textContent = Math.round(currentAmbient) + '%';
    }
    
    if (yValueMarker) {
        const svgRect = svg.node().getBoundingClientRect();
        const markerX = svgRect.left + margin.left - 35;
        const markerY = svgRect.top + margin.top + ySc(currentY);
        yValueMarker.style.display = 'block';
        yValueMarker.style.left = markerX + 'px';
        yValueMarker.style.top = markerY + 'px';
        yValueMarker.textContent = Math.round(currentY) + '%';
    }
    
    window._updateSimulation();
});

svg.on('mouseleave', function() {
    xhLine.attr('x1', -10).attr('x2', -10);
    yhLine.attr('y1', -10).attr('y2', -10);
    if (xValueMarker) xValueMarker.style.display = 'none';
    if (yValueMarker) yValueMarker.style.display = 'none';
});

window._updateSimulation = function() {
    const x = currentAmbient;
    const tVal = sampleAtX('T', x);
    const effAmb = isTimeMode ? tVal : x;
    const ltVal = getLT(x);
    const cR = sampleAtX('R', effAmb);
    const cG = sampleAtX('G', effAmb);
    const cB = sampleAtX('B', effAmb);
    const cL = sampleAtX('L', effAmb);

    let fR, fG, fB;
    if (calcMode === 'ratio') {
        const sum = cR + cG + cB;
        if (sum > 0.001) { fR = cR/sum*cL; fG = cG/sum*cL; fB = cB/sum*cL; }
        else { fR = fG = fB = cL / 3; }
    } else {
        fR = cR * (cL / 100);
        fG = cG * (cL / 100);
        fB = cB * (cL / 100);
    }

    if (typeof window.updateEnvironment === 'function') {
        window.updateEnvironment(currentAmbient, fR, fG, fB, cL, ltVal);
    }

    const set = (id, v) => { const el = document.getElementById(id); if (el) el.textContent = Math.round(v); };
    const lbl = document.getElementById('lbl-xpos');
    if (lbl) lbl.textContent = isTimeMode ? 'Saat' : 'Ortam';
    
    set('disp-amb', x);
    set('disp-t', tVal);
    set('disp-lt', ltVal);
    set('disp-r', fR);
    set('disp-g', fG);
    set('disp-b', fB);
    set('disp-l', cL);
};

document.querySelectorAll('.ch-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        const ch = btn.dataset.ch;
        if (!CHANNELS[ch] || !CHANNELS[ch].editable) return;
        activeCh = ch;
        renderAll();
    });
});

function _updateCalcBtn() {
    const btn = document.getElementById('calc-mode-btn');
    if (!btn) return;
    btn.textContent = calcMode === 'ratio' ? '⚡ Oran' : '⚡ Mutlak';
}
const calcBtn = document.getElementById('calc-mode-btn');
if (calcBtn) {
    _updateCalcBtn();
    calcBtn.addEventListener('click', () => {
        calcMode = calcMode === 'ratio' ? 'absolute' : 'ratio';
        localStorage.setItem('rgbl_calc_mode', calcMode);
        _updateCalcBtn();
        window._updateSimulation();
    });
}

// ── Save-to-file logic ─────────────────────────────────────────────────────────
//
// Strateji:
//   • Kullanıcı ilk kez "Kaydet"e basınca bir dosya seçici açılır (tek seferlik).
//   • Seçilen FileSystemFileHandle IndexedDB'de saklanır ve page reload'dan sonra
//     geri yüklenir; bu sayede sonraki tüm kayıtlar sessizce aynı dosyaya yazılır.
//   • localStorage'da "korumalı ilk yedek" + 9 döner yedek = toplam 10 yedek.
//   • "📁 Konum Değiştir" butonu yeni bir konum seçmek için kullanılabilir.
//   • File System Access API yoksa indirme yöntemine geri döner.
// ──────────────────────────────────────────────────────────────────────────────

// ── IndexedDB yardımcıları ────────────────────────────────────────────────────
const _IDB = (() => {
    const DB = 'rgbl_editor_db', STORE = 'kv', VER = 1;
    const open = () => new Promise((res, rej) => {
        const r = indexedDB.open(DB, VER);
        r.onupgradeneeded = e => e.target.result.createObjectStore(STORE);
        r.onsuccess = e => res(e.target.result);
        r.onerror   = () => rej(r.error);
    });
    return {
        async get(key) {
            try {
                const db = await open();
                return await new Promise(res =>
                    db.transaction(STORE).objectStore(STORE).get(key).onsuccess =
                        e => res(e.target.result ?? null)
                );
            } catch { return null; }
        },
        async set(key, val) {
            try {
                const db = await open();
                await new Promise((res, rej) => {
                    const tx = db.transaction(STORE, 'readwrite');
                    tx.objectStore(STORE).put(val, key);
                    tx.oncomplete = res;
                    tx.onerror    = () => rej(tx.error);
                });
            } catch (e) { console.warn('idb.set:', e); }
        },
        async del(key) {
            try {
                const db = await open();
                await new Promise((res, rej) => {
                    const tx = db.transaction(STORE, 'readwrite');
                    tx.objectStore(STORE).delete(key);
                    tx.oncomplete = res;
                    tx.onerror    = () => rej(tx.error);
                });
            } catch (e) { console.warn('idb.del:', e); }
        },
    };
})();

// ── localStorage yedekleme (10 slot) ─────────────────────────────────────────
const BACKUP_LS_KEY  = 'rgbl_cal_backups';    // döner yedekler (9 adet)
const INITIAL_LS_KEY = 'rgbl_cal_initial';    // korumalı ilk yedek (hiç silinmez)
const MAX_ROT_BACKUPS = 9;

function rotateBackup(jsonStr) {
    // İlk kayıt → korumalı slota yaz (bir kez, sonsuza kadar saklı)
    if (!localStorage.getItem(INITIAL_LS_KEY)) {
        try {
            localStorage.setItem(INITIAL_LS_KEY,
                JSON.stringify({ ts: new Date().toISOString(), data: jsonStr }));
        } catch {}
    }
    // Döner halka (en yeni başa, en eski silinir)
    try {
        let b = [];
        try { b = JSON.parse(localStorage.getItem(BACKUP_LS_KEY) || '[]'); } catch {}
        b.unshift({ ts: new Date().toISOString(), data: jsonStr });
        if (b.length > MAX_ROT_BACKUPS) b.length = MAX_ROT_BACKUPS;
        localStorage.setItem(BACKUP_LS_KEY, JSON.stringify(b));
    } catch {}
}

// ── Durum flash mesajı ────────────────────────────────────────────────────────
function flashSaveStatus(msg, color = '#4ade80', ms = 2500) {
    const el = document.getElementById('save-status');
    if (!el) return;
    el.textContent   = msg;
    el.style.color   = color;
    el.style.opacity = '1';
    clearTimeout(el._t);
    el._t = setTimeout(() => { el.style.opacity = '0'; }, ms);
}

// ── JSON oluşturucu ───────────────────────────────────────────────────────────
function buildCalibrationJson() {
    const ltPoints = [];
    for (let xi = 0; xi <= 100; xi += 2) {
        const tAmb = sampleAtX('T', xi);
        const lVal = sampleAtX('L', tAmb);
        ltPoints.push({ x: +xi.toFixed(2), y: +lVal.toFixed(2), fixed: false });
    }
    const data = {
        R: nodes.R.map(n => ({ x: +n.x.toFixed(2), y: +n.y.toFixed(2), fixed: n.fixed })),
        G: nodes.G.map(n => ({ x: +n.x.toFixed(2), y: +n.y.toFixed(2), fixed: n.fixed })),
        B: nodes.B.map(n => ({ x: +n.x.toFixed(2), y: +n.y.toFixed(2), fixed: n.fixed })),
        L: ltPoints,
    };
    return JSON.stringify({ version: 8, calcMode, channels: data }, null, 2);
}

// ── İndirme fallback ──────────────────────────────────────────────────────────
function downloadJson(jsonStr) {
    const a   = document.createElement('a');
    a.href    = URL.createObjectURL(new Blob([jsonStr], { type: 'application/json' }));
    a.download = 'rgbl_calibration.json';
    a.click();
    URL.revokeObjectURL(a.href);
}

// ── FileSystemFileHandle yönetimi ─────────────────────────────────────────────
const IDB_HANDLE_KEY = 'save_handle';
let _fileHandle = null;   // bellekteki cache; sayfa yüklenince IDB'den restore edilir

/**
 * Geçerli bir yazılabilir handle sağlar.
 * forcePick=true → daima yeni dosya seçici açar.
 * İlk kullanımda seçici bir kez açılır, handle IDB'de saklanır.
 * Sonraki ziyaretlerde IDB'den yüklenir, seçici çıkmaz.
 */
async function ensureHandle(forcePick = false) {
    if (!window.showSaveFilePicker) return false;

    // IDB'den yükle (bellek boşsa)
    if (!_fileHandle && !forcePick) {
        _fileHandle = await _IDB.get(IDB_HANDLE_KEY);
    }

    // Yeni konum seçimi gerekiyorsa (ya hiç seçilmemiş ya da zorla)
    if (!_fileHandle || forcePick) {
        try {
            _fileHandle = await window.showSaveFilePicker({
                suggestedName: 'rgbl_calibration.json',
                types: [{ description: 'JSON Kalibrasyon', accept: { 'application/json': ['.json'] } }],
            });
            await _IDB.set(IDB_HANDLE_KEY, _fileHandle);
        } catch (e) {
            if (e.name !== 'AbortError') console.error('Dosya seçici hatası:', e);
            return false;   // kullanıcı iptal etti
        }
    }

    // İzin kontrolü (sayfa yenilendikten sonra gerekebilir)
    try {
        let perm = await _fileHandle.queryPermission({ mode: 'readwrite' });
        if (perm !== 'granted')
            perm = await _fileHandle.requestPermission({ mode: 'readwrite' });
        if (perm !== 'granted') {
            flashSaveStatus('✗ Dosya erişim izni reddedildi', '#f87171', 3000);
            return false;
        }
    } catch {
        // Eski tarayıcıda queryPermission yoksa geç
    }
    return true;
}

// ── Ana kaydet işleyicisi ─────────────────────────────────────────────────────
async function handleSave(forcePick = false) {
    const jsonStr = buildCalibrationJson();

    // File System Access API desteklenmiyor → indirme ile fallback
    if (!window.showSaveFilePicker) {
        downloadJson(jsonStr);
        rotateBackup(jsonStr);
        flashSaveStatus('✓ İndirildi (API desteklenmiyor)');
        return;
    }

    const ok = await ensureHandle(forcePick);
    if (!ok) return;

    try {
        const writable = await _fileHandle.createWritable();
        await writable.write(jsonStr);
        await writable.close();
        rotateBackup(jsonStr);
        flashSaveStatus(`✓ ${_fileHandle.name ?? 'rgbl_calibration.json'}`);
    } catch (e) {
        console.error('Yazma hatası:', e);
        // Handle geçersiz olabilir; sıfırla ki bir sonraki kayıtta yeniden seçsin
        _fileHandle = null;
        await _IDB.del(IDB_HANDLE_KEY);
        flashSaveStatus('✗ Yazma hatası — tekrar deneyin', '#f87171', 3000);
    }
}

const exportBtn = document.getElementById('export-btn');
if (exportBtn) exportBtn.addEventListener('click', () => handleSave(false));

const pickFileBtn = document.getElementById('pick-file-btn');
if (pickFileBtn) pickFileBtn.addEventListener('click', () => handleSave(true));

const resetBtn = document.getElementById('reset-btn');
if (resetBtn) {
    resetBtn.addEventListener('click', () => {
        if (!confirm('Tüm eğriler sıfırlansın mı?')) return;
        localStorage.removeItem(LS_KEY);
        nodes = defaultNodes();
        _invalidateLT();
        renderAll();
        window._updateSimulation();
    });
}

document.querySelectorAll('input[name="simMode"]').forEach(radio => {
    radio.addEventListener('change', () => {
        isTimeMode = radio.value === 'time';
        currentAmbient = 50;
        if (isTimeMode && activeCh === 'L') activeCh = 'T';
        if (!isTimeMode && activeCh === 'T') activeCh = 'L';
        renderAll();
        window._updateSimulation();
    });
});

(function init() {
    isTimeMode = getSimMode() === 'time';
    if (isTimeMode && activeCh === 'L') activeCh = 'T';
    if (!isTimeMode && activeCh === 'T') activeCh = 'L';
    renderAll();
    window._updateSimulation();
})();
