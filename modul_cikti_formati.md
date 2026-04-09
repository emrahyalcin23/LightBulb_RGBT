
// ===========================================
// ========== STANDART ÇIKTI FORMATI ==========
// ===========================================

// Yeni Output Type'lar ekleyelim
enum OutputType {
    OUT_LIVE = 0,    // Canlı akış
    OUT_SINGLE = 1,  // Tek okuma
    OUT_AVERAGE = 2, // Ortalama
    OUT_RAW = 3,     // Ham veri
    OUT_STATUS = 4,  // Durum mesajı
    OUT_ERROR = 5,   // Hata mesajı
    OUT_DUAL = 6     // YENİ: Hem ham hem işlenmiş
};

// Çift çıktı (RAW + Processed) için fonksiyon
void printDualOutput(OutputType type, int mode, 
                     uint16_t raw_r, uint16_t raw_g, uint16_t raw_b, uint16_t raw_c,
                     float proc_r, float proc_g, float proc_b,
                     const char* meta = "") {
    unsigned long ts = millis();
    Serial.print(ts);
    Serial.print(';');
    Serial.print((int)type);
    Serial.print(';');
    Serial.print(mode);
    Serial.print(';');
    
    // Ham veri (RAW) - orijinal uint16_t değerler
    Serial.print(raw_r);
    Serial.print(';');
    Serial.print(raw_g);
    Serial.print(';');
    Serial.print(raw_b);
    Serial.print(';');
    Serial.print(raw_c);
    Serial.print(';');
    
    // İşlenmiş veri (0-100 arası, 1 ondalık)
    Serial.print(proc_r, 1);
    Serial.print(';');
    Serial.print(proc_g, 1);
    Serial.print(';');
    Serial.print(proc_b, 1);
    
    if (strlen(meta) > 0) {
        Serial.print(';');
        Serial.print(meta);
    }
    Serial.println();
}
