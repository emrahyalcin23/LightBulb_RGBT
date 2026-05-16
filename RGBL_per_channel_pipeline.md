# RGBL Per-Channel Pipeline — Matematiksel Formülasyon

Bu belge, renk sensöründen gelen bağımsız RGB kanal değerlerinin ekran gama çıktısına nasıl
dönüştürüldüğünü adım adım açıklar.  
Formüller LaTeX ile yazılmıştır.

---

## Değişken Tablosu

| Sembol | Tanım | Aralık |
|---|---|---|
| $R_s,\; G_s,\; B_s$ | Sensörden gelen proc değerleri | $[0,\;100]$ |
| $A_L$ | CIE-Y ağırlıklı luminans — L eğrisinin X girdisi | $[0,\;100]$ |
| $A_R,\; A_G,\; A_B$ | Kanal bazlı bağımsız X girdileri | $[0,\;100]$ |
| $A_{min},\; A_{max}$ | Eğri giriş min-max ölçekleme sınırları | $[0,\;100]$ |
| $X_c$ | $c$ kanalının normalleştirilmiş eğri giriş değeri | $[0,\;100]$ |
| $E_c(x)$ | $c$ kanalının eğri fonksiyonu (RGBL editöründen) | $[0,\;100] \to [0,\;100]$ |
| $f_R,\; f_G,\; f_B,\; f_L$ | Ham eğri çıktıları | $[0,\;100]$ |
| $m$ | Renk kanalları arasındaki maksimum değer | $[0,\;100]$ |
| $D_R,\; D_G,\; D_B$ | Final display değerleri (gama girişi) | $[0,\;100]$ |
| $O_{min},\; O_{max}$ | Çıkış min-max ölçekleme sınırları | $[0,\;100]$ |
| $\gamma_c$ | $c$ kanalının gama çarpanı | $[0,\;1]$ |

---

## Adım 1 — Bağımsız Girdi Değerleri

L kanalı CIE-Y ağırlıklı ortalama ile hesaplanır (ITU-R BT.709 katsayıları).  
RGB kanalları doğrudan sensör proc değerlerinden alınır:

$$A_L = 0.2126 \cdot R_s + 0.7152 \cdot G_s + 0.0722 \cdot B_s$$

$$A_R = R_s, \qquad A_G = G_s, \qquad A_B = B_s$$

**Örnek** ($R_s = 35,\; G_s = 90,\; B_s = 90$):

$$A_L = 0.2126 \times 35 + 0.7152 \times 90 + 0.0722 \times 90
      = 7.44 + 64.37 + 6.50 = 78.31$$

$$A_R = 35, \qquad A_G = 90, \qquad A_B = 90$$

> **Önceki akışla fark:** Eski akışta $A_L$ tek skalar olarak dört eğriye de aynı şekilde
> giriliyordu.  Yeni akışta L eğrisi $A_L$'yi, RGB eğrileri ise kendi bağımsız $A_c$ değerini alır.

---

## Adım 2 — Eğri Giriş Normalizasyonu (min-max)

Her kanal kendi $A_c$ değerini aynı $[A_{min},\; A_{max}]$ aralığına göre normalleştirir:

$$X_c = A_{min} + \frac{A_c}{100} \cdot \bigl(A_{max} - A_{min}\bigr),
\qquad c \in \{R,\; G,\; B,\; L\}$$

Varsayılan $A_{min} = 0,\; A_{max} = 100$ olduğunda bu adım özdeşliktir ($X_c = A_c$).

**Örnek** (varsayılan sınırlar):

$$X_R = 35, \qquad X_G = 90, \qquad X_B = 90, \qquad X_L = 78.31$$

---

## Adım 3 — Eğri Değerlendirmesi

Her kanal **kendi $X_c$ değeriyle** kendi eğrisinden örneklenir:

$$f_R = E_R(X_R), \quad f_G = E_G(X_G), \quad f_B = E_B(X_B), \quad f_L = E_L(X_L)$$

Tam doğrusal eğri ($E_c(x) = x$) için:

$$f_R = 35, \qquad f_G = 90, \qquad f_B = 90, \qquad f_L = 78.31$$

> **Önceki akışla fark:** Eski akışta dört eğri de $X_L = A_L = 78.31$'den örnekleniyordu;
> doğrusal profilde tümü aynı değeri ($78.31$) üretiyordu.

