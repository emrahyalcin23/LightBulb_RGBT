# RGBL Eğri Editörü — Görev Planı

## Klasör Yapısı
```
RGBL_CurveEditor/
  ├── RGBL_curve_d2_oda_7.html   ← Kullanıcının orijinal çalışan kodu (referans/kaynak)
  └── RGBL_curve_editor.html     ← Nihai ürün (güncellenmesi gereken dosya)
```

---

## Temel Kural
> `RGBL_curve_editor.html`, `RGBL_curve_d2_oda_7.html`'deki **tüm orijinal yapıyı** koruyacak.
> Sadece aşağıdaki 3 değişiklik yapılacak, **başka hiçbir şey değiştirilmeyecek.**

---

## Yapılması Gereken 3 Değişiklik

### ✅ Tamamlanan
- Dosyalar `RGBL_CurveEditor/` klasörüne taşındı
- `RGBL_curve_d2_oda_7.svg` → `RGBL_curve_d2_oda_7.html` olarak yeniden adlandırıldı

### ⏳ DEĞİŞİKLİK 1 — Düzen: İki Panel
Orijinal tek panel layout → iki sütunlu layout:
- **Sol panel (yeni):** D3.js RGBL Eğri Editörü
- **Sağ panel (orijinal):** Mevcut simülasyon + dashboard aynen korunacak

### ⏳ DEĞİŞİKLİK 2 — `updateEnvironment()` imzası güncelleme
**Orijinal:** `updateEnvironment(xRaw, yRaw)` — 2 parametre, 0-255 aralığında

**Yeni:** `updateEnvironment(ambientLight, finalR, finalG, finalB, finalL)` — 5 parametre, 0-100 aralığında

Ne değişiyor:
- `xRaw` → `ambientLight` (0-100 skala, 0-255 dönüşümü kaldırılacak)
- `yRaw` ile hesaplanan sabit oran RGBL (R=y*1.0, G=y*0.92, B=y*1.05) → D3'ten gelen `finalR/G/B/L` ile değiştirilecek
- Ekran rengi: `rgb(finalR*2.55, finalG*2.55, finalB*2.55)` şeklinde hesaplanacak
- Sky/celestial/room/yıldız mantığı: **aynen korunacak**, sadece 0-255→0-100 ölçek farkı uyarlanacak
- Dashboard, toggle, info panel: **tamamen korunacak**
- `mousemove` listener: **kaldırılacak** (kontrolü D3'e devrettik)

### ⏳ DEĞİŞİKLİK 3 — D3.js Sol Panel Ekleme
Aşağıdaki teknik gereksinimlerle sol panel eklenecek:
- X ekseni: Ortam Işığı (0-100), Y ekseni: Çıkış (0-100)
- 4 kanal: R (kırmızı), G (yeşil), B (mavi), L (beyaz, kalın, dashed)
- Eğri tipi: `d3.curveCatmullRom`
- Boş yere / eğriye tıklama → düğüm ekleme
- Düğüm sürükleme (x+y serbestçe, 0-100 sınırlı)
- Sağ tık / Shift+Tık → düğüm silme
- x=0 ve x=100 düğümleri korumalı (silinemez, x ekseni kilitli)
- Fare eğri üzerinde gezince hover → `updateEnvironment()` anlık tetiklenecek
- `Final_R = Curve_R * (Curve_L / 100)` matematiği uygulanacak

---

## Korunacak Orijinal Yapı (DEĞİŞTİRİLMEYECEK)
- Tüm CSS class'ları ve CSS variables (`--sky-color`, `--room-brightness`, `--screen-color`, vs.)
- SVG elemanları ve class isimleri (`.sky`, `.celestial-body`, `.sun-halo`, `.moon-halo`, vs.)
- `getSkyColorByTime()` ve `getSkyColorBySensor()` fonksiyonları
- `getCelestialY()`, `getRoomBrightnessByTime()` vb. yardımcı fonksiyonlar
- 24 Saat Döngüsü / Sensör Modu radio toggle
- Dashboard (info panel, status row)
- Yıldız `setInterval` animasyonu
- `changeMode()` fonksiyonu

---

## Notlar
- Branch: `claude/dal-06-light-sensor-control-oo4Qw`
- Başka branch'e push YASAK
- Orijinal dosya (`RGBL_curve_d2_oda_7.html`) referans olarak korunacak, silinmeyecek
