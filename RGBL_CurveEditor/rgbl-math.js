// ═══════════════════════════════════════════════════════════════════════════════
//  RGBL-MATH.JS — Tüm RGBL dönüşüm yaklaşımlarının merkezi JS kütüphanesi.
//  Hiçbir yaklaşım silinmez; isimle çağrılır.
//  d3-editor.js ve sim-engine.js bu dosyadan import eder.
//
//  Değer aralıkları:
//    Modül proc değerleri   : 0-100  (firmware yüzde)
//    Eğri çıktıları fR/G/B : 0-100
//    ltVal / L              : 0-100
//    Evaluate çıktısı       : 0-100  → toRgb255() ile 0-255'e çevirilir
//    toRgb255               : 0-255  (CSS rgb() için)
//    C# SetGammaRgbl        : 0-100 alır → /100 → DeviceContext 0-1 multiplier
//    DeviceContext ramp[i]  : i * 255 * multiplier  (0–65 025)
// ═══════════════════════════════════════════════════════════════════════════════

const RgblMath = {

    // ─── AmbientPct ────────────────────────────────────────────────────────────
    // ProcR/G/B (0-100) → ambientPct (0-100): CIE-Y ağırlıklı ortalama.
    ambientPct(pR, pG, pB) {
        return Math.max(0, Math.min(100, 0.2126 * pR + 0.7152 * pG + 0.0722 * pB));
    },

    // ─── Yaklaşım A — Absolute ─────────────────────────────────────────────────
    // finalR = fR * L / 100
    // L=50, R=80 → final=40  (L tek başına parlaklığı kontrol etmez)
    absolute(fR, fG, fB, L) {
        return [fR * L / 100, fG * L / 100, fB * L / 100];
    },

    // ─── Yaklaşım A-sim — Eski sim-engine ekran render formülü ─────────────────
    // sqrt(L/100)*2.55 × (300/totalRGB) normalizasyonu.
    // C#'tan ~2× parlak görüntü üretiyordu; artık kullanılmıyor.
    // NOT: Bu fonksiyon 0-255 döndürür; diğer yaklaşımlar 0-100 döndürür.
    absoluteSimOld(fR, fG, fB, L) {
        const tot = fR + fG + fB;
        const factor = tot > 0 ? 300 / tot : 1;
        const masterL = Math.sqrt(L / 100) * 2.55;
        return [
            Math.min(255, Math.round(fR * factor * masterL)),
            Math.min(255, Math.round(fG * factor * masterL)),
            Math.min(255, Math.round(fB * factor * masterL)),
        ];
    },

    // ─── Yaklaşım B — Ratio ────────────────────────────────────────────────────
    // finalR = (fR / sum) * L   (sum = fR + fG + fB)
    // L=50, sum=240 → max kanal ≈ 20.8%  (L ≠ parlaklık garantisi yok)
    ratio(fR, fG, fB, L) {
        const s = fR + fG + fB;
        return s > 0.001
            ? [fR / s * L, fG / s * L, fB / s * L]
            : [L / 3, L / 3, L / 3];
    },

    // ─── Yaklaşım C — MaxNormalize  ← AKTİF ───────────────────────────────────
    // finalR = (fR / max(R,G,B)) * L
    // Garanti: max(R,G,B) = L  →  L=50 → en parlak kanal daima %50.
    // R/G/B renk oranlarını tanımlar; L ana parlaklık diyalıdır.
    maxNormalize(fR, fG, fB, L) {
        const mx = Math.max(fR, fG, fB);
        return mx > 0.001
            ? [fR / mx * L, fG / mx * L, fB / mx * L]
            : [L / 3, L / 3, L / 3];
    },

    // ─── Ekran rengi dönüşümü ──────────────────────────────────────────────────
    // 0-100 → 0-255 (CSS rgb() değeri).
    // C# SetGammaRgbl ile birebir: gammaMultiplier = v / 100,
    // CSS renk = gammaMultiplier * 255 = v / 100 * 255.
    toRgb255(v) {
        return Math.min(255, Math.round(v / 100 * 255));
    },
};
