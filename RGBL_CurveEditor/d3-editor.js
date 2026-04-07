// ═══════════════════════════════════════════════════════════════════
//  D3-EDITOR.JS — RGBL Eğri Editörü  (v4)
//
//  Mimari:
//    L   = L_sensor   : ortam ışığı → parlaklık  [monoton ↑, sensör modunda düzenlenir]
//    T   = Gün Profili: saat → ortam eşdeğeri    [saat modunda düzenlenir]
//    LT  = L(T(t))    : TÜRETİLMİŞ, saat modunda gösterilir, düzenlenemez
//    RGB : ortak "ortam" uzayında, her 2 modda da düzenlenebilir
//
//  Bağlantı (her iki yön):
//    L_sensor değişince → LT otomatik güncellenir  (LT = L_sensor ∘ T)
//    T değişince        → LT otomatik güncellenir  (LT = L_sensor ∘ T)
//
//  Gereksinim: sim-engine.js önceden yüklenmiş olmalı.
// ═══════════════════════════════════════════════════════════════════
window._es = 1;

const CHANNELS = {
    R:  { color: '#f87171', dashed: false, width: 2,   editable: true  },
    G:  { color: '#4ade80', dashed: false, width: 2,   editable: true  },
    B:  { color: '#60a5fa', dashed: false, width: 2,   editable: true  },
    L:  { color: '#e2e8f0', dashed: true,  width: 3.5, editable: true  }, // L_sensor
    T:  { color: '#fbbf24', dashed: false, width: 2.5, editable: true  }, // Gün Profili
    LT: { color: '#c084fc', dashed: true,  width: 2,   editable: false }, // türetilmiş
};

const EDITABLE_CHS = Object.keys(CHANNELS).filter(ch => CHANNELS[ch].editable);

// ── Düğüm yönetimi ──
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

// ── localStorage kalıcılık ──
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
        // Geriye uyumluluk: eski LT (v3) kaydı varsa yok say, T yoksa LT'yi T olarak kullan
        if (!data.T && data.LT) data.T = data.LT;
        const result = {};
        let valid = true;
        EDITABLE_CHS.forEach(ch => {
            if (!data[ch] || !Array.isArray(data[ch]) || data[ch].length < 2) {
                valid = false; return;
            }
            result[ch] = data[ch].map(n => mkNode(
                parseFloat(n.x) || 0,
                parseFloat(n.y) || 0,
                !!n.fixed
            ));
        });
        return valid ? result : null;
    } catch(e) { return null; }
}

let nodes = loadFromLocalStorage() || defaultNodes();

// ── calcMode toggle ──
let calcMode = localStorage.getItem('rgbl_calc_mode') || 'absolute';

// ── Türetilmiş LT önbelleği ──
let _ltDirty = true;
let _ltPoints = [];
function _invalidateLT() { _ltDirty = true; }

// ── D3 SVG kurulumu ──
const margin = { top: 20, right: 20, bottom: 40, left: 50 };

const wrap = document.getElementById('d3-wrap');
let W = (wrap ? wrap.clientWidth  : 480) - margin.left - margin.right;
let H = (wrap ? wrap.clientHeight : 340) - margin.top  - margin.bottom;
if (W < 100) W = 410;
if (H < 100) H = 280;

const svg = d3.select('#d3-wrap')
    .append('svg')
    .attr('width',  W + margin.left + margin.right)
    .attr('height', H + margin.top  + margin.bottom);

const g = svg.append('g')
    .attr('transform', `translate(${margin.left},${margin.top})`);

const xSc = d3.scaleLinear().domain([0, 100]).range([0, W]);
const ySc = d3.scaleLinear().domain([0, 100]).range([H, 0]);

// ── Grid ──
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

// ── Eksenler ──
const xAxisEl = g.append('g').attr('transform', `translate(0,${H})`).call(d3.axisBottom(xSc).ticks(5));
xAxisEl.selectAll('text').style('fill', '#94a3b8').style('font-size', '11px');
xAxisEl.selectAll('.domain,.tick line').style('stroke', '#475569');

