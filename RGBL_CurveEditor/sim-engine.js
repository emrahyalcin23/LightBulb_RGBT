// ═══════════════════════════════════════════════════════════════════
//  SIM-ENGINE.JS — Simülasyon Motoru
//  updateEnvironment() dışarıdan (d3-editor.js) çağrılır.
// ═══════════════════════════════════════════════════════════════════

const simEl = {
    sky:           document.getElementById('simSky'),
    celestial:     document.getElementById('celestial'),
    sunHalo:       document.getElementById('sunHalo'),
    moonHalo:      document.getElementById('moonHalo'),
    starsGroup:    document.getElementById('starsGroup'),
    monitorScreen: document.getElementById('monitorScreen'),
    screenGlow:    document.getElementById('screenGlow'),
    roomOverlay:   document.getElementById('roomOverlay'),
    status:        document.getElementById('sim-status'),
    valX:          document.getElementById('valX'),
    valY:          document.getElementById('valY'),
};

// ── Yıldız oluşturma ──
(function () {
    for (let i = 0; i < 160; i++) {
        const s = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
        s.setAttribute('cx', 70 + Math.random() * 260);
        s.setAttribute('cy', 70 + Math.random() * 320);
        s.setAttribute('r',  1 + Math.random() * 2.2);
        s.style.fill       = '#fff9e0';
        s.style.opacity    = '0';
        s.style.transition = 'opacity 0.4s ease-out';
        simEl.starsGroup.appendChild(s);
    }
})();

let _starOpacity = 0;

setInterval(() => {
    const stars = simEl.starsGroup.querySelectorAll('circle');
    if (_starOpacity > 0.05) {
        stars.forEach(s => {
            s.style.opacity = Math.min(0.95, _starOpacity * (0.7 + Math.random() * 0.6));
        });
    } else {
        stars.forEach(s => { s.style.opacity = '0'; });
    }
}, 700);

// ── Yardımcı fonksiyonlar ──

function smoothStep(t) { return t * t * (3 - 2 * t); }

/**
 * 24 Saat gökyüzü rengi — t: 0 (gece yarısı) → 1 (24 saat sonra gece yarısı)
 */
function getSkyColorByTime(t) {
    const night1  = { r: 2,   g: 6,   b: 23  };
    const predawn = { r: 15,  g: 30,  b: 60  };
    const morning = { r: 80,  g: 140, b: 210 };
    const day     = { r: 110, g: 195, b: 232 };
    const dusk    = { r: 180, g: 95,  b: 43  };
    const night2  = { r: 8,   g: 12,  b: 38  };
    let r, g, b;
    if (t < 0.2) {
        const p = smoothStep(t / 0.2);
        r = night1.r + (predawn.r - night1.r) * p;
        g = night1.g + (predawn.g - night1.g) * p;
        b = night1.b + (predawn.b - night1.b) * p;
    } else if (t < 0.35) {
        const p = smoothStep((t - 0.2) / 0.15);
        r = predawn.r + (morning.r - predawn.r) * p;
        g = predawn.g + (morning.g - predawn.g) * p;
        b = predawn.b + (morning.b - predawn.b) * p;
    } else if (t < 0.65) {
        const p = (t - 0.35) / 0.3;
        const intensity = 1 - Math.abs(p - 0.5) * 0.3;
        r = Math.min(245, day.r * (0.9 + intensity * 0.2));
        g = Math.min(235, day.g * (0.9 + intensity * 0.2));
        b = Math.min(250, day.b * (1 + intensity * 0.1));
    } else if (t < 0.8) {
        const p = smoothStep((t - 0.65) / 0.15);
        r = day.r + (dusk.r - day.r) * p;
        g = day.g + (dusk.g - day.g) * p;
        b = day.b + (dusk.b - day.b) * p;
    } else {
        const p = smoothStep((t - 0.8) / 0.2);
        r = dusk.r + (night2.r - dusk.r) * p;
        g = dusk.g + (night2.g - dusk.g) * p;
        b = dusk.b + (night2.b - dusk.b) * p;
    }
    return `rgb(${Math.floor(r)},${Math.floor(g)},${Math.floor(b)})`;
}

/**
 * Sensör gökyüzü rengi — ratio: 0-1
 */
function getSkyColorBySensor(ratio) {
    const dark   = { r: 11,  g: 20,  b: 40  };
    const bright = { r: 192, g: 224, b: 255 };
    const t = Math.pow(Math.sin(ratio * Math.PI / 2), 1.2);
    return `rgb(${Math.floor(dark.r + (bright.r - dark.r) * t)},` +
               `${Math.floor(dark.g + (bright.g - dark.g) * t)},` +
               `${Math.floor(dark.b + (bright.b - dark.b) * t)})`;
}

function getCelestialY_time(t)   { return 360 - 265 * Math.sin(Math.PI * t); }
function getCelestialY_sensor(r) { return 360 - 265 * r; }
function getRoomBrightnessByTime(t)   { return 0.12 + Math.sin(Math.PI * t) * 0.78; }
function getRoomBrightnessBySensor(r) { return 0.1  + Math.pow(r, 1.2) * 0.85; }

/**
 * updateEnvironment — d3-editor.js tarafından çağrılır.
 * @param {number} ambientLight  0-100  (sensör değeri veya zaman ekseni)
 * @param {number} finalR        0-100  (R × L/100)
 * @param {number} finalG        0-100
 * @param {number} finalB        0-100
 * @param {number} finalL        0-100  (master luminance)
 */
