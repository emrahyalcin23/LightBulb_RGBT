// ═══════════════════════════════════════════════════════════════════
//  D3-EDITOR.JS — RGBL Eğri Editörü  (v2)
//  Değişiklikler:
//    1. curveCatmullRom.alpha(0) → alpha(0.5)  (geçiş yumuşatma)
//    2. T kanalı  (Gün Eğrisi) — tam düzenlenebilir zaman→ortam eğrisi
//    3. L → Gün Eğrisi kilidi  (lLockedToDay)
//    4. Hesap modu toggle  (Mutlak | Oran)
//  Gereksinim: sim-engine.js önceden yüklenmiş olmalı.
// ═══════════════════════════════════════════════════════════════════
window._es = 1;

const CHANNELS = {
    R: { color: '#f87171', dashed: false, width: 2   },
    G: { color: '#4ade80', dashed: false, width: 2   },
    B: { color: '#60a5fa', dashed: false, width: 2   },
    L: { color: '#e2e8f0', dashed: true,  width: 3.5 },
    T: { color: '#fbbf24', dashed: true,  width: 2.5 }, // Gün Eğrisi (zaman→ortam)
};

// ── Düğüm yönetimi ──
let _nid = 0;
function mkNode(x, y, fixed = false) { return { id: ++_nid, x, y, fixed }; }

function defaultNodes() {
    return {
        R: [mkNode(0, 20, true), mkNode(100, 88, true)],
        G: [mkNode(0, 15, true), mkNode(100, 82, true)],
        B: [mkNode(0, 28, true), mkNode(100, 68, true)],
        L: [mkNode(0, 10, true), mkNode(50, 55), mkNode(100, 95, true)],
        T: [mkNode(0, 3, true), mkNode(50, 100), mkNode(100, 3, true)], // Varsayılan Gauss-benzeri
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
        ['R', 'G', 'B', 'L', 'T'].forEach(ch => {
            if (data[ch] && Array.isArray(data[ch])) {
                result[ch] = data[ch].map(n => mkNode(n.x, n.y, !!n.fixed));
            }
        });
        // T kanalı yoksa varsayılan ekle (eski kayıtlar için geriye uyumluluk)
        if (!result['T'] || result['T'].length < 2) result['T'] = defaultNodes().T;
        if (['R', 'G', 'B', 'L', 'T'].every(ch => result[ch] && result[ch].length >= 2)) return result;
    } catch(e) {}
    return null;
}

function exportJSON() {
    const data = { channels: {} };
    Object.keys(nodes).forEach(ch => {
        data.channels[ch] = [...nodes[ch]].sort((a, b) => a.x - b.x)
            .map(n => ({ x: Math.round(n.x * 10) / 10, y: Math.round(n.y * 10) / 10, fixed: n.fixed }));
    });
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
    const url  = URL.createObjectURL(blob);
    const a    = document.createElement('a');
    a.href = url; a.download = 'rgbl_calibration.json'; a.click();
    URL.revokeObjectURL(url);
}

window._es = 2;
// ── State ──
let nodes          = loadFromLocalStorage() || defaultNodes();
let activeChannel  = 'L';
let currentAmbient = 50;

// ── Yeni state değişkenleri ──
let lLockedToDay = (localStorage.getItem('l_locked') === '1');
let lMin = parseInt(localStorage.getItem('l_min') || '5',  10);
let lMax = parseInt(localStorage.getItem('l_max') || '100', 10);
let calcMode = localStorage.getItem('rgbl_calc_mode') || 'absolute';

// ── DOM referansları ──
const dispAmb = document.getElementById('disp-amb');
const dispR   = document.getElementById('disp-r');
const dispG   = document.getElementById('disp-g');
const dispB   = document.getElementById('disp-b');
const dispL   = document.getElementById('disp-l');

window._es = 3;

// ════════════════════════════════════════════════════════
//  EVENT LISTENERS
// ════════════════════════════════════════════════════════

// ── Kanal butonları ──
document.querySelectorAll('.ch-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        activeChannel = btn.dataset.ch;
        document.querySelectorAll('.ch-btn').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        renderAll();
    });
});

// ── Mod değişimi (radio) ──
document.querySelectorAll('input[name="simMode"]').forEach(r => {
    r.addEventListener('change', () => {
        const isTime = (document.querySelector('input[name="simMode"]:checked').value === 'time');
        // Saat moduna geçince T kanalı aktif olsun; sensör modunda L'ye dön
        const newCh = isTime ? 'T' : 'L';
        activeChannel = newCh;
        document.querySelectorAll('.ch-btn').forEach(b => {
            b.classList.toggle('active', b.dataset.ch === newCh);
        });
        applyModeUI(isTime);
        buildAxes();
        renderAll();
        triggerUpdate(currentAmbient);
    });
});

