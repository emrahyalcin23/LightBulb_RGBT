// ═══════════════════════════════════════════════════════════════════
//  D3-EDITOR.JS — RGBL Eğri Editörü
//  Gereksinim: sim-engine.js önceden yüklenmiş olmalı.
// ═══════════════════════════════════════════════════════════════════
window._es = 1; // adım 1: script başladı

const CHANNELS = {
    R: { color: '#f87171', dashed: false, width: 2   },
    G: { color: '#4ade80', dashed: false, width: 2   },
    B: { color: '#60a5fa', dashed: false, width: 2   },
    L: { color: '#e2e8f0', dashed: true,  width: 3.5 },
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
    };
}

// ── localStorage kalıcılık ──
// LS_KEY'i nodes'tan ÖNCE tanımla (temporal dead zone sorunu önlenir)
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
        ['R', 'G', 'B', 'L'].forEach(ch => {
            if (data[ch] && Array.isArray(data[ch])) {
                result[ch] = data[ch].map(n => mkNode(n.x, n.y, !!n.fixed));
            }
        });
        if (['R', 'G', 'B', 'L'].every(ch => result[ch] && result[ch].length >= 2)) return result;
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

window._es = 2; // adım 2: fonksiyon tanımları OK
// ── State ──
let nodes          = loadFromLocalStorage() || defaultNodes();
let activeChannel  = 'L';
let currentAmbient = 50;

// ── Değer gösterge chip'leri ──
const dispAmb = document.getElementById('disp-amb');
const dispR   = document.getElementById('disp-r');
const dispG   = document.getElementById('disp-g');
const dispB   = document.getElementById('disp-b');
const dispL   = document.getElementById('disp-l');

window._es = 3; // adım 3: state & disp OK
// ── Kanal butonları ──
// Aktif kanal değişince renderAll() çağrılır; böylece eğri/düğüm vurgusu güncellenir.
document.querySelectorAll('.ch-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        activeChannel = btn.dataset.ch;
        document.querySelectorAll('.ch-btn').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        renderAll();          // ← aktif kanalı vurgulamak için şart
    });
});

// ── Mod değişimi (radio) ──
document.querySelectorAll('input[name="simMode"]').forEach(r => {
    r.addEventListener('change', () => {
        buildAxes();                    // X eksenini mod için yeniden çiz
        renderAll();
        triggerUpdate(currentAmbient);
    });
});

// ── JSON Dışa Aktar ──
document.getElementById('export-btn').addEventListener('click', () => exportJSON());

// ── Sıfırla ──
document.getElementById('reset-btn').addEventListener('click', () => {
    nodes = defaultNodes();
    saveToLocalStorage();
    renderAll();
    triggerUpdate(currentAmbient);
});

window._es = 4; // adım 4: event listener'lar OK
// ═══════════════════════════════════════════════════════════════════
//  D3 KURULUMU
// ═══════════════════════════════════════════════════════════════════

const margin = { top: 28, right: 20, bottom: 44, left: 50 };
const wrap   = document.getElementById('d3-wrap');
window._es = 5; // adım 5: wrap alındı
const d3svg  = d3.select('#d3-wrap').append('svg');
window._es = 6; // adım 6: SVG oluşturuldu
const g      = d3svg.append('g').attr('transform', `translate(${margin.left},${margin.top})`);

let W, H, xSc, ySc;

// ── KATMAN SIRASI (kritik — bgRect eğri/düğümlerin ALTINDA olmalı) ──
const gridLayer    = g.append('g').attr('class', 'grid-layer');

// overlayLayer (bgRect) önce ekleniyor → eğri ve düğümler üstte kalıyor
const overlayLayer = g.append('g').attr('class', 'overlay-layer');
const bgRect       = overlayLayer.append('rect').attr('fill', 'transparent').attr('cursor', 'crosshair');

const curveLayer     = g.append('g').attr('class', 'curve-layer');
const timeCurveLayer     = g.append('g').attr('class', 'time-curve-layer');
const projectedNodeLayer = g.append('g').attr('class', 'proj-node-layer');
const nodeLayer          = g.append('g').attr('class', 'node-layer');
const crossLayer = g.append('g').attr('class', 'crosshair').attr('pointer-events', 'none');

// ── Crosshair elemanları ──
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

    // Saatlik modda 0h/6h/12h/18h/24h; sensör modunda 0-100
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
        .text('Çıkış (0 – 100)');
}

// ═══════════════════════════════════════════════════════════════════
//  EĞRİ & DÜĞÜM RENDER
// ═══════════════════════════════════════════════════════════════════

const lineGen = d3.line()
    .x(d => xSc(d.x))
    .y(d => ySc(d.y))
    .curve(d3.curveCatmullRom.alpha(0));

const curvePaths     = {}; // ch → d3 selection (ambient-space, editing)
const timeCurvePaths = {}; // ch → d3 selection (time-domain display, Gaussian)

