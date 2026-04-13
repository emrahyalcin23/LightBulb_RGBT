using System;

namespace LightBulb.Services;

/// <summary>
/// Tüm RGBL dönüşüm yaklaşımlarının merkezi kütüphanesi.
/// Hiçbir yaklaşım silinmez; isimle çağrılır. Aktif kod buradan hangisini
/// kullanacağını seçer.
///
/// Değer aralıkları / Value ranges:
///   Modül proc değerleri     : 0-100  (firmware yüzde, type-1/2 formatı)
///   Eğri çıktıları fR/G/B   : 0-100
///   ltVal (L eğrisi çıktısı): 0-100
///   Evaluate() çıktısı       : 0-100  → GammaService.SetGammaRgbl() 0-100 alır
///   SetGammaRgbl → /100      : 0-1    → DeviceContext.SetGamma() 0-1 alır
///   DeviceContext gamma ramp : ramp[i] = (ushort)(i * 255 * multiplier)  (0–65 025)
/// </summary>
public static class RgblPipeline
{
    // ─── AmbientPct — Modül proc değerlerinden RGBL eğrisi X girdi üretimi ──────
    // ProcR/G/B (0-100) → ambientPct (0-100): CIE-Y ağırlıklı ortalama.
    // Yeşil kanala (0.7152) daha fazla ağırlık verir; luminans algısıyla uyumlu.
    public static double AmbientPct(double procR, double procG, double procB) =>
        Math.Clamp(0.2126 * procR + 0.7152 * procG + 0.0722 * procB, 0.0, 100.0);

    // ─── Yaklaşım A — Absolute ────────────────────────────────────────────────────
    // finalR = fR * L / 100
    // Özellik: L=50, R=80 → final=40  (L tek başına parlaklığı kontrol etmez;
    //          R ve L birlikte çarpar → her ikisi de parlaklığı etkiler)
    public static (double R, double G, double B) Absolute(
        double fR, double fG, double fB, double L) =>
        (fR * L / 100.0, fG * L / 100.0, fB * L / 100.0);

    // ─── Yaklaşım B — Ratio ───────────────────────────────────────────────────────
    // finalR = (fR / sum) * L   (sum = fR + fG + fB)
    // Özellik: RGB toplamı sabit tutulur, L toplam ışık enerjisini ölçekler.
    //          L=50, sum=240 → max kanal ≈ 20.8%  (L ≠ parlaklık garantisi yok)
    public static (double R, double G, double B) Ratio(
        double fR, double fG, double fB, double L)
    {
        var sum = fR + fG + fB;
        return sum > 0.001
            ? (fR / sum * L, fG / sum * L, fB / sum * L)
            : (L / 3.0, L / 3.0, L / 3.0);
    }

    // ─── Yaklaşım C — MaxNormalize  ← AKTİF ─────────────────────────────────────
    // finalR = (fR / max(R,G,B)) * L
    // Garanti: max(R,G,B) = L  →  L=50 → en parlak kanal daima %50
    // R/G/B eğrileri renk oranlarını tanımlar (en parlak kanal = referans = 100%);
    // L eğrisi ana parlaklık diyalıdır.
    public static (double R, double G, double B) MaxNormalize(
        double fR, double fG, double fB, double L)
    {
        var mx = Math.Max(fR, Math.Max(fG, fB));
        return mx > 0.001
            ? (fR / mx * L, fG / mx * L, fB / mx * L)
            : (L / 3.0, L / 3.0, L / 3.0);
    }

    // ─── Gamma katsayısı ──────────────────────────────────────────────────────────
    // 0-100 → 0.0-1.0  (DeviceContext.SetGamma() argüman aralığı)
    public static double ToGamma(double value0to100) =>
        Math.Clamp(value0to100 / 100.0, 0.0, 1.0);
}