function applyModeUI(isTime) {
    const tBtn       = document.getElementById('t-btn');
    const dayCtrl    = document.getElementById('day-controls');
    if (tBtn)    tBtn.style.display    = isTime ? '' : 'none';
    if (dayCtrl) dayCtrl.style.display = isTime ? 'flex' : 'none';
}

// ── Hesap modu toggle ──
const calcModeBtn = document.getElementById('calc-mode-btn');
if (calcModeBtn) {
    _updateCalcBtn();
    calcModeBtn.addEventListener('click', () => {
        calcMode = (calcMode === 'absolute') ? 'ratio' : 'absolute';
        localStorage.setItem('rgbl_calc_mode', calcMode);
        _updateCalcBtn();
        triggerUpdate(currentAmbient);
    });
}
function _updateCalcBtn() {
    if (!calcModeBtn) return;
    if (calcMode === 'ratio') {
        calcModeBtn.textContent = '⚖ Oran';
        calcModeBtn.title = 'Oran Modu: RGB normalize → L ile çarp. Tıkla: Mutlak Modu';
    } else {
        calcModeBtn.textContent = '⚡ Mutlak';
        calcModeBtn.title = 'Mutlak Modu: fR = cR × (L/100). Tıkla: Oran Modu';
    }
}

// ── L → Gün Eğrisi kilidi ──
const lLockBtn    = document.getElementById('l-lock-btn');
const lRangeDiv   = document.getElementById('l-range-controls');
const lMinSlider  = document.getElementById('l-min-slider');
const lMaxSlider  = document.getElementById('l-max-slider');
const lMinValEl   = document.getElementById('l-min-val');
const lMaxValEl   = document.getElementById('l-max-val');

if (lLockBtn) {
    _updateLLockBtn();
    lLockBtn.addEventListener('click', () => {
        lLockedToDay = !lLockedToDay;
        localStorage.setItem('l_locked', lLockedToDay ? '1' : '0');
        _updateLLockBtn();
        renderAll();
        triggerUpdate(currentAmbient);
    });
}
function _updateLLockBtn() {
    if (!lLockBtn) return;
    lLockBtn.textContent = lLockedToDay ? '🔒 L = Gün Eğrisi' : '🔓 L Serbest';
    lLockBtn.style.borderColor = lLockedToDay ? '#fbbf24' : '';
    if (lRangeDiv) lRangeDiv.style.display = lLockedToDay ? 'flex' : 'none';
}

if (lMinSlider) {
    lMinSlider.value = lMin;
    if (lMinValEl) lMinValEl.textContent = lMin;
    lMinSlider.addEventListener('input', () => {
        lMin = parseInt(lMinSlider.value, 10);
        if (lMinValEl) lMinValEl.textContent = lMin;
        localStorage.setItem('l_min', lMin);
        triggerUpdate(currentAmbient);
    });
}
if (lMaxSlider) {
    lMaxSlider.value = lMax;
    if (lMaxValEl) lMaxValEl.textContent = lMax;
    lMaxSlider.addEventListener('input', () => {
        lMax = parseInt(lMaxSlider.value, 10);
        if (lMaxValEl) lMaxValEl.textContent = lMax;
        localStorage.setItem('l_max', lMax);
        triggerUpdate(currentAmbient);
    });
}

// ── JSON Dışa Aktar ──
document.getElementById('export-btn').addEventListener('click', () => exportJSON());

// ── Sıfırla ──
document.getElementById('reset-btn').addEventListener('click', () => {
    nodes = defaultNodes();
    saveToLocalStorage();
    renderAll();
    triggerUpdate(currentAmbient);
});

window._es = 4;

// ═══════════════════════════════════════════════════════════════════
//  D3 KURULUMU
// ═══════════════════════════════════════════════════════════════════

const margin = { top: 28, right: 20, bottom: 44, left: 50 };
const wrap   = document.getElementById('d3-wrap');
window._es = 5;
const d3svg  = d3.select('#d3-wrap').append('svg');
window._es = 6;
const g      = d3svg.append('g').attr('transform', `translate(${margin.left},${margin.top})`);

let W, H, xSc, ySc;