function renderAll() {
    // Ölçekler henüz hazır değilse çizme (resize() öncesi çağrı koruması)
    if (!xSc || !ySc) return;

    ['R', 'G', 'B', 'L'].forEach(ch => {
        const cfg    = CHANNELS[ch];
        const isActive = ch === activeChannel;
        const pts    = [...nodes[ch]].sort((a, b) => a.x - b.x);

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
                // Eğriye tıklama → o kanala düğüm ekle
                .on('click', function (event) {
                    if (event.shiftKey) return;
                    const [mx] = d3.pointer(event, g.node());
                    const dx   = Math.max(0.5, Math.min(99.5, xSc.invert(mx)));
                    const dy   = sampleAtX(ch, dx);
                    addNode(ch, dx, dy);
                    // Tıklanan eğrinin kanalını aktif yap
                    activeChannel = ch;
                    document.querySelectorAll('.ch-btn').forEach(b => {
                        b.classList.toggle('active', b.dataset.ch === ch);
                    });
                    renderAll();
                });
        }

        // Aktif kanal: tam görünür, pasif: soluk
        curvePaths[ch]
            .datum(pts)
            .attr('d', lineGen(pts))
            .attr('opacity', isActive ? 0.92 : 0.18)
            .attr('stroke-width', isActive ? cfg.width + (ch === 'L' ? 0.5 : 0.5) : cfg.width);

        // ── Düğümler (D3 enter-update-exit) ──
        const sel = nodeLayer.selectAll(`.nd-${ch}`)
            .data(nodes[ch], d => d.id);

        const entered = sel.enter().append('circle')
            .attr('class', `nd-${ch}`)
            .attr('r', 7)
            .attr('fill', cfg.color)
            .attr('stroke', '#080c18')
            .attr('stroke-width', 2)
            // Sağ tık → sil
            .on('contextmenu', (event, d) => {
                event.preventDefault();
                event.stopPropagation();
                deleteNode(ch, d);
            })
            // Shift+Tık → sil
            .on('click', (event, d) => {
                if (event.shiftKey) {
                    event.stopPropagation();
                    deleteNode(ch, d);
                }
            })
            .call(
                d3.drag()
                    .on('start', function (event, d) {
                        d._dragFixed = d.fixed;
                        d3.select(this).attr('cursor', 'grabbing').raise();
                        // Sürüklenen kanalı aktif yap
                        if (activeChannel !== ch) {
                            activeChannel = ch;
                            document.querySelectorAll('.ch-btn').forEach(b => {
                                b.classList.toggle('active', b.dataset.ch === ch);
                            });
                        }
                    })
                    // d3.pointer(event, g.node()) — drag event.x/event.y KULLANILMAZ:
                    // D3 drag event.x = subject.x(data-space) + delta_px → birim karışması.
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
                    })
            );

        // Hem yeni hem var olan düğümlerin konumu ve görünümü
        entered.merge(sel)
            .attr('cx', d => xSc(d.x))
            .attr('cy', d => ySc(d.y))
            // Aktif kanalda: normal düğümler; pasif kanalda: gizli
            .style('display', isActive ? null : 'none')
            .attr('cursor', d => d.fixed ? 'ns-resize' : 'grab')
            .attr('pointer-events', isActive ? null : 'none');

        sel.exit().remove();
    });

    // Saatlik modda: base eğrileri gizle → time eğrilerini göster
    const isTimeMode = (document.querySelector('input[name="simMode"]:checked').value === 'time');
    Object.keys(CHANNELS).forEach(ch => {
        if (curvePaths[ch]) {
            curvePaths[ch].attr('opacity', isTimeMode ? 0 : (ch === activeChannel ? 0.92 : 0.18));
        }
    });
    if (isTimeMode) nodeLayer.selectAll('circle').style('display', 'none');
    timeCurveLayer.style('display', isTimeMode ? null : 'none');
    if (isTimeMode) {
        renderTimeCurves();
        drawProjectedNodes();
    } else {
        projectedNodeLayer.selectAll('*').remove();
    }
}

// ── Saatlik modda türetilmiş Gaussian eğrilerini çizer ──
// Her xi (0-100) için timeToAmbient(xi) → sampleAtX → çıkış noktası
function renderTimeCurves() {
    Object.keys(CHANNELS).forEach(ch => {
        const cfg      = CHANNELS[ch];
        const isActive = ch === activeChannel;
        const pts = d3.range(0, 101).map(xi => ({
            x: xi,
            y: sampleAtX(ch, timeToAmbient(xi))
        }));

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
            .attr('d', lineGen(pts))
            .attr('stroke-width', isActive ? cfg.width + 0.5 : cfg.width)
            .attr('stroke-dasharray', cfg.dashed ? '9,5' : null)
            .attr('opacity', isActive ? 0.92 : 0.18);
    });
}

