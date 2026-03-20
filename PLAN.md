# RGBL Eğri Editörü — Görev Planı

## Referans Prompt
> Orijinal prompt → `PROMPT.md` (değiştirilmez, harfi harfine kayıt)

## Klasör Yapısı
```
RGBL_CurveEditor/
  ├── RGBL_curve_d2_oda_7.html   ← Kullanıcının orijinal çalışan kodu (referans/kaynak)
  └── RGBL_curve_editor.html     ← Nihai ürün
```

---

## Temel Kural
> `RGBL_curve_editor.html`, `RGBL_curve_d2_oda_7.html`'deki **tüm orijinal yapıyı** koruyacak.
> Sadece aşağıdaki 3 değişiklik yapılacak, **başka hiçbir şey değiştirilmeyecek.**

---

## Yapılacaklar

### ✅ Tamamlanan
- Dosyalar `RGBL_CurveEditor/` klasörüne taşındı
- `RGBL_curve_d2_oda_7.svg` → `RGBL_curve_d2_oda_7.html` olarak yeniden adlandırıldı
- `PROMPT.md` oluşturuldu (prompt harfi harfine)

### ⏳ DEĞİŞİKLİK 1 — Düzen: İki Panel
Orijinal tek panel layout → iki sütunlu layout:
- **Sol panel (yeni):** D3.js RGBL Eğri Editörü
- **Sağ panel (orijinal):** Mevcut `simulation-container` + `dashboard` aynen korunacak
- `body` layout: dikey ortalı → `display:flex; flex-direction:row`
- `simulation-container` ve `dashboard` genişlikleri: sabit px → `width:100%; max-width:Xpx` (responsive, içerik aynı)

### ⏳ DEĞİŞİKLİK 2 — `updateEnvironment()` imzası güncelleme
**Orijinal:** `updateEnvironment(xRaw, yRaw)` — 2 parametre, 0-255 aralığında

**Yeni:** `updateEnvironment(ambientLight, finalR, finalG, finalB, finalL)` — 5 parametre, 0-100 aralığında

Detaylar:
- `t_param` / `sensorRatio`: `xRaw / 255` → `ambientLight / 100`
- Sabit oran RGBL hesabı (`R=y*1.0`, `G=y*0.92`, `B=y*1.05`) **kaldırılacak**
- Ekran rengi: `rgb(yRaw, yRaw*0.92, yRaw*1.05)` → `rgb(finalR*2.55, finalG*2.55, finalB*2.55)`
- Ekran glow opaklığı: `(yRaw/255)*0.65` → `(finalL/100)*0.65`
- `elX.innerText`: artık doğrudan `Math.round(ambientLight)` (zaten 0-100)
- `elY.innerText`: `R:finalR G:finalG B:finalB L:finalL`
- `currentX_raw`, `currentY_raw` → `currentAmbient` (0-100)
- `changeMode()`: `updateEnvironment(currentX_raw, currentY_raw)` → `triggerUpdate(currentAmbient)`
- `mousemove` listener: **kaldırılacak** (kontrolü D3'e devrettik)
- Sky/celestial/room/yıldız mantığı: **aynen korunacak**

### ⏳ DEĞİŞİKLİK 3 — D3.js Sol Panel Ekleme
Prompt teknik gereksinimleri:
- X ekseni: Ortam Işığı (0-100), Y ekseni: Çıkış (0-100)
- 4 kanal: R (kırmızı), G (yeşil), B (mavi), L (beyaz, kalın, dashed)
- Eğri tipi: `d3.curveCatmullRom`
- Boş yere tıklama → aktif kanala düğüm ekleme
- Eğriye tıklama → o kanala düğüm ekleme
- Düğüm sürükleme (x+y serbestçe, 0-100 sınırlı)
- Sağ tık / Shift+Tık → düğüm silme
- x=0 ve x=100 düğümleri korumalı (silinemez, x ekseni kilitli)
- Hover (farenin grafik üzerindeki x konumu) → `updateEnvironment()` anlık tetiklenir
- `Final_R = Curve_R * (Curve_L / 100)` matematiği
- D3.js CDN üzerinden dahil edilecek
- Dark mode tasarım

---

## Korunacak Orijinal Yapı (DEĞİŞTİRİLMEYECEK)
- Tüm CSS class'ları ve CSS variables (`--sky-color`, `--room-brightness`, `--screen-color`, vs.)
- SVG elemanları ve class isimleri (`.sky`, `.celestial-body`, `.sun-halo`, `.moon-halo`, vs.)
- `getSkyColorByTime()` ve `getSkyColorBySensor()` fonksiyonları
- `getCelestialY()`, `getCelestialYSensor()`, `getRoomBrightnessByTime()`, `getRoomBrightnessBySensor()`
- `smoothStep()` fonksiyonu
- 24 Saat Döngüsü / Sensör Modu radio toggle
- Dashboard (info panel, status row, toggle-group)
- Yıldız `setInterval` animasyonu
- `generateStars()` fonksiyonu

---

## Notlar
- Branch: `claude/dal-06-light-sensor-control-oo4Qw`
- Başka branch'e push YASAK
- Orijinal dosya (`RGBL_curve_d2_oda_7.html`) referans olarak korunacak, silinmeyecek