const yAxisEl = g.append('g').call(d3.axisLeft(ySc).ticks(5));
yAxisEl.selectAll('text').style('fill', '#94a3b8').style('font-size', '11px');
yAxisEl.selectAll('.domain,.tick line').style('stroke', '#475569');

const xAxisLabel = g.append('text')
    .attr('x', W / 2).attr('y', H + 35)
    .attr('text-anchor', 'middle')
    .style('fill', '#64748b').style('font-size', '11px')
    .text('Ortam Işığı (%)');

g.append('text')
    .attr('transform', 'rotate(-90)').attr('x', -H / 2).attr('y', -38)
    .attr('text-anchor', 'middle').style('fill', '#64748b').style('font-size', '11px')
    .text('Çıkış (%)');

// ── Yeniden boyutlandırma ──
function resize() {
    if (!wrap) return;
    const nw = wrap.clientWidth  - margin.left - margin.right;
    const nh = wrap.clientHeight - margin.top  - margin.bottom;
    if (nw < 50 || nh < 50) return;
    W = nw; H = nh;
    svg.attr('width',  W + margin.left + margin.right)
       .attr('height', H + margin.top  + margin.bottom);
    xSc.range([0, W]);
    ySc.range([H, 0]);
    _drawGrid();
    xAxisEl.attr('transform', `translate(0,${H})`).call(d3.axisBottom(xSc).ticks(5));
    xAxisEl.selectAll('text').style('fill', '#94a3b8').style('font-size', '11px');
    xAxisEl.selectAll('.domain,.tick line').style('stroke', '#475569');
    yAxisEl.call(d3.axisLeft(ySc).ticks(5));
    yAxisEl.selectAll('text').style('fill', '#94a3b8').style('font-size', '11px');
    yAxisEl.selectAll('.domain,.tick line').style('stroke', '#475569');
    xAxisLabel.attr('x', W / 2).attr('y', H + 35);
    xhLine.attr('y2', H);
    xhLineCanon.attr('y2', H);
    _invalidateLT();
    renderAll();
}
if (wrap) new ResizeObserver(() => resize()).observe(wrap);

// ── Eğri yolları ──
const pathLayer  = g.append('g').attr('class', 'paths');
const nodeLayer  = g.append('g').attr('class', 'nodes');
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

// ── Çizgi üreticiler ──
const lineGen     = d3.line().x(d => xSc(d.x)).y(d => ySc(d.y)).curve(d3.curveCatmullRom.alpha(0.5));
const lineGenMono = d3.line().x(d => xSc(d.x)).y(d => ySc(d.y)).curve(d3.curveMonotoneX);

// ── Crosshair'lar ──
const xhLine = crossLayer.append('line')
    .attr('y1', 0).attr('y2', H)
    .style('stroke', 'rgba(255,255,255,0.5)').style('stroke-width', '1.5')
    .style('stroke-dasharray', '4,3').style('pointer-events', 'none');

// Kehribar: saat modunda T(t) — "bu saatte ortam eşdeğeri"
const xhLineCanon = crossLayer.append('line')
    .attr('y1', 0).attr('y2', H)
    .style('stroke', 'rgba(251,191,36,0.7)').style('stroke-width', '1.5')
    .style('stroke-dasharray', '3,4').style('pointer-events', 'none')
    .style('display', 'none');

// ── Aktif kanal / mod ──
let activeCh   = 'L';
let isTimeMode = false;

function getSimMode() {
    const el = document.querySelector('input[name="simMode"]:checked');
    return el ? el.value : 'sensor';
}

// ── Kanal görünürlük kuralları ──
function getChannelVisibility(ch) {
    if (ch === 'L')  return !isTimeMode;  // sensör modunda
    if (ch === 'T')  return isTimeMode;   // saat modunda (düzenlenebilir)
    if (ch === 'LT') return isTimeMode;   // saat modunda (türetilmiş, salt okunur)
    return true;                          // RGB her zaman
}

