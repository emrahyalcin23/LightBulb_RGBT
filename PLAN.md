# RGBL Eğri Editörü — Görev Planı

## Referans Prompt
> Orijinal prompt → `PROMPT.md` (değiştirilmez, harfi harfine kayıt)

## Klasör Yapısı
```
RGBL_CurveEditor/
  ├── RGBL_curve_d2_oda_7.html   ← Kullanıcının orijinal çalışan kodu (referans/kaynak)
  ├── RGBL_curve_editor.html     ← Nihai ürün (ana giriş noktası)
  ├── sim-engine.js              ← Simülasyon motoru (updateEnvironment, gökyüzü, oda)
  └── d3-editor.js               ← D3 eğri editörü (düğüm, sürükleme, eğri render)
```

---

## Temel Kural
> `RGBL_curve_editor.html`, `RGBL_curve_d2_oda_7.html`'deki **tüm orijinal yapıyı** koruyacak.

---

## Yapılacaklar

### ✅ Tamamlanan

- Dosyalar `RGBL_CurveEditor/` klasörüne taşındı
- `RGBL_curve_d2_oda_7.svg` → `RGBL_curve_d2_oda_7.html` olarak yeniden adlandırıldı
- `PROMPT.md` oluşturuldu (prompt harfi harfine)
- DEĞİŞİKLİK 1 — İki Panel Düzeni (D3 sol panel + simülasyon sağ panel)
- DEĞİŞİKLİK 2 — `updateEnvironment()` imzası güncellendi (5 parametre, 0-100)
- DEĞİŞİKLİK 3 — D3.js RGBL Eğri Editörü sol panele eklendi
- Kalibrasyon penceresine "RGBL Simülasyon Aracını Aç" butonu eklendi (tarayıcıda açma)
- Kod çok dosyalı yapıya bölündü: `sim-engine.js` + `d3-editor.js` + `RGBL_curve_editor.html`

---

### ✅ GÖREV 4 — Eski HTML'den Yeni HTML'e Geçişteki Eksiklikler

Orijinal `RGBL_curve_d2_oda_7.html`'de çalışan özellikler `RGBL_curve_editor.html`'e taşındı:

- **24 Saatlik Gösterim:** `sim-engine.js`'e eklendi — mod radio toggle (24 Saat / Sensör), `getSkyColorByTime()`, `smoothStep()`, `getRoomBrightnessByTime()`, saat bazlı celestial animasyon
- **Dashboard Bilgileri:** Sağ panele mod toggle + `info-panel` (#valX, #valY) eklendi; `updateEnvironment()` her iki modu destekliyor
- **Simülasyon Tutarsızlıkları:** Gökyüzü rengi, celestial body, room brightness hesapları yeni parametre seti (0-100) ile uyumlu hale getirildi

---

### ✅ GÖREV 5a — Veri Köprüsü (HTML Tarafı)

- **localStorage kayıt/yükleme:** `saveToLocalStorage()` / `loadFromLocalStorage()` — düğüm verileri her değişiklikte `localStorage['rgbl_calibration']`'e yazılır, sayfa açılışında yüklenir
- **JSON dışa aktarma:** `exportJSON()` — `rgbl_calibration.json` dosyasını Blob + anchor click ile indirir
- **"⬇ Kaydet JSON" butonu** `RGBL_curve_editor.html` header'ına eklendi
- **Tetikleyiciler:** `addNode`, `deleteNode`, drag `end`, reset handler

---

### ✅ GÖREV 6b — D3 Grafik Render Sorunu

Kod bölümleme sonrası D3 eğri grafikleri ekranda görünmüyor. Olası nedenler:

- **Katman sırası:** `overlayLayer` (bgRect) eğri/düğüm katmanlarının ALTINA alındı (kritik fix ✅). Buna rağmen görünürlük sorunu devam ediyor.
- **Drag koordinat birimi:** `event.x/y` → `d3.pointer(event, g.node())` olarak geri alındı ✅
- **Mousemove:** `bgRect.on('mousemove')` → `g.on('mousemove')` olarak taşındı ✅
- **Kalan sorun:** Grafiklerin (eğriler + grid + düğümler) ekranda görünmemesi — araştırılıp düzeltilecek

---

### ⏳ GÖREV 6 — Orijinal HTML Düğüm / Sürükleme Hata Düzeltmeleri

D3.js eğri editöründe tespit edilen bug'lar:

- **Düğüm ekleme hatası:** Boş alana / eğriye tıklayınca düğüm eklenmiyor veya yanlış konuma ekleniyor
- **Düğüm çıkarma hatası:** Sağ tık / Shift+Tık ile silme çalışmıyor veya silinmemesi gereken düğümleri (x=0, x=100) siliyor
- **Sürükleme hatası:** Düğüm sürüklenirken koordinat kayması, sınır dışına çıkma, veya x=0/x=100 kilitlemesinin bozulması

---

### ⏳ GÖREV 5 — Eski Avalonia Kalibrasyon Yapısı Kaldırılacak

HTML ekranı, C# uygulamasının kalibrasyon arayüzünün **tamamen yerini** alacak. Eski yapı silinecek.

#### 5a — Veri Köprüsü (ÖNCELİKLİ)

Eski kalibrasyon verisi `SettingsService.UsbCalibrationPoints` (JSON) üzerinde tutuluyor ve `UsbSensorService` tarafından piecewise linear interpolation ile uygulanıyor. HTML bunu değiştirebilmek için:

- **HTML → Export:** Kullanıcı eğrileri düzenleyip "Kaydet" butonuna bastığında, düğüm verisi bir JSON dosyasına yazılacak (örn. `rgbl_calibration.json`, uygulama dizininde)
- **C# → Import:** `UsbSensorService` bu JSON dosyasını okuyacak, `UsbCalibrationPoints` benzeri veri yapısına dönüştürecek
- **localStorage:** HTML, düğüm verilerini `localStorage`'a da kayıt edecek (sayfa kapanıp açıldığında kaybolmasın)
- **Dosya yolu:** `AppContext.BaseDirectory/rgbl_calibration.json` (build'e Content olarak eklenmiş)

#### 5b — Kaldırılacak / Değiştirilecek Dosyalar

Eski Avalonia kalibrasyon yapısına ait tüm dosyalar:

| Dosya | Yapılacak |
|-------|-----------|
| `LightBulb/Models/UsbCalibrationPoint.cs` | Silinecek (yerini JSON model alacak) |
| `LightBulb/Views/Components/CalibrationCurveControl.cs` | Silinecek (custom Avalonia çizim kontrolü) |
| `LightBulb/Views/Dialogs/UsbCalibrationWindow.axaml` | Silinecek → sadece "HTML aracını aç" butonu kalacak |
| `LightBulb/Views/Dialogs/UsbCalibrationWindow.axaml.cs` | Silinecek |
| `LightBulb/ViewModels/Dialogs/UsbCalibrationViewModel.cs` | Büyük ölçüde sadeleştirilecek (sadece `OpenSimulationCommand` kalacak) |
| `LightBulb/ViewModels/Components/Settings/UsbSensorSettingsTabViewModel.cs` | Kalibrasyon açma çağrısı gözden geçirilecek |
| `LightBulb/Services/SettingsService.cs` | `UsbCalibrationPoints` kaldırılacak, `rgbl_calibration.json` okuma eklenecek |
| `LightBulb/Services/UsbSensorService.cs` | Piecewise interpolation JSON'dan okunacak şekilde güncellencek |
| `LightBulb/Framework/ViewModelManager.cs` | `UsbCalibrationViewModel` kaydı gözden geçirilecek |
| `LightBulb/Framework/ViewManager.cs` | `UsbCalibrationWindow` kaydı gözden geçirilecek |

---

## Notlar
- Branch: `claude/dal-06-light-sensor-control-oo4Qw`
- Başka branch'e push YASAK
- Orijinal dosya (`RGBL_curve_d2_oda_7.html`) referans olarak korunacak, silinmeyecek
- JS dosyaları (`sim-engine.js`, `d3-editor.js`) HTML ile aynı dizinde olmalı