// ── Katman sırası ──
const gridLayer          = g.append('g').attr('class', 'grid-layer');
const overlayLayer       = g.append('g').attr('class', 'overlay-layer');
const bgRect             = overlayLayer.append('rect').attr('fill', 'transparent').attr('cursor', 'crosshair');
const curveLayer         = g.append('g').attr('class', 'curve-layer');
const timeCurveLayer     = g.append('g').attr('class', 'time-curve-layer');
const projectedNodeLayer = g.append('g').attr('class', 'proj-node-layer');
const nodeLayer          = g.append('g').attr('class', 'node-layer');
const crossLayer         = g.append('g').attr('class', 'crosshair').attr('pointer-events', 'none');

// ── Crosshair ──
const xhLine = crossLayer.append('line')
    .attr('stroke', 'rgba(255,255,255,0.22)')
    .attr('stroke-width', 1)
    .attr('stroke-dasharray', '4,3')
    .style('display', 'none');

const xhDots = {};
Object.keys(CHANNELS).forEach(ch => {
    xhDots[ch] = crossLayer.append('circle')
        .attr('r', 4.5)
        .attr('fill', CHANNELS[ch].color)
        .attr('stroke', '#080c18').attr('stroke-width', 1.5)
        .style('display', 'none');
});

// ═══════════════════════════════════════════════════════════════════
//  BOYUTLANDIRMA & EKSENLER
// ═══════════════════════════════════════════════════════════════════

function resize() {
    const r = wrap.getBoundingClientRect();
    W = r.width  - margin.left - margin.right;
    H = r.height - margin.top  - margin.bottom;
    if (W <= 0 || H <= 0) return;
    d3svg.attr('width', r.width).attr('height', r.height);
    xSc = d3.scaleLinear().domain([0, 100]).range([0, W]);
    ySc = d3.scaleLinear().domain([0, 100]).range([H, 0]);
    bgRect.attr('width', W).attr('height', H);
    buildAxes();
    renderAll();
}

function buildAxes() {
    const mode   = document.querySelector('input[name="simMode"]:checked').value;
    const isTime = (mode === 'time');

    gridLayer.selectAll('*').remove();
    g.selectAll('.ax-x,.ax-y,.ax-lbl').remove();

    gridLayer.append('g')
        .call(d3.axisBottom(xSc).tickSize(-H).tickValues(d3.range(0, 101, 10)).tickFormat(''))
        .attr('transform', `translate(0,${H})`)
        .call(gg => { gg.select('.domain').remove(); gg.selectAll('line').attr('stroke', 'rgba(255,255,255,0.055)'); });

    gridLayer.append('g')
        .call(d3.axisLeft(ySc).tickSize(-W).tickValues(d3.range(0, 101, 10)).tickFormat(''))
        .call(gg => { gg.select('.domain').remove(); gg.selectAll('line').attr('stroke', 'rgba(255,255,255,0.055)'); });

    const xTicks  = isTime ? [0, 25, 50, 75, 100] : d3.range(0, 101, 20);
    const xFormat = isTime ? d => `${Math.round(d / 100 * 24)}h` : d3.format('d');

    g.append('g').attr('class', 'ax-x')
        .attr('transform', `translate(0,${H})`)
        .call(d3.axisBottom(xSc).tickValues(xTicks).tickFormat(xFormat))
        .call(gg => {
            gg.select('.domain').attr('stroke', 'rgba(255,255,255,0.18)');
            gg.selectAll('text').attr('fill', '#4a6080').attr('font-size', '10px');
            gg.selectAll('line').attr('stroke', 'rgba(255,255,255,0.12)');
        });

    g.append('g').attr('class', 'ax-y')
        .call(d3.axisLeft(ySc).tickValues(d3.range(0, 101, 20)))
        .call(gg => {
            gg.select('.domain').attr('stroke', 'rgba(255,255,255,0.18)');
            gg.selectAll('text').attr('fill', '#4a6080').attr('font-size', '10px');
            gg.selectAll('line').attr('stroke', 'rgba(255,255,255,0.12)');
        });

    g.append('text').attr('class', 'ax-lbl')
        .attr('x', W / 2).attr('y', H + 36)
        .attr('text-anchor', 'middle').attr('fill', '#3a5a88').attr('font-size', '10px')
        .text(isTime ? 'Saat (0h – 24h)' : 'Ortam Işığı (0 – 100)');

    g.append('text').attr('class', 'ax-lbl')
        .attr('transform', 'rotate(-90)')
        .attr('x', -H / 2).attr('y', -38)
        .attr('text-anchor', 'middle').attr('fill', '#3a5a88').attr('font-size', '10px')
        .text(isTime ? 'Ortam Işığı / Çıkış (0 – 100)' : 'Çıkış (0 – 100)');
}