// ── sampleAtX: SVG yol üzerinde ikili arama ──
function sampleAtX(ch, xVal) {
    const path = curvePaths[ch];
    if (!path || !path.node()) return xVal;
    const pathEl   = path.node();
    const totalLen = pathEl.getTotalLength();
    if (totalLen === 0) return xVal;
    const targetX = xSc(Math.max(0, Math.min(100, xVal)));
    let lo = 0, hi = totalLen, mid, pt;
    for (let i = 0; i < 50; i++) {
        mid = (lo + hi) / 2;
        pt  = pathEl.getPointAtLength(mid);
        if (Math.abs(pt.x - targetX) < 0.3) break;
        if (pt.x < targetX) lo = mid; else hi = mid;
    }
    return Math.max(0, Math.min(100, ySc.invert(pt.y)));
}

// ── LT türetme ── LT(t) = L_sensor(T(t))
function _computeLTPoints() {
    const pts = [];
    for (let xi = 0; xi <= 100; xi += 2) {
        const tAmb = sampleAtX('T', xi);   // T(t): ortam eşdeğeri
        const lVal = sampleAtX('L', tAmb); // L_sensor at that ambient
        pts.push({ x: xi, y: lVal });
    }
    return pts;
}

function _renderLT() {
    if (!isTimeMode) {
        curvePaths['LT'].attr('opacity', 0);
        return;
    }
    if (_ltDirty) {
        _ltPoints = _computeLTPoints();
        _ltDirty  = false;
    }
    curvePaths['LT']
        .datum(_ltPoints)
        .attr('d', lineGen)
        .attr('opacity', 0.75)
        .style('pointer-events', 'none');
}

// ── Render ──
function renderAll() {
    xAxisLabel.text(isTimeMode ? 'Saat (0h → 24h)' : 'Ortam Işığı (%)');
    xhLineCanon.style('display', isTimeMode ? null : 'none');

    // Düzenlenebilir kanalları çiz
    EDITABLE_CHS.forEach(ch => {
        const show   = getChannelVisibility(ch);
        const gen    = ch === 'L' ? lineGenMono : lineGen;
        const sorted = [...nodes[ch]].sort((a, b) => a.x - b.x);
        curvePaths[ch]
            .datum(sorted).attr('d', gen)
            .attr('opacity', show ? 1 : 0)
            .style('pointer-events', show && ch !== 'LT' ? 'stroke' : 'none');
    });

    // Türetilmiş LT'yi çiz (L veya T değiştiğinde otomatik güncellenir)
    _renderLT();

    // Düğüm noktaları (sadece düzenlenebilir kanallar)
    EDITABLE_CHS.forEach(ch => {
        const show     = getChannelVisibility(ch);
        const isActive = ch === activeCh;
        const sorted   = [...nodes[ch]].sort((a, b) => a.x - b.x);

        const sel = nodeLayer.selectAll(`.node-${ch}`)
            .data(show ? sorted : [], d => d.id);

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
                _invalidateLT();
                saveToLocalStorage();
                renderAll();
                triggerUpdate();
            })
            .merge(sel)
            .attr('cx', d => xSc(d.x))
            .attr('cy', d => ySc(d.y))
            .attr('r',  isActive ? 7 : 5)
            .style('opacity', show ? 1 : 0);

        sel.exit().remove();
    });

    // Kanal butonları
    document.querySelectorAll('.ch-btn').forEach(btn => {
        const ch = btn.dataset.ch;
        btn.classList.toggle('active', ch === activeCh);
        if (ch === 'L') btn.style.display = !isTimeMode ? '' : 'none';
        if (ch === 'T') btn.style.display =  isTimeMode ? '' : 'none';
    });
}

// ── Monoton kısıt (L kanalı) ──
function clampLMonotone(node, newY) {
    const sorted = [...nodes.L].sort((a, b) => a.x - b.x);
    const idx    = sorted.findIndex(n => n.id === node.id);
    const minY   = idx > 0                  ? sorted[idx - 1].y : 0;
    const maxY   = idx < sorted.length - 1 ? sorted[idx + 1].y : 100;
    return Math.max(minY, Math.min(maxY, newY));
}

