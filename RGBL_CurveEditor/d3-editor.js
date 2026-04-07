// ═══════════════════════════════════════════════════════════════════
//  D3-EDITOR.JS — RGBL Eğri Editörü  (v3)
//  Mimari:
//    L   = L_sensor  : ortam ışığı (0-100) → parlaklık (0-100) [monoton ↑]
//    LT  = L_time    : saat (0-100) → parlaklık (0-100) [serbest]
//    T(t)= türetilmiş: L_sensor⁻¹(LT(t)) — hiç gösterilmez
//    RGB : ortak "etkin ortam" uzayında yaşar (her 2 modda da düzenlenebilir)
//  Gereksinim: sim-engine.js önceden yüklenmiş olmalı.
// ═══════════════════════════════════════════════════════════════════
window._es = 1;

const CHANNELS = {
    R:  { color: '#f87171', dashed: false, width: 2   },
    G:  { color: '#4ade80', dashed: false, width: 2   },
    B:  { color: '#60a5fa', dashed: false, width: 2   },
    L:  { color: '#e2e8f0', dashed: true,  width: 3.5 }, // L_sensor (monoton)
    LT: { color: '#c084fc', dashed: true,  width: 3.5 }, // L_time   (saat modu)
};

// ── Düğüm yönetimi ──
let _nid = 0;
function mkNode(x, y, fixed = false) { return { id: ++_nid, x, y, fixed }; }

function defaultNodes() {
    return {
        R:  [mkNode(0, 20, true), mkNode(100, 88, true)],
        G:  [mkNode(0, 15, true), mkNode(100, 82, true)],
        B:  [mkNode(0, 28, true), mkNode(100, 68, true)],
        L:  [mkNode(0, 10, true), mkNode(50, 55), mkNode(100, 95, true)],
        LT: [mkNode(0, 3,  true), mkNode(50, 100), mkNode(100, 3, true)],
    };
}

// ── localStorage kalıcılık ──
const LS_KEY = 'rgbl_calibration';

function saveToLocalStorage() {
    const data = {};
    Object.keys(nodes).forEach(ch => {
        data[ch] = nodes[ch].map(n => ({ x: n.x, y: n.y, fixed: n.fixed }));
    });
    try { localStorage.setItem(LS_KEY, JSON.stringify(data)); } catch(e) {}
}