function updateEnvironment(ambientLight, finalR, finalG, finalB, finalL) {
    const mode  = document.querySelector('input[name="simMode"]:checked').value;
    const ratio = Math.max(0, Math.min(1, ambientLight / 100));

    let skyColor, roomBright, celestialY, celestialX, celestialColor, timeLabel;
    let isDayMode;

    if (mode === 'time') {
        // 0-100 → 0h-24h
        const t       = ratio;
        const hourVal = t * 24;
        const hh      = Math.floor(hourVal).toString().padStart(2, '0');
        const mm      = Math.floor((hourVal % 1) * 60).toString().padStart(2, '0');
        const prefix  = `${hh}:${mm} — `;

        if      (hourVal < 4)    timeLabel = prefix + '🌌 Gece Yarısı';
        else if (hourVal < 6)    timeLabel = prefix + '🌠 Sabaha Karşı';
        else if (hourVal < 9)    timeLabel = prefix + '🌄 Sabah';
        else if (hourVal < 11.5) timeLabel = prefix + '🌤️ Kuşluk';
        else if (hourVal < 13.5) timeLabel = prefix + '🔆 Tam Öğlen';
        else if (hourVal < 16.5) timeLabel = prefix + '☀️ Öğleden Sonra';
        else if (hourVal < 19)   timeLabel = prefix + '🌅 Akşamüstü';
        else if (hourVal < 21)   timeLabel = prefix + '🌆 Gün Batımı';
        else                     timeLabel = prefix + '🌙 Gece';

        skyColor   = getSkyColorByTime(t);
        roomBright = getRoomBrightnessByTime(t);
        celestialY = getCelestialY_time(t);
        celestialX = 200 - 110 * Math.cos(Math.PI * t);
        isDayMode  = Math.sin(Math.PI * t) > 0.1;

        if (isDayMode) {
            const er = t < 0.5 ? 0 : smoothStep((t - 0.5) / 0.5);
            celestialColor = `rgb(255,${Math.floor(230 - 60 * er)},${Math.floor(150 - 50 * er)})`;
        } else {
            celestialColor = 'rgb(230,240,255)';
        }

        _starOpacity = Math.min(0.96, Math.pow(Math.max(0, 1 - Math.sin(Math.PI * t) * 1.4), 1.5) * 0.9);

        if (simEl.valX) simEl.valX.textContent = `${hh}:${mm}`;
    } else {
        // Sensör modu
        if      (ratio > 0.9) timeLabel = '🔆 Çok Aydınlık';
        else if (ratio > 0.7) timeLabel = '☀️ Aydınlık';
        else if (ratio > 0.5) timeLabel = '🌤️ Normal';
        else if (ratio > 0.3) timeLabel = '🌅 Loş';
        else if (ratio > 0.1) timeLabel = '🌆 Alacakaranlık';
        else                  timeLabel = '🌙 Karanlık';

        skyColor   = getSkyColorBySensor(ratio);
        roomBright = getRoomBrightnessBySensor(ratio);
        celestialY = getCelestialY_sensor(ratio);
        celestialX = 200 - 110 * Math.cos(Math.PI * ratio);
        isDayMode  = ratio > 0.4;

        if (isDayMode) {
            const warm = Math.min(1, (ratio - 0.4) / 0.6);
            celestialColor = `rgb(255,${Math.floor(170 + warm * 85)},${Math.floor(80 + warm * 120)})`;
        } else {
            celestialColor = '#ccd9ff';
        }

        _starOpacity = Math.min(0.96, Math.pow(Math.max(0, 1 - ratio * 1.3), 1.5) * 0.9);

        if (simEl.valX) simEl.valX.textContent = Math.round(ambientLight);
    }

    // ── Gökyüzü ──
    simEl.sky.style.fill = skyColor;

    // ── Oda karartma ──
    simEl.roomOverlay.style.opacity = Math.max(0, 1 - roomBright);

    // ── Gök cismi ──
    simEl.celestial.setAttribute('cx', celestialX);
    simEl.celestial.setAttribute('cy', celestialY);
    simEl.celestial.setAttribute('r',  isDayMode ? 40 : 30);
    simEl.celestial.style.fill = celestialColor;

    simEl.sunHalo.setAttribute('cx', celestialX);
    simEl.sunHalo.setAttribute('cy', celestialY);
    simEl.sunHalo.setAttribute('opacity', isDayMode ? 0.6 : 0);

    simEl.moonHalo.setAttribute('cx', celestialX);
    simEl.moonHalo.setAttribute('cy', celestialY);
    simEl.moonHalo.setAttribute('opacity', isDayMode ? 0 : 0.6);

    // ── Ekran rengi ──
    const sR = Math.round(finalR * 2.55);
    const sG = Math.round(finalG * 2.55);
    const sB = Math.round(finalB * 2.55);
    const scr = `rgb(${sR},${sG},${sB})`;
    simEl.monitorScreen.style.fill = scr;
    simEl.screenGlow.style.fill    = scr;
    simEl.screenGlow.style.opacity = (finalL / 100) * 0.65;

    // ── Durum etiketleri ──
    if (simEl.status) simEl.status.textContent = timeLabel;
    if (simEl.valY)   simEl.valY.textContent   =
        `R:${Math.round(finalR)} G:${Math.round(finalG)} B:${Math.round(finalB)} L:${Math.round(finalL)}`;
}
