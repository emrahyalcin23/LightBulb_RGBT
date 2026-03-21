// ═══════════════════════════════════════════════════════════════════
//  D3-EDITOR.JS — RGBL Eğri Editörü
//  Gereksinim: sim-engine.js önceden yüklenmiş olmalı.
// ═══════════════════════════════════════════════════════════════════

const CHANNELS = {
    R: { color: '#f87171', dashed: false, width: 2 },
    G: { color: '#4ade80', dashed: false, width: 2 },
    B: { color: '#60a5fa', dashed: false, width: 2 },
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

let nodes         = defaultNodes();
let activeChannel = 'L';
let currentAmbient = 50;

// ── Değer gösterge chip'leri ──
const dispAmb = document.getElementById('disp-amb');
const dispR   = document.getElementById('disp-r');
const dispG   = document.getElementById('disp-g');
const dispB   = document.getElementById('disp-b');
const dispL   = document.getElementById('disp-l');

// ── Kanal butonları ──
document.querySelectorAll('.ch-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        activeChannel = btn.dataset.ch;
        document.querySelectorAll('.ch-btn').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
    });
});

// ── Mod değişimi (radio) ──
document.querySelectorAll('input[name="simMode"]').forEach(r => {
    r.addEventListener('change', () => triggerUpdate(currentAmbient));
});

// ── Sıfırla ──
document.getElementById('reset-btn').addEventListener('click', () => {
    nodes = defaultNodes();
    renderAll();
    triggerUpdate(currentAmbient);
});

// ═══════════════════════════════════════════════════════════════════
//  D3 KURULUMU
// ═══════════════════════════════════════════════════════════════════

const margin = { top: 28, right: 20, bottom: 44, left: 50 };
const wrap   = document.getElementById('d3-wrap');
const d3svg  = d3.select('#d3-wrap').append('svg');
const g      = d3svg.append('g').attr('transform', `translate(${margin.left},${margin.top})`);

let W, H, xSc, ySc;

// ── KATMAN SIRASI (kritik — bgRect eğri/düğümlerin ALTINDA olmalı) ──
const gridLayer  = g.append('g').attr('class', 'grid-layer');

// overlayLayer (bgRect) önce ekleniyor → eğri ve düğümler üstte kalıyor
const overlayLayer = g.append('g').attr('class', 'overlay-layer');
const bgRect = overlayLayer.append('rect').attr('fill', 'transparent').attr('cursor', 'crosshair');

const curveLayer = g.append('g').attr('class', 'curve-layer');
const nodeLayer  = g.append('g').attr('class', 'node-layer');
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
    gridLayer.selectAll('*').remove();
    g.selectAll('.ax-x,.ax-y,.ax-lbl').remove();

    gridLayer.append('g')
        .call(d3.axisBottom(xSc).tickSize(-H).tickValues(d3.range(0, 101, 10)).tickFormat(''))
        .attr('transform', `translate(0,${H})`)
        .call(gg => { gg.select('.domain').remove(); gg.selectAll('line').attr('stroke', 'rgba(255,255,255,0.055)'); });

    gridLayer.append('g')
        .call(d3.axisLeft(ySc).tickSize(-W).tickValues(d3.range(0, 101, 10)).tickFormat(''))
        .call(gg => { gg.select('.domain').remove(); gg.selectAll('line').attr('stroke', 'rgba(255,255,255,0.055)'); });

    g.append('g').attr('class', 'ax-x')
        .attr('transform', `translate(0,${H})`)
        .call(d3.axisBottom(xSc).tickValues(d3.range(0, 101, 20)))
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
        .text('Ortam Işığı (0 – 100)');

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

const curvePaths = {}; // ch → d3 selection