// ── Sürükleme davranışı ──
function makeDrag(ch) {
    return d3.drag()
        .on('start', function() { d3.select(this).style('cursor', 'grabbing'); })
        .on('drag', function(ev, d) {
            const [px, py] = d3.pointer(ev, g.node());
            const mx = xSc.invert(px);
            const my = ySc.invert(py);
            const cy = Math.max(0, Math.min(100, my));

            if (d.fixed) {
                d.y = (ch === 'L') ? clampLMonotone(d, cy) : cy;
            } else {
                d.x = Math.max(1, Math.min(99, mx));
                d.y = (ch === 'L') ? clampLMonotone(d, cy) : cy;
            }
            // L veya T değişince LT'yi yeniden hesapla
            if (ch === 'L' || ch === 'T') _invalidateLT();
            renderAll();
            triggerUpdate();
        })
        .on('end', function(ev, d) {
            d3.select(this).style('cursor', d.fixed ? 'ns-resize' : 'grab');
            saveToLocalStorage();
        });
}

// ── SVG tıklama → düğüm ekleme ──
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
    triggerUpdate();
});

// ── Fare hareketi → crosshair ──
let currentAmbient = 50;

svg.on('mousemove', function(ev) {
    const [px] = d3.pointer(ev, g.node());
    const xVal = Math.max(0, Math.min(100, xSc.invert(px)));
    currentAmbient = xVal;

    xhLine.attr('x1', xSc(xVal)).attr('x2', xSc(xVal));

    if (isTimeMode) {
        // Kehribar crosshair: T(t) — bu saatteki ortam eşdeğeri
        const effAmb = sampleAtX('T', xVal);
        xhLineCanon
            .attr('x1', xSc(effAmb)).attr('x2', xSc(effAmb))
            .style('display', null);
    }
    triggerUpdate();
});

svg.on('mouseleave', function() {
    xhLine.attr('x1', -10).attr('x2', -10);
    xhLineCanon.style('display', 'none');
});

// ── Simülasyon güncelleme ──
function triggerUpdate() {
    const isTime = isTimeMode;
    let cR, cG, cB, cL, effAmb;

    if (isTime) {
        // Saat modu: T(t) → ortam eşdeğeri; L_sensor(ortam) → parlaklık
        effAmb = sampleAtX('T', currentAmbient);   // T(t)
        cL     = sampleAtX('L', effAmb);            // L_sensor(T(t))
        cR     = sampleAtX('R', effAmb);
        cG     = sampleAtX('G', effAmb);
        cB     = sampleAtX('B', effAmb);
    } else {
        // Sensör modu: doğrudan
        effAmb = currentAmbient;
        cL     = sampleAtX('L', effAmb);
        cR     = sampleAtX('R', effAmb);
        cG     = sampleAtX('G', effAmb);
        cB     = sampleAtX('B', effAmb);
    }

    // ── Hesap modu ──
    // Mutlak: fR = R × (L/100)  — toplam güç = (R+G+B) × L/100
    // Oran  : fR = (R/sum) × L  — toplam güç = L (her zaman)
    let fR, fG, fB;
    if (calcMode === 'ratio') {
        const sum = cR + cG + cB;
        if (sum > 0.001) {
            fR = (cR / sum) * cL;
            fG = (cG / sum) * cL;
            fB = (cB / sum) * cL;
        } else {
            fR = fG = fB = cL / 3;
        }
    } else {
        fR = cR * (cL / 100);
        fG = cG * (cL / 100);
        fB = cB * (cL / 100);
    }

    if (typeof updateEnvironment === 'function') {
        updateEnvironment(currentAmbient, fR, fG, fB, cL);
    }

    // ── Değer çubuğu ──
    const lblXpos = document.getElementById('lbl-xpos');
    const chipEff = document.getElementById('chip-eff');
    const dispEff = document.getElementById('disp-eff');
    if (lblXpos) lblXpos.textContent = isTime ? 'Saat' : 'Ortam';
    if (chipEff) chipEff.style.display = isTime ? '' : 'none';
    if (dispEff && isTime) dispEff.textContent = Math.round(effAmb);
    const el = (id) => document.getElementById(id);
    if (el('disp-amb')) el('disp-amb').textContent = Math.round(currentAmbient);
    if (el('disp-r'))   el('disp-r').textContent   = Math.round(fR);
    if (el('disp-g'))   el('disp-g').textContent   = Math.round(fG);
    if (el('disp-b'))   el('disp-b').textContent   = Math.round(fB);
    if (el('disp-l'))   el('disp-l').textContent   = Math.round(cL);
}

