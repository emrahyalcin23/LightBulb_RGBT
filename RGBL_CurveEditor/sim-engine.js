// ═══════════════════════════════════════════════════════════════════
//  SIM-ENGINE.JS — Simülasyon Motoru
// ═══════════════════════════════════════════════════════════════════

// Inlined from luminosity.js — avoids ES module import which breaks file:// loading.
function getAdjustedAmbient(sensorValue) {
    const min = 0, max = 100, minH = 1, maxH = 25;
    const raw = Math.min(max, Math.max(min, sensorValue));
    const t   = (raw - min) / (max - min);
    const mapped = minH + t * (maxH - minH);
    return Math.min(100, Math.max(0, ((mapped - minH) / (maxH - minH)) * 100));
}

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
    screenLtValue: document.getElementById('screenLtValue'),
};

let passiveBrightness = 20;

const passiveSlider = document.getElementById('passiveLSlider');
const passiveValue = document.getElementById('passiveLValue');
// if (passiveSlider) {
//     passiveSlider.addEventListener('input', function(e) {
//         passiveBrightness = parseInt(e.target.value, 10);
//         if (passiveValue) passiveValue.textContent = passiveBrightness;
//         if (window._updateSimulation) window._updateSimulation();
//     });
// }
if (passiveSlider) {
    passiveSlider.addEventListener('input', function(e) {
        // Slider 0-100 arası, ama çarpan etkisi için üstel veya doğrusal ölçekleme yapabiliriz
        let rawValue = parseInt(e.target.value, 10);
        
        // İSTEĞE BAĞLI: Slider'ın alt aralıkta daha hassas, üst aralıkta daha agresif olması için
        // Örneğin: 0→0, 50→25, 100→100 gibi bir eğri
        // passiveBrightness = Math.pow(rawValue / 100, 1.5) * 100;  // üstel yumuşak
        passiveBrightness = rawValue;  // direkt (doğrusal)
        
        if (passiveValue) passiveValue.textContent = Math.round(passiveBrightness);
        if (window._updateSimulation) window._updateSimulation();
    });
}