function renderAll() {
    ['R', 'G', 'B', 'L'].forEach(ch => {
        const cfg = CHANNELS[ch];
        const pts = [...nodes[ch]].sort((a, b) => a.x - b.x);

        // ── Eğri ──
        if (!curvePaths[ch]) {
            curvePaths[ch] = curveLayer.append('path')
                .attr('class', `curve-${ch}`)
                .attr('fill', 'none')
                .attr('stroke', cfg.color)
                .attr('stroke-width', cfg.width)
                .attr('stroke-dasharray', cfg.dashed ? '9,5' : null)
                .attr('stroke-linecap', 'round')
                .attr('opacity', 0.85)
                .style('cursor', 'pointer')
                // Eğriye tıklama → o kanala düğüm ekle
                // NOT: bgRect artık ALTINDA olduğu için bu handler'a event ulaşır
                .on('click', function (event) {
                    if (event.shiftKey) return;
                    const [mx] = d3.pointer(event, g.node());
                    const dx   = Math.max(0.5, Math.min(99.5, xSc.invert(mx)));
                    const dy   = sampleAtX(ch, dx);
                    addNode(ch, dx, dy);
                    activeChannel = ch;
                    document.querySelectorAll('.ch-btn').forEach(b => {
                        b.classList.toggle('active', b.dataset.ch === ch);
                    });
                });
        }
        curvePaths[ch].datum(pts).attr('d', lineGen(pts));

        // ── Düğümler (D3 enter-update-exit) ──
        const sel = nodeLayer.selectAll(`.nd-${ch}`)
            .data(nodes[ch], d => d.id);

        const entered = sel.enter().append('circle')
            .attr('class', `nd-${ch}`)
            .attr('r', 7)
            .attr('fill', cfg.color)
            .attr('stroke', '#080c18')
            .attr('stroke-width', 2)
            .attr('cursor', d => d.fixed ? 'ns-resize' : 'grab')
            // Sağ tık → sil
            .on('contextmenu', (event, d) => {
                event.preventDefault();
                deleteNode(ch, d);
            })
            // Shift+Tık → sil
            .on('click', (event, d) => {
                if (event.shiftKey) {
                    event.stopPropagation(); // bgRect'e ulaşmasın
                    deleteNode(ch, d);
                }
            })
            .call(
                d3.drag()
                    .on('start', function (event, d) {
                        d._dragFixed = d.fixed;
                        d3.select(this).attr('cursor', 'grabbing').raise();
                    })
                    // FIX: d3.pointer(dragEvent) yerine event.x / event.y kullan
                    // (drag event'in koordinatları zaten g'nin koordinat uzayındadır)
                    .on('drag', function (event, d) {
                        if (!d._dragFixed) {
                            d.x = Math.max(0.5, Math.min(99.5, xSc.invert(event.x)));
                        }
                        d.y = Math.max(0, Math.min(100, ySc.invert(event.y)));
                        renderAll();
                        triggerUpdate(currentAmbient);
                    })
                    .on('end', function (event, d) {
                        d3.select(this).attr('cursor', d._dragFixed ? 'ns-resize' : 'grab');
                    })
            );

        // Hem yeni hem var olan düğümlerin konumunu güncelle
        entered.merge(sel)
            .attr('cx', d => xSc(d.x))
            .attr('cy', d => ySc(d.y));

        sel.exit().remove();
    });
}

// ═══════════════════════════════════════════════════════════════════
//  DÜĞÜM İŞLEMLERİ
// ═══════════════════════════════════════════════════════════════════

function addNode(ch, x, y) {
    if (nodes[ch].some(n => Math.abs(n.x - x) < 2.5)) return;
    nodes[ch].push(mkNode(x, y));
    renderAll();
    triggerUpdate(currentAmbient);
}

function deleteNode(ch, d) {
    if (d.fixed) return;
    nodes[ch] = nodes[ch].filter(n => n !== d);
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

// ── RGBL hesapla + ekranı güncelle ──
function triggerUpdate(x) {
    currentAmbient = Math.max(0, Math.min(100, x));

    const cR = sampleAtX('R', currentAmbient);
    const cG = sampleAtX('G', currentAmbient);
    const cB = sampleAtX('B', currentAmbient);
    const cL = sampleAtX('L', currentAmbient);

    const fR = cR * (cL / 100);
    const fG = cG * (cL / 100);
    const fB = cB * (cL / 100);

    if (dispAmb) dispAmb.textContent = Math.round(currentAmbient);
    if (dispR)   dispR.textContent   = Math.round(fR);
    if (dispG)   dispG.textContent   = Math.round(fG);
    if (dispB)   dispB.textContent   = Math.round(fB);
    if (dispL)   dispL.textContent   = Math.round(cL);

    updateEnvironment(currentAmbient, fR, fG, fB, cL);
}

// ═══════════════════════════════════════════════════════════════════
//  FARE ETKILEŞIMI
// ═══════════════════════════════════════════════════════════════════

// FIX: Mousemove g'ye bağlandı (bgRect yerine).
// Böylece cursor eğri veya düğüm üzerindeyken de crosshair güncellenir.
g.on('mousemove', function (event) {
    if (!xSc || !ySc) return;
    const [mx] = d3.pointer(event, g.node());
    const x    = Math.max(0, Math.min(100, xSc.invert(mx)));

    xhLine.style('display', null)
        .attr('x1', xSc(x)).attr('y1', 0)
        .attr('x2', xSc(x)).attr('y2', H);

    Object.keys(CHANNELS).forEach(ch => {
        const y = sampleAtX(ch, x);
        xhDots[ch].style('display', null)
            .attr('cx', xSc(x))
            .attr('cy', ySc(y));
    });

    triggerUpdate(x);
}).on('mouseleave', function () {
    xhLine.style('display', 'none');
    Object.values(xhDots).forEach(d => d.style('display', 'none'));
});

// Boş alana tıklama → aktif kanala düğüm ekle
// (bgRect artık ALTINDA olduğu için sadece eğri/düğüm olmayan alanlarda tetiklenir)
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