// ── Kanal seçimi ──
document.querySelectorAll('.ch-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        const ch = btn.dataset.ch;
        if (!CHANNELS[ch] || !CHANNELS[ch].editable) return;
        if (!getChannelVisibility(ch)) return;
        activeCh = ch;
        renderAll();
    });
});

// ── Hesap modu toggle ──
function _updateCalcBtn() {
    const btn = document.getElementById('calc-mode-btn');
    if (!btn) return;
    if (calcMode === 'ratio') {
        btn.textContent = '⚡ Oran';
        btn.title = 'ORAN: RGB renk oranını belirler, toplam güç = L (fR+fG+fB = L her zaman)';
    } else {
        btn.textContent = '⚡ Mutlak';
        btn.title = 'MUTLAK: fR = R×(L/100) — her kanal bağımsız ölçeklenir';
    }
}
const calcBtn = document.getElementById('calc-mode-btn');
if (calcBtn) {
    _updateCalcBtn();
    calcBtn.addEventListener('click', () => {
        calcMode = calcMode === 'ratio' ? 'absolute' : 'ratio';
        localStorage.setItem('rgbl_calc_mode', calcMode);
        _updateCalcBtn();
        triggerUpdate();
    });
}

// ── JSON Dışa Aktarma ──
const exportBtn = document.getElementById('export-btn');
if (exportBtn) {
    exportBtn.addEventListener('click', () => {
        const data = {};
        EDITABLE_CHS.forEach(ch => {
            data[ch] = nodes[ch].map(n => ({ x: +n.x.toFixed(2), y: +n.y.toFixed(2), fixed: n.fixed }));
        });
        const json = JSON.stringify({ version: 4, calcMode, channels: data }, null, 2);
        const blob = new Blob([json], { type: 'application/json' });
        const a    = document.createElement('a');
        a.href     = URL.createObjectURL(blob);
        a.download = 'rgbl_calibration.json';
        a.click();
        URL.revokeObjectURL(a.href);
    });
}

// ── Sıfırlama ──
const resetBtn = document.getElementById('reset-btn');
if (resetBtn) {
    resetBtn.addEventListener('click', () => {
        if (!confirm('Tüm eğriler sıfırlansın mı?')) return;
        localStorage.removeItem(LS_KEY);
        nodes = defaultNodes();
        _invalidateLT();
        renderAll();
        triggerUpdate();
    });
}

// ── Sim modu değişimi (sensör ↔ zaman) ──
document.querySelectorAll('input[name="simMode"]').forEach(radio => {
    radio.addEventListener('change', () => {
        isTimeMode = radio.value === 'time';
        currentAmbient = 50;
        if ( isTimeMode && activeCh === 'L') activeCh = 'T';
        if (!isTimeMode && activeCh === 'T') activeCh = 'L';
        renderAll();
        triggerUpdate();
    });
});

// ── İlk render ──
(function init() {
    isTimeMode = getSimMode() === 'time';
    if ( isTimeMode && activeCh === 'L') activeCh = 'T';
    if (!isTimeMode && activeCh === 'T') activeCh = 'L';
    renderAll();
    triggerUpdate();
})();