// ── Saatlik modda kontrol düğümlerini Gaussian eğrisi üzerine yansıtır ──
// Her düğüm (ax, ay) → timeToAmbient(t)=ax denklemini çöz → t=50±dt
function drawProjectedNodes() {
    projectedNodeLayer.selectAll('*').remove();
    const ch    = activeChannel;
    const cfg   = CHANNELS[ch];
    const sigma = 12.5;

    nodes[ch].forEach(nd => {
        const val = (nd.x - 3) / 97;              // timeToAmbient tersine çevir
        if (val <= 0 || val > 1) return;           // çözüm yok (gece minimum)

        const dt    = sigma * Math.sqrt(-2 * Math.log(Math.min(val, 1)));
        const times = dt < 0.5 ? [50] : [50 - dt, 50 + dt];

        times.forEach(t => {
            if (t < 0 || t > 100) return;
            const y = sampleAtX(ch, nd.x);         // ≈ nd.y (Catmull-Rom noktadan geçer)

            projectedNodeLayer.append('circle')
                .attr('cx', xSc(t))
                .attr('cy', ySc(y))
                .attr('r', 6)
                .attr('fill', 'none')
                .attr('stroke', cfg.color)
                .attr('stroke-width', 2)
                .attr('opacity', 0.7)
                .attr('pointer-events', 'none');
        });
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

// Gaussian: saat pozisyonu (x=0-100 → saat 0h-24h) → ortam parlaklığı (0-100)
// Tepe nokta: x=50 (12:00) → 100; gece (x=0 veya 100) → ≈3
function timeToAmbient(x) {
    const sigma = 12.5; // ≈ 3 saat yarı-genişlik (12.5/100*24 ≈ 3h)
    return Math.min(100, Math.max(0, 3 + 97 * Math.exp(-Math.pow(x - 50, 2) / (2 * sigma * sigma))));
}

// ── RGBL hesapla + ekranı güncelle ──
function triggerUpdate(x) {
    currentAmbient = Math.max(0, Math.min(100, x));
    const mode = document.querySelector('input[name="simMode"]:checked').value;
    // Saatlik modda: fare konumu = zaman → Gaussian ile gerçek ortam parlaklığı hesaplanır
    const ambForRGBL = (mode === 'time') ? timeToAmbient(currentAmbient) : currentAmbient;

    const cR = sampleAtX('R', ambForRGBL);
    const cG = sampleAtX('G', ambForRGBL);
    const cB = sampleAtX('B', ambForRGBL);
    const cL = sampleAtX('L', ambForRGBL);

    const fR = cR * (cL / 100);
    const fG = cG * (cL / 100);
    const fB = cB * (cL / 100);

    if (dispAmb) dispAmb.textContent = Math.round(ambForRGBL); // gerçek ortam parlaklığını göster
    if (dispR)   dispR.textContent   = Math.round(fR);
    if (dispG)   dispG.textContent   = Math.round(fG);
    if (dispB)   dispB.textContent   = Math.round(fB);
    if (dispL)   dispL.textContent   = Math.round(cL);

    updateEnvironment(currentAmbient, fR, fG, fB, cL); // currentAmbient: ham zaman/sensör pozisyonu
}

// ═══════════════════════════════════════════════════════════════════
//  FARE ETKILEŞIMI
// ═══════════════════════════════════════════════════════════════════

// Mousemove g'ye bağlandı (bgRect yerine).
// Böylece cursor eğri veya düğüm üzerindeyken de crosshair güncellenir.
g.on('mousemove', function (event) {
    if (!xSc || !ySc) return;
    const [mx] = d3.pointer(event, g.node());
    const x    = Math.max(0, Math.min(100, xSc.invert(mx)));
    const mode = document.querySelector('input[name="simMode"]:checked').value;
    // Saatlik modda noktalar Gaussian ortam pozisyonunda; sensör modunda fare pozisyonunda
    const ambX = (mode === 'time') ? timeToAmbient(x) : x;

    xhLine.style('display', null)
        .attr('x1', xSc(x)).attr('y1', 0)
        .attr('x2', xSc(x)).attr('y2', H);

    // Time modunda nokta fare (zaman) pozisyonunda; sensor modunda ambient pozisyonunda
    const dotX = (mode === 'time') ? x : ambX;
    Object.keys(CHANNELS).forEach(ch => {
        const y = sampleAtX(ch, ambX);
        xhDots[ch].style('display', null)
            .attr('cx', xSc(dotX))
            .attr('cy', ySc(y));
    });

    triggerUpdate(x);
}).on('mouseleave', function () {
    xhLine.style('display', 'none');
    Object.values(xhDots).forEach(d => d.style('display', 'none'));
});

// Boş alana tıklama → aktif kanala düğüm ekle
// (bgRect ALTINDA olduğu için sadece eğri/düğüm olmayan alanlarda tetiklenir)
bgRect.on('click', function (event) {
    if (event.shiftKey) return;
    const [mx, my] = d3.pointer(event, g.node());
    const x = Math.max(0.5, Math.min(99.5, xSc.invert(mx)));
    const y = Math.max(0,   Math.min(100,  ySc.invert(my)));
    addNode(activeChannel, x, y);
});

// ── ResizeObserver ──
new ResizeObserver(() => resize()).observe(wrap);

// ── Başlangıç ──
requestAnimationFrame(() => {
    resize();
    triggerUpdate(50);
});