// ═══════════════════════════════════════════════════════════════════
//  EĞRİ & DÜĞÜM RENDER
// ═══════════════════════════════════════════════════════════════════

// FIX 1: alpha(0.5) — centripetal Catmull-Rom, overshoot azaltılmış
const lineGen = d3.line()
    .x(d => xSc(d.x))
    .y(d => ySc(d.y))
    .curve(d3.curveCatmullRom.alpha(0.5));

// Yoğun örnekli eğriler için doğrusal çizici (timeCurve projeksiyonları)
const lineGenLinear = d3.line()
    .x(d => xSc(d.x))
    .y(d => ySc(d.y))
    .curve(d3.curveLinear);

const curvePaths     = {};
const timeCurvePaths = {};

function _makeDragHandler(ch) {
    return d3.drag()
        .on('start', function (event, d) {
            d._dragFixed = d.fixed;
            d3.select(this).attr('cursor', 'grabbing').raise();
            if (activeChannel !== ch) {
                activeChannel = ch;
                document.querySelectorAll('.ch-btn').forEach(b => {
                    b.classList.toggle('active', b.dataset.ch === ch);
                });
            }
        })
        .on('drag', function (event, d) {
            const [mx, my] = d3.pointer(event, g.node());
            if (!d._dragFixed) {
                d.x = Math.max(0.5, Math.min(99.5, xSc.invert(mx)));
            }
            d.y = Math.max(0, Math.min(100, ySc.invert(my)));
            renderAll();
            triggerUpdate(currentAmbient);
        })
        .on('end', function (event, d) {
            d3.select(this).attr('cursor', d._dragFixed ? 'ns-resize' : 'grab');
            saveToLocalStorage();
        });
}

function _renderChannel(ch, showCurve, showNodes) {
    const cfg      = CHANNELS[ch];
    const isActive = ch === activeChannel;
    const pts      = [...nodes[ch]].sort((a, b) => a.x - b.x);

    // ── Eğri ──
    if (!curvePaths[ch]) {
        curvePaths[ch] = curveLayer.append('path')
            .attr('class', `curve-${ch}`)
            .attr('fill', 'none')
            .attr('stroke', cfg.color)
            .attr('stroke-width', cfg.width)
            .attr('stroke-dasharray', cfg.dashed ? '9,5' : null)
            .attr('stroke-linecap', 'round')
            .style('cursor', 'pointer')
            .on('click', function (event) {
                if (event.shiftKey) return;
                if (!showCurve) return;
                const [mx] = d3.pointer(event, g.node());
                const dx = Math.max(0.5, Math.min(99.5, xSc.invert(mx)));
                const dy = sampleAtX(ch, dx);
                addNode(ch, dx, dy);
                activeChannel = ch;
                document.querySelectorAll('.ch-btn').forEach(b => {
                    b.classList.toggle('active', b.dataset.ch === ch);
                });
                renderAll();
            });
    }

    const curveOpacity = showCurve ? (isActive ? 0.92 : (ch === 'T' ? 0.35 : 0.18)) : 0;
    curvePaths[ch]
        .datum(pts)
        .attr('d', lineGen(pts))
        .attr('opacity', curveOpacity)
        .attr('stroke-width', isActive ? cfg.width + 0.5 : cfg.width)
        .attr('pointer-events', showCurve ? null : 'none');

    // ── Düğümler ──
    // L kilitlendiyse L düğümleri gizlenir
    const actuallyShowNodes = showNodes && !(ch === 'L' && lLockedToDay);

    const sel = nodeLayer.selectAll(`.nd-${ch}`)
        .data(nodes[ch], d => d.id);

    const entered = sel.enter().append('circle')
        .attr('class', `nd-${ch}`)
        .attr('r', 7)
        .attr('fill', cfg.color)
        .attr('stroke', '#080c18')
        .attr('stroke-width', 2)
        .on('contextmenu', (event, d) => {
            event.preventDefault();
            event.stopPropagation();
            deleteNode(ch, d);
        })
        .on('click', (event, d) => {
            if (event.shiftKey) { event.stopPropagation(); deleteNode(ch, d); }
        })
        .call(_makeDragHandler(ch));

    entered.merge(sel)
        .attr('cx', d => xSc(d.x))
        .attr('cy', d => ySc(d.y))
        .style('display', (actuallyShowNodes && isActive) ? null : 'none')
        .attr('cursor', d => d.fixed ? 'ns-resize' : 'grab')
        .attr('pointer-events', (actuallyShowNodes && isActive) ? null : 'none');

    sel.exit().remove();
}