---

## Adım 4 — MaxNormalize: Renk Oranı × Parlaklık

$f_R,\; f_G,\; f_B$ renk **oranını**; $f_L$ ise genel **parlaklık şiddetini** belirler.

$$m = \max(f_R,\; f_G,\; f_B)$$

$$D_c = \frac{f_c}{m} \cdot f_L, \qquad c \in \{R,\; G,\; B\}$$

$m = 0$ kenar durumunda ($f_R = f_G = f_B = 0$) geri dönüş:

$$D_R = D_G = D_B = \frac{f_L}{3}$$

**Örnek:**

$$m = \max(35,\; 90,\; 90) = 90$$

$$D_R = \frac{35}{90} \times 78.31 = 0.3\overline{8} \times 78.31 = 30.45$$

$$D_G = \frac{90}{90} \times 78.31 = 1.0 \times 78.31 = 78.31$$

$$D_B = \frac{90}{90} \times 78.31 = 1.0 \times 78.31 = 78.31$$

**Garanti edilen özellikler:**

$$\max(D_R,\; D_G,\; D_B) = f_L \quad \checkmark$$

$$D_R : D_G : D_B \;=\; f_R : f_G : f_B \quad \checkmark$$

---

## Adım 5 — Çıkış Normalizasyonu (min-max, opsiyonel)

$[0,\;100]$ çıktı aralığı $[O_{min},\; O_{max}]$'a ölçeklenir:

$$D_c^{*} = O_{min} + \frac{D_c}{100} \cdot \bigl(O_{max} - O_{min}\bigr),
\qquad c \in \{R,\; G,\; B,\; L\}$$

$O_{min} = 0,\; O_{max} = 100$ varsayılanında bu adım özdeşliktir.

---

## Adım 6 — Gama Çarpanı

$$\gamma_c = \frac{D_c^{*}}{100}, \qquad c \in \{R,\; G,\; B\}$$

**Örnek:**

$$\gamma_R = 0.3045, \qquad \gamma_G = 0.7831, \qquad \gamma_B = 0.7831$$

---

## Adım 7 — Gama Ramp (Donanım)

Her parlaklık seviyesi $i \in [0,\;255]$ için:

$$\text{ramp}_c[i] = \text{round}\!\left(i \times 255 \times \gamma_c\right)$$

---

## Tam Pipeline Özeti

$$\boxed{
  D_c = \frac{E_c\!\left(A_{min} + \dfrac{A_c}{100}(A_{max}-A_{min})\right)}
             {\displaystyle\max_{k \in \{R,G,B\}} E_k\!\left(A_{min} + \dfrac{A_k}{100}(A_{max}-A_{min})\right)}
       \times E_L\!\left(A_{min} + \dfrac{A_L}{100}(A_{max}-A_{min})\right)
}$$

burada:

$$A_R = R_s, \quad A_G = G_s, \quad A_B = B_s, \quad
A_L = 0.2126\,R_s + 0.7152\,G_s + 0.0722\,B_s$$

---

## Eski Akışla Karşılaştırma

| | Eski Akış | Yeni Akış |
|---|---|---|
| RGB eğrisi X girdisi | $A_L$ (luminans) | $A_R, A_G, A_B$ (her kanal bağımsız) |
| L eğrisi X girdisi | $A_L$ | $A_L$ (değişmez) |
| Doğrusal profilde çıktı | $D_R = D_G = D_B = A_L$ | $D_R : D_G : D_B = R_s : G_s : B_s$ |
| RGBL editörü | Kullanılıyor | Kullanılıyor (değişmez) |
| min-max ölçekleme | Kullanılıyor | Kullanılıyor (değişmez) |
| MaxNormalize | Kullanılıyor | Kullanılıyor (değişmez) |

---

## Etkilenen Kaynak Dosyalar

| Dosya | Değişen Yer |
|---|---|
| `LightBulb/Services/RgblCurveEvaluator.cs` | `Evaluate(ambientPct)` → `Evaluate(ambR, ambG, ambB, ambL)` |
| `LightBulb/Services/UsbSensorService.cs` | Tek `curveInput` yerine dört bağımsız `curveInputR/G/B/L` hesabı |
