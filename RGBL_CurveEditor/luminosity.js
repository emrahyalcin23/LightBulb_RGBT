// ============================================================
// luminosity.js
// Işık şiddeti ve normalize edilmiş sensör girişi
// ============================================================

// ----- AYARLAR (bağımsız değişkenler) -----
const SENSOR_CONFIG = {
    minSensör: 0,      // sensörden gelen minimum değer
    maxSensör: 100,    // sensörden gelen maksimum değer
    minHassas: 1,      // hassas bölge başlangıcı (düşük ışık)
    maxHassas: 25,     // hassas bölge bitişi (kritik üst sınır)
};

// 0–100 arası sensör verisini → 1–25 arasına normalize eder
function normalizeToSensitiveRange(sensorValue) {
    let raw = Math.min(SENSOR_CONFIG.maxSensör, Math.max(SENSOR_CONFIG.minSensör, sensorValue));
    
    // 0–100 aralığını 0–1 arasına çek
    let t = (raw - SENSOR_CONFIG.minSensör) / (SENSOR_CONFIG.maxSensör - SENSOR_CONFIG.minSensör);
    
    // 1–25 aralığına genişlet
    let mapped = SENSOR_CONFIG.minHassas + t * (SENSOR_CONFIG.maxHassas - SENSOR_CONFIG.minHassas);
    
    // sonucu 0–100 arası "normalized çıkış" olarak döndür (isteğe bağlı)
    // burada 0–100 arasında bir değer istiyorsak yeniden ölçekleriz
    let normalizedOutput = ((mapped - SENSOR_CONFIG.minHassas) / (SENSOR_CONFIG.maxHassas - SENSOR_CONFIG.minHassas)) * 100;
    
    return Math.min(100, Math.max(0, normalizedOutput));
}

// Aynı mantıkla "düzeltilmiş ortam değeri" döndürür
export function getAdjustedAmbient(sensorValue) {
    return normalizeToSensitiveRange(sensorValue);
}

// ----- RGB İŞLEME (eski sistem) -----
function absoluteRGB(R, G, B) {
    return { R, G, B };
}

function percentageRGB(R, G, B) {
    let toplam = R + G + B;
    if (toplam === 0) return { R: 0, G: 0, B: 0 };
    
    let rOran = R / toplam;
    let gOran = G / toplam;
    let bOran = B / toplam;
    
    let rVal = rOran * 100;
    let gVal = gOran * 100;
    let bVal = bOran * 100;
    
    let toplamYuvarlanmis = Math.round(rVal) + Math.round(gVal) + Math.round(bVal);
    let fark = 100 - toplamYuvarlanmis;
    
    if (fark !== 0) {
        if (rVal >= gVal && rVal >= bVal) rVal += fark;
        else if (gVal >= rVal && gVal >= bVal) gVal += fark;
        else bVal += fark;
    }
    
    return {
        R: Math.round(rVal),
        G: Math.round(gVal),
        B: Math.round(bVal)
    };
}

export function absoluteOrPercentageRGB(R, G, B, mode) {
    if (mode === 'absolute') return absoluteRGB(R, G, B);
    if (mode === 'ratio') return percentageRGB(R, G, B);
    throw new Error('Invalid mode');
}