function renderAll() {
    if (!xSc || !ySc) return;

    const isTimeMode = (document.querySelector('input[name="simMode"]:checked').value === 'time');

    // R, G, B, L: ortam uzayında; sadece sensör modunda düzenlenebilir
    ['R', 'G', 'B', 'L'].forEach(ch => {
        // showCurve: sensör modunda görünür (saat modunda gizli — projeksiyon olarak gösterilir)
        // showNodes: sensör modunda aktif kanal düğümleri
        _renderChannel(ch, !isTimeMode, !isTimeMode);
    });

    // T (Gün Eğrisi): zaman uzayında; sadece saat modunda düzenlenebilir
    _renderChannel('T', isTimeMode, isTimeMode);

    // Saat modunda: R/G/B/L → T üzerinden projekte edilmiş zaman eğrileri
    timeCurveLayer.style('display', isTimeMode ? null : 'none');
    projectedNodeLayer.selectAll('*').remove();
    if (isTimeMode) {
        renderTimeCurves();
    }
}

// ── Saat modunda R/G/B/L'yi T eğrisi üzerinden projekte eder ──
// Her xi (zaman 0-100) için: T(xi) → ambient → R/G/B/L örnekleme
function renderTimeCurves() {
    ['R', 'G', 'B', 'L'].forEach(ch => {
        const cfg      = CHANNELS[ch];
        const isActive = ch === activeChannel;

        const pts = d3.range(0, 101).map(xi => {
            const tVal = sampleAtX('T', xi);   // T kanalı: zaman → ortam
            let y;
            if (ch === 'L' && lLockedToDay) {
                // L kilitlendiyse: T değerini [lMin, lMax] aralığına ölçekle
                y = lMin + (lMax - lMin) * (tVal / 100);
            } else {
                y = sampleAtX(ch, tVal);        // Normal: ortam uzayında örnekle
            }
            return { x: xi, y };
        });

        if (!timeCurvePaths[ch]) {
            timeCurvePaths[ch] = timeCurveLayer.append('path')
                .attr('class', `time-curve-${ch}`)
                .attr('fill', 'none')
                .attr('stroke', cfg.color)
                .attr('stroke-linecap', 'round')
                .attr('pointer-events', 'none');
        }

        timeCurvePaths[ch]
            .datum(pts)
            .attr('d', lineGenLinear(pts))  // Yoğun nokta → doğrusal yeterli
            .attr('stroke-width', isActive ? cfg.width + 0.5 : cfg.width)
            .attr('stroke-dasharray', cfg.dashed ? '9,5' : null)
            .attr('opacity', isActive ? 0.92 : 0.18);
    });
}

// ═══════════════════════════════════════════════════════════════════
//  DÜĞÜM İŞLEMLERİ
// ═══════════════════════════════════════════════════════════════════

function addNode(ch, x, y) {
    if (nodes[ch].some(n => Math.abs(n.x - x) < 2.5)) return;
    nodes[ch].push(mkNode(x, y));
    saveToLocalStorage();
    renderAll();
    triggerUpdate(currentAmbient);
}

function deleteNode(ch, d) {
    if (d.fixed) return;
    nodes[ch] = nodes[ch].filter(n => n !== d);
    saveToLocalStorage();
    renderAll();
    triggerUpdate(currentAmbient);
}

// ── Eğri üzerinde Y değeri örnekleme (SVG path binary search) ──
function sampleAtX(ch, x) {
    const pathEl = curvePaths[ch] ? curvePaths[ch].node() : null;
    if (!pathEl) return 50;
    const total = pathEl.getTotalLength();
    if (total === 0) return 50;

    const targetX = xSc(x);
    let lo = 0, hi = total;
    for (let i = 0; i < 52; i++) {
        const mid = (lo + hi) / 2;
        if (pathEl.getPointAtLength(mid).x < targetX) lo = mid;
        else hi = mid;
    }
    const pt = pathEl.getPointAtLength((lo + hi) / 2);
    return Math.max(0, Math.min(100, ySc.invert(pt.y)));
}

// ── Zaman modunda anlık kanal değerini hesapla (crosshair için) ──
function timeCurveYAt(ch, xi) {
    const tVal = sampleAtX('T', xi);
    if (ch === 'L' && lLockedToDay) return lMin + (lMax - lMin) * (tVal / 100);
    return sampleAtX(ch, tVal);
}