function loadFromLocalStorage() {
    try {
        const raw = localStorage.getItem(LS_KEY);
        if (!raw) return null;
        const data = JSON.parse(raw);
        const result = {};
        // Geriye uyumluluk: eski 'T' anahtarı → 'LT'
        if (data.T && !data.LT) data.LT = data.T;
        const channels = ['R', 'G', 'B', 'L', 'LT'];
        let valid = true;
        channels.forEach(ch => {
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

// ── L_sensor ters çevirme önbelleği ──
let _lInvCache = null, _lInvHash = '';

function _invalidateLCache() { _lInvHash = ''; _lInvCache = null; }

function _buildLInvCache() {
    if (!curvePaths['L']) return;
    const hash = nodes.L.map(n => n.x.toFixed(1) + ',' + n.y.toFixed(1)).join('|');
    if (hash === _lInvHash && _lInvCache) return;
    _lInvHash = hash;
    _lInvCache = [];
    for (let i = 0; i <= 200; i++) {
        const a = i / 2;  // 0..100 adım 0.5
        const v = sampleAtX('L', a);
        _lInvCache.push({ a, v });
    }
    _lInvCache.sort((p, q) => p.v - q.v);
}

function invertLSensor(targetL) {
    _buildLInvCache();
    if (!_lInvCache || _lInvCache.length === 0) return targetL;
    const first = _lInvCache[0], last = _lInvCache[_lInvCache.length - 1];
    if (targetL <= first.v) return first.a;
    if (targetL >= last.v)  return last.a;
    let lo = 0, hi = _lInvCache.length - 1;
    while (lo < hi - 1) {
        const mid = (lo + hi) >> 1;
        if (_lInvCache[mid].v < targetL) lo = mid; else hi = mid;
    }
    const { a: a0, v: v0 } = _lInvCache[lo];
    const { a: a1, v: v1 } = _lInvCache[hi];
    if (v1 <= v0) return a0;
    return a0 + (targetL - v0) / (v1 - v0) * (a1 - a0);
}

// T(t): türetilmiş gizli ara eğri = L_sensor⁻¹(L_time(t))
function computeEffectiveAmbient(t) {
    return invertLSensor(sampleAtX('LT', t));
}

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

// ── Yeniden boyutlandırma ──
function resize() {
    if (!wrap) return;
    const nw = wrap.clientWidth  - margin.left - margin.right;
    const nh = wrap.clientHeight - margin.top  - margin.bottom;
    if (nw < 50 || nh < 50) return;
    W = nw; H = nh;
    svg.attr('width', W + margin.left + margin.right)
       .attr('height', H + margin.top + margin.bottom);
    xSc.range([0, W]);
    ySc.range([H, 0]);
    // Grid güncelle
    grid.selectAll('line').remove();
    [0, 25, 50, 75, 100].forEach(v => {
        grid.append('line')
            .attr('x1', xSc(v)).attr('x2', xSc(v))
            .attr('y1', 0).attr('y2', H)
            .style('stroke', '#334155').style('stroke-width', '1')
            .style('stroke-dasharray', v === 0 || v === 100 ? 'none' : '4,4');
        grid.append('line')
            .attr('x1', 0).attr('x2', W)
            .attr('y1', ySc(v)).attr('y2', ySc(v))
            .style('stroke', '#334155').style('stroke-width', '1')
            .style('stroke-dasharray', v === 0 || v === 100 ? 'none' : '4,4');
    });
    // Eksenler güncelle
    xAxisEl.call(d3.axisBottom(xSc).ticks(5));
    xAxisEl.selectAll('text').style('fill', '#94a3b8').style('font-size', '11px');
    xAxisEl.selectAll('.domain,.tick line').style('stroke', '#475569');
    xAxisEl.attr('transform', `translate(0,${H})`);
    yAxisEl.call(d3.axisLeft(ySc).ticks(5));
    yAxisEl.selectAll('text').style('fill', '#94a3b8').style('font-size', '11px');
    yAxisEl.selectAll('.domain,.tick line').style('stroke', '#475569');
    xAxisLabel.attr('x', W / 2).attr('y', H + 35);
    xhLine.attr('y2', H);
    xhLineCanon.attr('y2', H);
    renderAll();
}

if (wrap) new ResizeObserver(() => resize()).observe(wrap);

// ── Grid ──
const grid = g.append('g').attr('class', 'grid');
[0, 25, 50, 75, 100].forEach(v => {
    grid.append('line')
        .attr('x1', xSc(v)).attr('x2', xSc(v))
        .attr('y1', 0).attr('y2', H)
        .style('stroke', '#334155').style('stroke-width', '1')
        .style('stroke-dasharray', v === 0 || v === 100 ? 'none' : '4,4');
    grid.append('line')
        .attr('x1', 0).attr('x2', W)
        .attr('y1', ySc(v)).attr('y2', ySc(v))
        .style('stroke', '#334155').style('stroke-width', '1')
        .style('stroke-dasharray', v === 0 || v === 100 ? 'none' : '4,4');
});

// ── Eksenler ──
const xAxisEl = g.append('g').attr('transform', `translate(0,${H})`).call(d3.axisBottom(xSc).ticks(5));
xAxisEl.selectAll('text').style('fill', '#94a3b8').style('font-size', '11px');
xAxisEl.selectAll('.domain,.tick line').style('stroke', '#475569');

const yAxisEl = g.append('g').call(d3.axisLeft(ySc).ticks(5));
yAxisEl.selectAll('text').style('fill', '#94a3b8').style('font-size', '11px');
yAxisEl.selectAll('.domain,.tick line').style('stroke', '#475569');

// ── Eksen etiketleri ──
const xAxisLabel = g.append('text')
    .attr('x', W / 2).attr('y', H + 35)
    .attr('text-anchor', 'middle')
    .style('fill', '#64748b').style('font-size', '11px')
    .text('Ortam Işığı (%)');

g.append('text')
    .attr('transform', 'rotate(-90)')
    .attr('x', -H / 2).attr('y', -38)
    .attr('text-anchor', 'middle')
    .style('fill', '#64748b').style('font-size', '11px')
    .text('Çıkış (%)');

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
        .attr('opacity', 0);
});

// ── Çizgi üreticiler ──
const lineGen     = d3.line()
    .x(d => xSc(d.x)).y(d => ySc(d.y))
    .curve(d3.curveCatmullRom.alpha(0.5));

const lineGenMono = d3.line()
    .x(d => xSc(d.x)).y(d => ySc(d.y))
    .curve(d3.curveMonotoneX);

// ── Crosshair'lar ──
// Birincil: beyaz kesikli (zaman modunda saat konumu, sensör modunda ortam)
const xhLine = crossLayer.append('line')
    .attr('y1', 0).attr('y2', H)
    .style('stroke', 'rgba(255,255,255,0.5)')
    .style('stroke-width', '1.5')
    .style('stroke-dasharray', '4,3')
    .style('pointer-events', 'none');

// İkincil: kehribar kesikli (zaman modunda T(t) = L_sensor⁻¹(LT(t)) konumu)
const xhLineCanon = crossLayer.append('line')
    .attr('y1', 0).attr('y2', H)
    .style('stroke', 'rgba(251,191,36,0.7)')
    .style('stroke-width', '1.5')
    .style('stroke-dasharray', '3,4')
    .style('pointer-events', 'none')
    .style('display', 'none');

// ── Aktif kanal ──
let activeCh = 'L';
let isTimeMode = false;

// ── Sim modu takibi ──
function getSimMode() {
    const el = document.querySelector('input[name="simMode"]:checked');
    return el ? el.value : 'sensor';
}

// ── Kanal görünürlük kuralları ──
function getChannelVisibility(ch) {
    if (ch === 'L')  return !isTimeMode;   // sadece sensör modunda
    if (ch === 'LT') return isTimeMode;    // sadece zaman modunda
    return true;                           // RGB her zaman
}

// ── sampleAtX: SVG yol üzerinde ikili arama ──
function sampleAtX(ch, xVal) {
    const path = curvePaths[ch];
    if (!path || !path.node()) return xVal;
    const pathEl  = path.node();
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

// ── Render ──
function renderAll() {
    const isTime = isTimeMode;

    // Eksen etiketi güncelle
    xAxisLabel.text(isTime ? 'Saat (0h → 24h)' : 'Ortam Işığı (%)');

    // İkincil crosshair'ı göster/gizle
    xhLineCanon.style('display', isTime ? null : 'none');

    Object.keys(CHANNELS).forEach(ch => {
        const show = getChannelVisibility(ch);
        const gen  = ch === 'L' ? lineGenMono : lineGen;
        const sorted = [...nodes[ch]].sort((a, b) => a.x - b.x);
        curvePaths[ch]
            .datum(sorted)
            .attr('d', gen)
            .attr('opacity', show ? 1 : 0)
            .style('pointer-events', show ? 'stroke' : 'none');
    });

    // Düğüm noktaları — her kanal için
    Object.keys(CHANNELS).forEach(ch => {
        const show = getChannelVisibility(ch);
        const isActive = ch === activeCh;
        const sorted = [...nodes[ch]].sort((a, b) => a.x - b.x);

        const sel = nodeLayer.selectAll(`.node-${ch}`)
            .data(show ? sorted : [], d => d.id);

        sel.enter()
            .append('circle')
            .attr('class', `node-dot node-${ch}`)
            .attr('r', 6)
            .style('fill', CHANNELS[ch].color)
            .style('stroke', '#1e293b')
            .style('stroke-width', '2')
            .style('cursor', d => d.fixed ? 'ns-resize' : 'grab')
            .call(makeDrag(ch))
            .on('contextmenu', (ev, d) => {
                ev.preventDefault();
                if (d.fixed || nodes[ch].length <= 2) return;
                nodes[ch] = nodes[ch].filter(n => n.id !== d.id);
                saveToLocalStorage();
                renderAll();
                triggerUpdate();
            })
            .merge(sel)
            .attr('cx', d => xSc(d.x))
            .attr('cy', d => ySc(d.y))
            .attr('r',  d => (isActive ? 7 : 5))
            .style('opacity', show ? 1 : 0);

        sel.exit().remove();
    });

    // Kanal buton aktif/pasif görünümü
    document.querySelectorAll('.ch-btn').forEach(btn => {
        const ch = btn.dataset.ch;
        btn.classList.toggle('active', ch === activeCh);
        // LT butonu: sadece zaman modunda
        if (ch === 'LT') {
            btn.style.display = isTime ? '' : 'none';
        }
        // L butonu: sadece sensör modunda
        if (ch === 'L') {
            btn.style.display = isTime ? 'none' : '';
        }
    });
}

// ── Monoton sürükleme kısıtı (L kanalı için) ──
function clampLMonotone(node, newY) {
    const sorted = [...nodes.L].sort((a, b) => a.x - b.x);
    const idx    = sorted.findIndex(n => n.id === node.id);
    const minY   = idx > 0 ? sorted[idx - 1].y : 0;
    const maxY   = idx < sorted.length - 1 ? sorted[idx + 1].y : 100;
    return Math.max(minY, Math.min(maxY, newY));
}

// ── Sürükleme davranışı ──
function makeDrag(ch) {
    return d3.drag()
        .on('start', function(ev, d) {
            d3.select(this).style('cursor', 'grabbing');
        })
        .on('drag', function(ev, d) {
            const mx = xSc.invert(ev.x);
            const my = ySc.invert(ev.y);
            const clampedY = Math.max(0, Math.min(100, my));

            if (d.fixed) {
                // Sabit düğümler: sadece Y hareketli
                if (ch === 'L') {
                    d.y = clampLMonotone(d, clampedY);
                    _invalidateLCache();
                } else {
                    d.y = clampedY;
                }
            } else {
                // Serbest düğümler: X ve Y hareketli (uç düğüm değil)
                d.x = Math.max(1, Math.min(99, mx));
                if (ch === 'L') {
                    d.y = clampLMonotone(d, clampedY);
                    _invalidateLCache();
                } else {
                    d.y = clampedY;
                }
            }
            renderAll();
            triggerUpdate();
        })
        .on('end', function(ev, d) {
            d3.select(this).style('cursor', d.fixed ? 'ns-resize' : 'grab');
            saveToLocalStorage();
        });
}

// ── SVG tıklama → yeni düğüm ekleme ──
svg.on('click', function(ev) {
    const [px, py] = d3.pointer(ev, g.node());
    const nx = xSc.invert(px);
    const ny = ySc.invert(py);
    if (nx < 1 || nx > 99 || ny < 0 || ny > 100) return;
    // Mevcut düğümlere çok yakınsa ekleme
    const tooClose = nodes[activeCh].some(n => Math.abs(n.x - nx) < 3);
    if (tooClose) return;
    nodes[activeCh].push(mkNode(nx, ny, false));
    saveToLocalStorage();
    renderAll();
    triggerUpdate();
});

// ── Fare hareketi → crosshair güncel ──
let currentAmbient = 50;

svg.on('mousemove', function(ev) {
    const [px] = d3.pointer(ev, g.node());
    const xVal = Math.max(0, Math.min(100, xSc.invert(px)));
    currentAmbient = xVal;

    // Birincil crosshair
    xhLine.attr('x1', xSc(xVal)).attr('x2', xSc(xVal));

    // İkincil crosshair (zaman modunda: T(t) konumu)
    if (isTimeMode) {
        const effAmb = computeEffectiveAmbient(xVal);
        xhLineCanon
            .attr('x1', xSc(effAmb))
            .attr('x2', xSc(effAmb))
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
        // Zaman modu: LT(t) → L; T(t) = L_sensor⁻¹(LT(t)) → RGB örnekle
        cL     = sampleAtX('LT', currentAmbient);          // parlaklık
        effAmb = invertLSensor(cL);                         // T(t): etkin ortam
        cR     = sampleAtX('R', effAmb);
        cG     = sampleAtX('G', effAmb);
        cB     = sampleAtX('B', effAmb);
    } else {
        // Sensör modu: ortam → L_sensor; RGB doğrudan
        effAmb = currentAmbient;
        cR     = sampleAtX('R', effAmb);
        cG     = sampleAtX('G', effAmb);
        cB     = sampleAtX('B', effAmb);
        cL     = sampleAtX('L', effAmb);
    }

    // ── Hesap modu ──
    // Mutlak: her kanal bağımsız × L/100 ölçeklenir  (fR+fG+fB = (R+G+B)·L/100)
    // Oran  : RGB renk oranı sabit, toplam güç = L   (fR+fG+fB = cL her zaman)
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
    const lblXpos  = document.getElementById('lbl-xpos');
    const chipEff  = document.getElementById('chip-eff');
    const dispEff  = document.getElementById('disp-eff');
    const da = document.getElementById('disp-amb');
    const dr = document.getElementById('disp-r');
    const dg = document.getElementById('disp-g');
    const db = document.getElementById('disp-b');
    const dl = document.getElementById('disp-l');

    if (lblXpos) lblXpos.textContent = isTime ? 'Saat' : 'Ortam';
    if (chipEff) chipEff.style.display = isTime ? '' : 'none';
    if (dispEff && isTime) dispEff.textContent = Math.round(effAmb);

    if (da) da.textContent = Math.round(currentAmbient);
    if (dr) dr.textContent = Math.round(fR);
    if (dg) dg.textContent = Math.round(fG);
    if (db) db.textContent = Math.round(fB);
    if (dl) dl.textContent = Math.round(cL);
}

// ── Kanal seçimi ──
document.querySelectorAll('.ch-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        const ch = btn.dataset.ch;
        if (!CHANNELS[ch]) return;
        // Görünür olmayan kanala geçişi engelle
        if (!getChannelVisibility(ch)) return;
        activeCh = ch;
        renderAll();
    });
});

// ── Hesap modu toggle ──
function _updateCalcBtn() {
    const calcBtn = document.getElementById('calc-mode-btn');
    if (!calcBtn) return;
    if (calcMode === 'ratio') {
        calcBtn.textContent = '⚡ Oran';
        calcBtn.title = 'ORAN modu: RGB eğrileri renk oranını belirler, toplam güç = L (fR+fG+fB = L her zaman)';
    } else {
        calcBtn.textContent = '⚡ Mutlak';
        calcBtn.title = 'MUTLAK mod: Her kanal bağımsız ölçeklenir, fR=R×(L/100) — toplam güç = (R+G+B)×L/100';
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
        Object.keys(nodes).forEach(ch => {
            data[ch] = nodes[ch].map(n => ({ x: +n.x.toFixed(2), y: +n.y.toFixed(2), fixed: n.fixed }));
        });
        const json = JSON.stringify({ version: 3, calcMode, channels: data }, null, 2);
        const blob = new Blob([json], { type: 'application/json' });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
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
        _invalidateLCache();
        renderAll();
        triggerUpdate();
    });
}

// ── Sim modu değişimi (sensör ↔ zaman) ──
document.querySelectorAll('input[name="simMode"]').forEach(radio => {
    radio.addEventListener('change', () => {
        isTimeMode = radio.value === 'time';
        // Moda geçişte x eksenini ortaya sıfırla — iki mod farklı koordinat uzayı kullanır
        currentAmbient = 50;
        // Aktif kanalı moda göre ayarla
        if (isTimeMode && activeCh === 'L')   activeCh = 'LT';
        if (!isTimeMode && activeCh === 'LT') activeCh = 'L';
        renderAll();
        triggerUpdate();
    });
});

// ── İlk render ──
(function init() {
    const mode = getSimMode();
    isTimeMode = mode === 'time';
    if (isTimeMode && activeCh === 'L')   activeCh = 'LT';
    if (!isTimeMode && activeCh === 'LT') activeCh = 'L';
    renderAll();
    triggerUpdate();
})();
