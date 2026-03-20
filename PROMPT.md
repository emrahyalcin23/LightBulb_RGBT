Sistem, ortam ışığını (0-100) bir USB sensörden alıp, bilgisayar ekranının parlaklığını ve RGB renk dengesini dinamik olarak ayarlayacak bir açılır ekran (html/js olarak düşündüm ama mantıklı değilse söyle) yap. Bana "uydurma" kodlar değil, tamamen çalışan, test edilmiş ve teknik gereksinimlere harfiyen uyan bir kod yazmanı istiyorum.
            ŞU ANA KADAR YAPTIKLARIMIZ (MEVCUT DURUM):
            Açılır ekranın Sağ tarafında çalışacak, ortam ışığına ve ekran parlaklığına göre dinamik olarak değişen bir "Simülasyon Odası" (SVG + JS) kodunu zaten yazdım. Bu kod kusursuz çalışıyor. CSS değişkenleri (--sky-color, --screen-color vb.) üzerinden güncelleniyor. Mevcut kodda farenin ekrandaki rastgele konumu (mousemove) bu değerleri değiştiriyor. Kodun en altında bu SVG yapısını sana vereceğim.
            SENDEN İSTEDİĞİM GÖREV (ASIL İŞ):
            Açılır Ekranı ikiye bölmeni istiyorum. Sağ tarafta benim vereceğim SVG Simülasyon Odası duracak. Sol tarafta ise asıl kontrol mekanizması olan D3.js tabanlı, interaktif bir "RGBL Eğri Grafiği" (Curve Editor) inşa edeceksin. Sonra bu D3.js grafiğini sağ taraftaki SVG simülasyonu ile konuşturacaksın.
            D3.js GRAFİĞİ İÇİN TEKNİK GEREKSİNİMLER (SOL PANEL):
            1. Eksenler: X Ekseni (Ortam Işığı 0-100). Y Ekseni (Gama/Parlaklık Çıktısı 0-100).
            2. 4 Farklı Kanal: R (Kırmızı), G (Yeşil), B (Mavi) ve L (Luminance/Master).
            - L kanalı kalın, beyaz ve kesik çizgili (dashed) olmalı. Diğerleri kendi renklerinde olmalı.
            3. Etkileşim (Çok Önemli):
            - Eğriler doğrusal değil, yumuşak kavisli (d3.curveCatmullRom) olmalı.
            - Grafikte boş bir yere tıklandığında (veya bir çizgiye tıklandığında) o X noktasına yeni bir kontrol düğümü (node) eklenmeli.
            - Düğümler hem X hem de Y ekseninde serbestçe (0-100 sınırları içinde) sürüklenebilmeli (d3.drag).
            - Bir düğüme sağ tıklandığında (veya Shift+Tık yapıldığında) o düğüm silinmeli. (Baştaki X=0 ve sondaki X=100 düğümleri silinemez olmalı).
            ENTEGRASYON VE MATEMATİK GEREKSİNİMLERİ (SİHİRLİ KISIM):
            Fare ile rastgele çalışan mevcut SVG yapısını iptal edip, kontrolü tamamen D3.js grafiğine vereceksin.
            1. Kullanıcı D3.js grafiği üzerinde fareyi gezdirdikçe (hover) veya bir düğümü sürükledikçe, farenin bulunduğu o anki "X" değeri (Ortam Işığı) alınacak.
            2. O "X" noktasında, 4 eğrinin de (R, G, B, L) interpolasyonla denk geldiği "Y" değerleri hesaplanacak.
            3. Çıktı Matematiği: L kanalı, RGB kanallarının çarpanıdır.
            - Final_R = Curve_R_Ydeğeri * (Curve_L_Ydeğeri / 100)
            - Final_G = Curve_G_Ydeğeri * (Curve_L_Ydeğeri / 100)
            - Final_B = Curve_B_Ydeğeri * (Curve_L_Ydeğeri / 100)
            4. Bulunan bu "X" (Ortam Işığı) ve "Final RGBL" değerleri, anlık olarak benim yazdığım SVG simülasyonundaki updateEnvironment() fonksiyonuna gönderilerek sağ panelin anında güncellenmesi sağlanacak.
            Tasarım olarak karanlık tema (dark mode) kullan. HTML dosyasını yazarken D3.js kütüphanesini CDN üzerinden dahil etmeyi unutma.
            İşte referans alman ve entegre etmen gereken mevcut HTML/JS yapım (SVG'yi ayrı dosyaya taşıyacağını unutma):
            [HTML/SVG/JS KODU 'RGBL_curve_d2_oda_7' dosyasında. dosyayı ana klasöre koydum ama olması gereken yere taşı.]