// ── RGBL hesapla + ekranı güncelle ──
function triggerUpdate(x) {
    currentAmbient = Math.max(0, Math.min(100, x));
    const mode       = document.querySelector('input[name="simMode"]:checked').value;
    const isTimeMode = (mode === 'time');

    // FIX 2: Saat modunda T kanalı → ortam; sensör modunda doğrudan
    const ambForRGBL = isTimeMode ? sampleAtX('T', currentAmbient) : currentAmbient;

    const cR = sampleAtX('R', ambForRGBL);
    const cG = sampleAtX('G', ambForRGBL);
    const cB = sampleAtX('B', ambForRGBL);
    // FIX 3: L kilitlendiyse T × ölçek; değilse L eğrisinden
    const cL = (isTimeMode && lLockedToDay)
        ? (lMin + (lMax - lMin) * (ambForRGBL / 100))
        : sampleAtX('L', ambForRGBL);

    let fR, fG, fB;
    // FIX 4: Hesap modu
    if (calcMode === 'ratio') {
        const sum = cR + cG + cB;
        if (sum > 0.001) {
            fR = (cR / sum) * cL;
            fG = (cG / sum) * cL;
            fB = (cB / sum) * cL;
        } else {
            fR = fG = fB = cL / 3;  // Nötr beyaz (tüm kanallar sıfır)
        }
    } else {
        fR = cR * (cL / 100);
        fG = cG * (cL / 100);
        fB = cB * (cL / 100);
    }

    if (dispAmb) dispAmb.textContent = Math.round(ambForRGBL);
    if (dispR)   dispR.textContent   = Math.round(fR);
    if (dispG)   dispG.textContent   = Math.round(fG);
    if (dispB)   dispB.textContent   = Math.round(fB);
    if (dispL)   dispL.textContent   = Math.round(cL);

    updateEnvironment(currentAmbient, fR, fG, fB, cL);
}

// ═══════════════════════════════════════════════════════════════════
//  FARE ETKİLEŞİMİ
// ═══════════════════════════════════════════════════════════════════

g.on('mousemove', function (event) {
    if (!xSc || !ySc) return;
    const [mx] = d3.pointer(event, g.node());
    const x    = Math.max(0, Math.min(100, xSc.invert(mx)));
    const mode = document.querySelector('input[name="simMode"]:checked').value;
    const isTimeMode = (mode === 'time');

    xhLine.style('display', null)
        .attr('x1', xSc(x)).attr('y1', 0)
        .attr('x2', xSc(x)).attr('y2', H);

    Object.keys(CHANNELS).forEach(ch => {
        if (ch === 'T') {
            // T: sadece saat modunda, zaman ekseni üzerinde
            if (isTimeMode) {
                const y = sampleAtX('T', x);
                xhDots['T'].style('display', null)
                    .attr('cx', xSc(x))
                    .attr('cy', ySc(y));
            } else {
                xhDots['T'].style('display', 'none');
            }
        } else {
            const y = isTimeMode ? timeCurveYAt(ch, x) : sampleAtX(ch, x);
            xhDots[ch].style('display', null)
                .attr('cx', xSc(x))
                .attr('cy', ySc(y));
        }
    });

    triggerUpdate(x);
}).on('mouseleave', function () {
    xhLine.style('display', 'none');
    Object.values(xhDots).forEach(d => d.style('display', 'none'));
});

// Boş alana tıklama → aktif kanala düğüm ekle
bgRect.on('click', function (event) {
    if (event.shiftKey) return;
    const [mx, my] = d3.pointer(event, g.node());
    const mode = document.querySelector('input[name="simMode"]:checked').value;
    const isTimeMode = (mode === 'time');
    // Saat modunda T kanalına ekle; sensör modunda aktif kanala
    const targetCh = isTimeMode ? 'T' : activeChannel;
    const x = Math.max(0.5, Math.min(99.5, xSc.invert(mx)));
    const y = Math.max(0,   Math.min(100,  ySc.invert(my)));
    addNode(targetCh, x, y);
});

// ── ResizeObserver ──
new ResizeObserver(() => resize()).observe(wrap);

// ── Başlangıç ──
requestAnimationFrame(() => {
    const isTime = (document.querySelector('input[name="simMode"]:checked').value === 'time');
    applyModeUI(isTime);
    _updateLLockBtn();
    resize();
    triggerUpdate(50);
});