// Yıldız oluşturma
(function() {
    for (let i = 0; i < 160; i++) {
        const s = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
        s.setAttribute('cx', 70 + Math.random() * 260);
        s.setAttribute('cy', 70 + Math.random() * 320);
        s.setAttribute('r', 1 + Math.random() * 2.2);
        s.style.fill = '#fff9e0';
        s.style.opacity = '0';
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

function smoothStep(t) { return t * t * (3 - 2 * t); }

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

window.updateEnvironment = function(ambientLight, finalR, finalG, finalB, finalL, ltValue) {
    // ★ Sensör ham değerini düzelt ★
    const adjustedAmbient = getAdjustedAmbient(ambientLight);
    
    const mode = document.querySelector('input[name="simMode"]:checked').value;
    const ratio = Math.max(0, Math.min(1, ambientLight / 100));

    let skyColor, roomBright, celestialY, celestialX, celestialColor, timeLabel;
    let isDayMode;

    if (mode === 'time') {
        const t = ratio;
        const hourVal = t * 24;
        const hh = Math.floor(hourVal).toString().padStart(2, '0');
        const mm = Math.floor((hourVal % 1) * 60).toString().padStart(2, '0');
        const prefix = `${hh}:${mm} — `;

        if (hourVal < 4) timeLabel = prefix + '🌌 Gece Yarısı';
        else if (hourVal < 6) timeLabel = prefix + '🌠 Sabaha Karşı';
        else if (hourVal < 9) timeLabel = prefix + '🌄 Sabah';
        else if (hourVal < 11.5) timeLabel = prefix + '🌤️ Kuşluk';
        else if (hourVal < 13.5) timeLabel = prefix + '🔆 Tam Öğlen';
        else if (hourVal < 16.5) timeLabel = prefix + '☀️ Öğleden Sonra';
        else if (hourVal < 19) timeLabel = prefix + '🌅 Akşamüstü';
        else if (hourVal < 21) timeLabel = prefix + '🌆 Gün Batımı';
        else timeLabel = prefix + '🌙 Gece';

        skyColor = getSkyColorByTime(t);
        roomBright = getRoomBrightnessByTime(t);
        celestialY = getCelestialY_time(t);
        celestialX = 200 - 110 * Math.cos(Math.PI * t);
        isDayMode = Math.sin(Math.PI * t) > 0.1;

        if (isDayMode) {
            const er = t < 0.5 ? 0 : smoothStep((t - 0.5) / 0.5);
            celestialColor = `rgb(255,${Math.floor(230 - 60 * er)},${Math.floor(150 - 50 * er)})`;
        } else {
            celestialColor = 'rgb(230,240,255)';
        }

        _starOpacity = Math.min(0.96, Math.pow(Math.max(0, 1 - Math.sin(Math.PI * t) * 1.4), 1.5) * 0.9);
        if (simEl.valX) simEl.valX.textContent = `${hh}:${mm}`;
    } else {
        if (ratio > 0.9) timeLabel = '🔆 Çok Aydınlık';
        else if (ratio > 0.7) timeLabel = '☀️ Aydınlık';
        else if (ratio > 0.5) timeLabel = '🌤️ Normal';
        else if (ratio > 0.3) timeLabel = '🌅 Loş';
        else if (ratio > 0.1) timeLabel = '🌆 Alacakaranlık';
        else timeLabel = '🌙 Karanlık';

        skyColor = getSkyColorBySensor(ratio);
        roomBright = getRoomBrightnessBySensor(ratio);
        celestialY = getCelestialY_sensor(ratio);
        celestialX = 200 - 110 * Math.cos(Math.PI * ratio);
        isDayMode = ratio > 0.4;

        if (isDayMode) {
            const warm = Math.min(1, (ratio - 0.4) / 0.6);
            celestialColor = `rgb(255,${Math.floor(170 + warm * 85)},${Math.floor(80 + warm * 120)})`;
        } else {
            celestialColor = '#ccd9ff';
        }

        _starOpacity = Math.min(0.96, Math.pow(Math.max(0, 1 - ratio * 1.3), 1.5) * 0.9);
        if (simEl.valX) simEl.valX.textContent = Math.round(ambientLight);
    }

    simEl.sky.style.fill = skyColor;
    simEl.roomOverlay.style.opacity = Math.max(0, 1 - roomBright);

    simEl.celestial.setAttribute('cx', celestialX);
    simEl.celestial.setAttribute('cy', celestialY);
    simEl.celestial.setAttribute('r', isDayMode ? 40 : 30);
    simEl.celestial.style.fill = celestialColor;

    simEl.sunHalo.setAttribute('cx', celestialX);
    simEl.sunHalo.setAttribute('cy', celestialY);
    simEl.sunHalo.setAttribute('opacity', isDayMode ? 0.6 : 0);

    simEl.moonHalo.setAttribute('cx', celestialX);
    simEl.moonHalo.setAttribute('cy', celestialY);
    simEl.moonHalo.setAttribute('opacity', isDayMode ? 0 : 0.6);

    // ★★★ EKRAN PARLAKLIĞI (v7.0) ★★★
    // 1. GİRİŞLER: 
    // finalR, finalG, finalB -> 0-100 (Renk oranları)
    // finalL -> 0-100 (Ana parlaklık şiddeti)
    // passiveBrightness -> 0-100 (Taban ışık)

    // 2. IŞIK ŞİDDETİ (L) EĞRİSİ:
    // Orta tonları (L=50) yukarı çekmek için "Power Curve" kullanıyoruz.
    // 0.45-0.55 arası bir değer, tepedeki 100'ü bozmadan ortayı canlandırır.
    const t = finalL / 100;
    const curve = Math.pow(t, 0.5); // t=0.5 iken sonuç 0.70 olur.

    // 3. EFEKTİF PARLAKLIK (0-1 aralığında)
    const effectiveL = (passiveBrightness / 100) + (curve * (1 - (passiveBrightness / 100)));

    // 1. Önce RGB'nin toplam gücünü ölç
    const totalRGB = finalR + finalG + finalB;

    // 2. Eğer toplam güç düşükse (renk değiştiyse), değerleri yukarı çek (Normalizasyon)
    // Bu sayede renk ne olursa olsun 'ışık enerjisi' korunur.
    const factor = totalRGB > 0 ? (300 / totalRGB) : 1; 

    // 3. Şimdi L ile çarp (Orta değerleri kurtarmak için yine hafif bir eğri kullan)
    const masterL = Math.sqrt(finalL / 100) * 2.55; 

    const sR = Math.min(255, Math.round(finalR * factor * masterL));
    const sG = Math.min(255, Math.round(finalG * factor * masterL));
    const sB = Math.min(255, Math.round(finalB * factor * masterL));

    const scr = `rgb(${sR},${sG},${sB})`;

    // UYGULAMA
    simEl.monitorScreen.style.fill = scr;
    simEl.screenGlow.style.fill = scr;
    simEl.screenGlow.style.opacity = effectiveL * 0.65;
    
    // ★★★ TANILAMA İÇİN GLOBAL DEĞİŞKENLER ★★★
    window._lastFinalL = finalL;
    window._lastSCR = scr;
    window._lastEffectiveL = effectiveL;

    // Ekran glow'u için blur sınırlarını gizle (görünmez yap)
    simEl.screenGlow.style.filter = 'blur(38px)';
    
    // LT değerini ekran içinde göster (Y ekseni değeri)
    if (simEl.screenLtValue && ltValue !== undefined) {
        // sadece sayı
        // simEl.screenLtValue.textContent = `${Math.round(ltValue)}%`;
        // LT ifadesi ve sayı
        simEl.screenLtValue.textContent = `LT:${Math.round(ltValue)}%`;
        const intensity = ltValue / 100;
        simEl.screenLtValue.setAttribute('fill', `rgba(255,255,255,${0.7 + intensity * 0.3})`);
    }
    

    if (simEl.status) simEl.status.textContent = timeLabel;
    if (simEl.valY) simEl.valY.textContent =
        `R:${Math.round(finalR)} G:${Math.round(finalG)} B:${Math.round(finalB)} L:${Math.round(finalL)}`;
};
