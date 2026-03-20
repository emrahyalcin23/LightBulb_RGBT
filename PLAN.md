# RGBL Curve Editor — Görev Planı

## Mevcut Durum
- `RGBL_curve_editor.html` oluşturuldu ve push edildi.
- Sol panel: D3.js RGBL eğri editörü
- Sağ panel: SVG simülasyon odası
- Entegrasyon: D3 → updateEnvironment() çalışıyor

## Yapılacaklar

### ✅ 1. RGBL_curve_editor.html oluştur
- D3.js editörü (sol panel)
- SVG simülasyon odası (sağ panel)
- mousemove kaldırıldı, kontrol D3'e devredildi
- Final_R/G/B = Curve * (L/100) matematiği

### ⏳ 2. Test & Doğrulama
- [ ] Tarayıcıda açılıp açılmadığı
- [ ] D3 CDN yüklenip yüklenmediği
- [ ] Eğri hover → simülasyon güncellemesi
- [ ] Düğüm ekleme/silme/sürükleme
- [ ] x=0 ve x=100 düğümlerinin korumalı olduğu

### ⏳ 3. Olası İyileştirmeler (kullanıcı onayına göre)
- [ ] Keyboard shortcut: Delete tuşu ile seçili düğüm silme
- [ ] Export: JSON olarak eğri noktaları kaydetme
- [ ] Import: JSON'dan eğri yükleme
- [ ] Sensor mode toggle (USB sensör simülasyonu)

## Notlar
- Branch: `claude/dal-06-light-sensor-control-oo4Qw`
- Başka branch'e push YASAK
- Her adım tamamlandıkça bu dosya güncellenir
