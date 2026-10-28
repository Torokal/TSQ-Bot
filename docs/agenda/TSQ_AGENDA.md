# TSQ Ajanda — /ajanda ve /gundem

Modül kimliği `agenda` (`ToroSquad.Modules.Agenda`). Varsayılan **kapalı**: `/modules enable agenda`. Açıldıktan sonra
herkes kullanabilir; yönetici işlemi yoktur (`/modules enable|disable` yeterli, `/tsq-admin` altında işlem yok).

TSQ Ajanda bir **görünümdür**: F1, CS2, voleybol, LFG ve Öngörü modüllerinin zaten tuttuğu veriyi okur. Yeni sağlayıcı, crawler,
canlı bağlantı, AI özeti, polling işi, tablo, migration veya yapılandırma yoktur. Komut çalışınca hiçbir sağlayıcıya istek
gitmez, hiçbir kayıt yazılmaz, bildirim/öngörü/LFG ilanı oluşmaz, coin/takip/katılım işlemi yapılmaz. Otomatik günlük
veya haftalık paylaşım yoktur.

## Komutlar

| Komut | Ne gösterir |
|---|---|
| `/ajanda [spor] [donem]` | **Spor programı.** `spor`: Tüm sporlar (varsayılan), Formula 1, CS2, Voleybol. `donem`: Bugün, Yarın, Önümüzdeki 7 gün (varsayılan), Önümüzdeki 14 gün. LFG içermez. |
| `/gundem` | **Bugünün kalanı ve yarın**: F1 seansları, CS2 ve voleybol maçları, uygun LFG ilanları ve açık öngörüler tek kartta. Seçeneği yoktur. |

İkisi de düz komuttur (alt komut/grup yok), herkesindir, yalnızca sunucuda çalışır. Başarılı cevap komutun kullanıldığı
kanala **herkese açık** ve **ping'siz** tek bir karttır (önce herkese açık "düşünüyor…" onayı, sonra kart — özel bir onay
sonradan herkese açık cevaba dönüşemez). Modül kapalıysa, DM'de veya geçersiz bir düğmede cevap yalnızca kullanana görünür.

**Sonuç gösterilmez:** skor, kazanan, set/harita sonucu, sıralama, puan, Safety Car / kırmızı bayrak / diskalifiye bilgisi
yoktur. Bitmiş etkinlik listeden çıkar; sonuç listesine dönüşmez.

## Gün ve saat

- "Şimdi" bir kez alınır (`TimeProvider`); aynı kart için her yer aynı anı kullanır.
- Günler **sunucunun** IANA saat dilimine göredir (`/setup` ayarı; yoksa varsayılan `Europe/Istanbul`). Host makinenin
  saati, kullanıcının dili veya Discord yerel ayarı kullanılmaz.
- Her yerel günün başı ayrı ayrı UTC'ye çevrilir: yaz saati günü 23 veya 25 saattir, gece yarısının hiç yaşanmadığı
  bölgelerde gün ilk geçerli dakikada başlar. Pencere `[başlangıç, bitiş)`.
- `/gundem` = yerel takvimde bugün + yarın ("şu andan 48 saat" değil). `/ajanda`: Bugün, Yarın, bugün dahil 7 veya 14 gün.
- Saatler Discord zaman damgasıdır (`<t:…:f>` · `<t:…:R>`): her okuyucu kendi yerel saatini görür. Kartın altında:
  "Günler: Europe/Istanbul · Saatler sizin yerel saatinizde".
- Saati bilinmeyen ama tarihi bilinen etkinlik "Saat açıklanmadı" ile o güne yazılır (00:00 uydurulmaz); tarihi de
  bilinmeyen etkinlik hiçbir güne konmaz, veri durumunda sayısı belirtilir.

## Durum ve güncellik

- **Devam ediyor** yalnızca kaynağın güncel verisi başlamış ve bitmemiş diyorsa. Planlanan saat geçti diye canlı denmez:
  "Başlangıç saati geçti; durum doğrulanamadı". Eski (stale) veride canlı durum hiç gösterilmez.
- İptal edilmiş etkinlik "İptal edildi" satırıyla görünür; yeni saati belli erteleme yeni saatinde, saati belli
  olmayan erteleme sahte bir saatle gösterilmez.
- **Veri durumu** alanı her kaynak için ayrı satırdır: son gerçek başarılı veri zamanı (kartın oluşturulma saati
  değil), eski veri uyarısı, "veriye şu an ulaşılamıyor", "henüz veri yok", "modül bu sunucuda kapalı", kaynağın
  kapsamadığı tarih ("veri … sonrasını kapsamıyor"), TEST/DEMO verisi, tarihsiz kayıt sayısı, filtre verisi eksik
  olduğu için gösterilmeyen maç sayısı. Bir kaynak bozuksa diğerleri gösterilmeye devam eder. Veri eksikken "etkinlik
  yok" denmez; "bazı veriler eksik" denir.
- Kaynak modülü sunucuda kapalıysa önbellekte veri kalsa bile okunmaz. Bildirim duraklatma (pause) modülü kapatmaz.

## Kaynaklar

| Kaynak | Okunan | Kapsam |
|---|---|---|
| F1 (`formula1`) | `Formula1Cache` takvimi: antrenman, sprint sıralama, sprint, sıralama, yarış — **seans başına bir satır** (hafta sonu ayrıca değil). Bildirim anahtarları kapalı olsa da takvimdeki seans görünür. | Bitmiş seans yok; kırmızı bayrak dahil koşan seans yalnızca "Devam ediyor" (16 saatten eski yaşam döngüsü canlı sayılmaz); sonuç, puan durumu, race control okunmaz. Kapsam: önbellekteki son sezonun sonu. |
| CS2 (`esports`) | `EsportsCache` maçları | Sunucunun **sunucu filtreleri** bildirim planlayıcısıyla aynı kuralla (`MatchFilter`: boyut içinde VEYA, boyutlar arasında VE, filtre yoksa tüm maçlar). VRS verisi yoksa filtre karar veremez → maç **gösterilmez** ve sayılır. Teslim ayarları (kanal, ping rolü, hatırlatma, spoiler modu, pause) ve üyelerin kişisel takipleri etkisizdir. Bitmiş maç yok; skor/harita okunmaz. Kapsam: son veri + 48 saat. |
| Voleybol (`volleyball`) | `VolleyballCache` maçları | Yalnızca modülün izlediği takım (Filenin Sultanları — Türkiye Kadın A Milli Takımı); yeni takım/lig eklenmez. Set/sayı okunmaz. Canlı yalnızca son 30 dakikadaki sağlayıcı gözlemiyle. Kapsam: son veri + 60 gün. |
| LFG (`lfg`, yalnızca `/gundem`) | Aktif ilanlar | Aşağıdaki kurallar. |
| Öngörü (`predictions`, yalnızca `/gundem`) | Bu sunucunun `prediction` kayıtları | Aşağıdaki kurallar. |

### LFG

- Yalnızca **Açık** veya **Dolu** ve süresi dolmamış ilanlar (`ExpiresAt > şimdi`; süre dolmuş ama worker henüz
  yazmamış ilan da sayılmaz). Kapanmış, süresi geçmiş, kartı kaybolmuş (Orphaned) ilan yok.
- Planlı ilan kendi gününe; planlanmış saati olmayan ilan bugünün **"Şimdi ekip arayanlar"** bölümüne (oluşturulma saati
  etkinlik saati gibi gösterilmez). Saati geçmiş ama açık ilan: "Başlangıç saati geçti · ilan açık" (oyunun başladığı
  iddia edilmez).
- Kontenjan yalnızca **katılanlar**dır ("Belki" ve bekleme listesi sayılmaz): "3/6 kişi" veya "6/6 · Dolu / bekleme listesi".
- Karta yalnızca oyun adı, saat, kontenjan ve ilan bağlantısı çıkar; katılımcı adı, ayrıntı metni, ses odası yoktur.

### Öngörü

- Yalnızca **açık** (tahmin alıyor) veya **kilitli ama sonuçlanmamış** öngörüler, son tahmin saati bugün veya yarınsa.
  Son tahmin saati olmayan açık öngörü bugünün **"Şu an açık öngörüler"** bölümündedir. Saati olmayan kilitli öngörü,
  sonuçlanmış, iptal edilmiş, yarım kalmış veya henüz yayınlanmakta olan öngörü listede **yoktur** (sonuç ima edilmez).
- Satırda başlık, son tahmin saati ve durum: "🟢 Son tahmin saati", "🟢 Tahminler açık" veya "🔒 Tahminler kapandı ·
  sonuç bekleniyor". Son tahmin saati geçtiyse modül tahmin almadığı için kilit işi henüz çalışmamış olsa da "kapandı" yazar.
- **Gösterilmez:** seçenekler ve oranlar, kazanan seçenek, kazanan sayısı, katılım sayısı, coin tutarları, kimin ne
  tahmin ettiği, cüzdanlar, sıralama, kurallar, oluşturan kişi.
- Bağlantı öngörünün kendi kartıdır; kart Discord'da silinmiş (`CardMissing`) veya saklama süresi sonunda kaldırılmışsa
  (`CardRemovedAt`) bağlantı verilmez. Öngörünün kanalı LFG ile aynı kanal kuralına tabidir. Tek seferde en fazla 100 öngörü
  okunur; aşılırsa veri durumunda sayı vermeden "liste eksik olabilir" yazar.

**Herkese açık hedef kuralı:** kart kanaldaki herkese açık olduğu için ilan yalnızca şu durumlarda gösterilir:

1. İlan **aynı kanaldadır**, ya da
2. İlanın kanalı **herkese açık olduğu kanıtlanan** bir kanaldır: @everyone kanalı görebilir, hiçbir rol veya üye
   izni görmeyi kaldırmaz, kanal bir thread değildir ve yaş sınırlı (NSFW) değildir (`IGuildGateway.GetChannelAudienceAsync`,
   gateway önbelleğinden; üye listesi indirilmez).

Kanıtlanamayan her ilan (bilinmeyen kanal, gizli kanal, rol/üye kısıtlaması, thread, NSFW, kontrol hatası) kartta **hiç**
görünmez: başlığı, oyunu, sahibi, bağlantısı ve sayısı dahil. Yönetici olmak bu kuralı aşmaz. Başka sunucunun kaydı
hiçbir durumda okunmaz.

- **Aynı kanal istisnası:** komutun kullanıldığı kanaldaki ilan, o kanal özel olsa bile gösterilir — kartı okuyan herkes
  zaten o kanalı okuyor; komutu kullanan üye kanala erişimini komutu orada kullanarak kanıtlamıştır. Kontrol yapılmaz.
- **Kontrol bütçesi:** kart başına her farklı kanal **bir kez** kontrol edilir (aynı kanaldaki ikinci ilan veya ilanın
  kendi bağlantısı aynı kararı kullanır). Kontrol gateway önbelleğinden okunur (REST çağrısı yok), bu yüzden sınır 250
  kanaldır — LFG kaynağı tek seferde en fazla 200 ilan okur; sınır yalnızca olağan dışı bir kartı durdurur. Kontroller en
  erken etkinlikten başlar. Özel kanallar sırada önce gelse de sonraki herkese açık ilan gösterilir.
- **Eksik kapsam:** bir kanal kontrol edilemediyse (önbellekte yok, hata, sınır) veya LFG kaynağı 200 ilan sınırına
  ulaştıysa ilan gösterilmez ve veri durumunda "⚠️ bazı kayıtlar bu kartta doğrulanamadı; liste eksik olabilir" yazar —
  **sayı verilmez** (bazıları özel olabilir); boş gün de "etkinlik yok" yerine "bazı veriler eksik" der. Gizli olduğu
  kanıtlanan ilan ise eksik sayılmaz, hiçbir iz bırakmaz.
- **Her tıklamada yeniden:** karar o kart üretimi için tutulur; Önceki/Sonraki/Yenile her seferinde kanalları yeniden
  kontrol eder (önceki karara güvenilmez).

## Bağlantılar

Öncelik: (1) gerçekten gönderilmiş TSQ kartı, (2) kaynak modülün zaten doğruladığı resmî sayfa, (3) ilgili komut
(`/f1 next`, `/esports matches`, `/volleyball schedule`), (4) hiçbiri — bağlantı uydurulmaz.

- Discord bağlantısı yalnızca bu sunucu + kanal + mesaj kimliklerinden kurulur. Yalnızca outbox'ta **Sent**, dry-run
  olmayan, mesaj kimliği bilinen ve **bu sunucu ve modülün** kayıtları sayılır (bekleyen, gönderimde, başarısız, belirsiz,
  iptal, süresi dolmuş, simüle kayıt asla). Kanal geçmişi taranmaz; kart yoksa yeni kart oluşturulmaz; kart için Discord'a
  istek atılmaz.
- **Bilinen silinmiş kart:** outbox sonraki bir düzenlemede mesajın silindiğini (`edit_target_deleted`) veya kanalın
  kaybolduğunu (`edit_failed UnknownChannel`) kaydettiyse kart bağlanmaz.
- **Eski ve yeni kart:** aynı etkinlik ve aynı kart türü için en yeni gönderim seçilir. En yeni kart silinmiş veya
  kullanılamıyorsa daha eski kart (eski programı gösterebilir) kullanılmaz; sıradaki kart türüne (CS2/voleybol:
  hatırlatma → başlangıç; F1: yarış hatırlatması → hafta sonu programı), sonra resmî sayfaya veya komuta geçilir.
- **Kanal kuralı:** kart bu kartın kanalında değilse, kartın kanalı LFG ile aynı kurala göre herkese açık olmalıdır. Gizli,
  rol kısıtlı, thread, NSFW veya artık var olmayan kanaldaki karta bağlanılmaz; yerine resmî sayfa, o da yoksa komut.
- **Son bilinen durum:** bağlantı "teslim edilmiş ve silindiği bilinmiyor" demektir, mesajın şu an var olduğunun canlı
  kanıtı değildir. Düzenlenmeden silinen bir mesaj outbox'ta görünmez; böyle bir bağlantı Discord'da "bilinmeyen mesaj"
  açabilir.
- Yalnızca program kartları: F1 hafta sonu programı (yarış için hatırlatma kartı); CS2 hatırlatma veya başlangıç kartı;
  voleybol hatırlatma veya başlangıç kartı. Sonuç, set ve final kartlarına bağlanılmaz. CS2 resmî maç sayfası "Maç
  sayfası — sonuç içerebilir" diye etiketlenir.
- LFG: ilanın kendi kartı ("İlanı aç"); kart kimliği henüz yoksa bağlantı verilmez. İlan yalnızca kanalı yukarıdaki
  kurala uyuyorsa gösterildiği için bağlantısı da başka bir yoldan dışarı çıkmaz.
- Her etkinliğin bir Discord kartı olması gerekmez (bildirim henüz gitmemiş olabilir).

## Kart ve sayfalama

- Bir komut → bir mesaj → tek embed. Günler başlık; her günün içinde başlangıca göre, eşitlikte tür ve kaynak kimliğine
  göre kararlı sıra. Kimlik kaynak + etkinlik kimliğidir (benzer başlıklı iki maç iki satırdır).
- Sayfa başına en fazla 10 etkinlik ve yaklaşık 3600 karakter; Discord sınırları (açıklama 4096, 25 alan, alan 1024,
  toplam 6000) testlerle denetlenir. Kesme yoktur (satır, bağlantı veya zaman damgası yarıda kalmaz): bir sonraki
  sayfaya geçen gün başlığı "(devam)" ile tekrarlanır ve her sayfanın sonunda "➕ Sonraki sayfalarda N etkinlik daha:
  Bugün 3 · Yarın 1" yazar.
- **/gundem ilk sayfası:** iki gün tek sayfaya sığmıyorsa ilk sayfa yine iki günü de gösterir: bugünün ilk etkinlikleri,
  ardından yarının ilk etkinlikleri (her gün en az 3, öteki gün azsa daha fazla; metin sığmazsa daha az, ama en az bir).
  Yarın boşsa "Bu gün için gösterilecek etkinlik yok." satırı ilk sayfadadır. Kalanlar sonraki sayfalarda gün sırasıyla
  "(devam)" başlığıyla gelir; her etkinlik bir kez görünür, gün içi zaman sırası korunur. `/ajanda` 7/14 gün bu kurala
  tabi değildir (günler sırayla akar).
- Düğmeler: ◀ Önceki · Sonraki ▶ · 🔄 Yenile (tek sayfada yalnızca Yenile). Aynı mesajı düzenler; ikinci bir herkese açık
  mesaj oluşmaz. Düğme kimliği yetki taşımaz: yalnızca kartı açan üye basabilir (başkasına özel "kendi komutunu kullan"),
  kart 30 dakika sonra kullanılamaz, aynı kartta 3 saniyelik bekleme vardır (sınırlı bellek). Her tıklama her şeyi yeniden
  hesaplar: saat, günler (gece yarısını geçen Yenile'de başlıklar da), kaynaklar, modül kapıları, LFG erişimi. Kimlikler
  durumsuzdur; yeniden başlatmadan sonra da çalışır.

## Yeni bir kaynak eklemek

Kaynak modül kendi projesinde Core sözleşmesini uygular ve kendisi kaydeder; Ajanda'da merkezi bir switch yoktur:

```csharp
public sealed class PollsAgendaSource(PollsCache cache) : IAgendaSource
{
    public AgendaKind Kind => AgendaKind.Polls;            // Core'daki AgendaKind'e yeni bir değer
    public ModuleId Module => PollsModule.ModuleIdTyped;    // modül kapalıysa Ajanda bu kaynağı hiç okumaz

    public Task<AgendaSourceResult> ReadAsync(AgendaQuery query, CancellationToken ct) =>
        Task.FromResult(new AgendaSourceResult(AgendaSourceState.Ready,
            cache.Upcoming.Where(p => p.StartUtc >= query.FromUtc && p.StartUtc < query.ToUtc)
                .Select(p => new AgendaItem(AgendaKind.Polls, p.Id, p.Title, null, p.StartUtc, null, AgendaStatus.Scheduled))
                .ToList(),
            cache.FetchedAt));
}

// PollsModule.ConfigureServices:
services.AddScoped<IAgendaSource, PollsAgendaSource>();
```

Kurallar: yalnızca modülün zaten tuttuğu veriyi oku; sağlayıcı çağrısı, outbox veya yazma yok; sonuç alanı taşıma;
`FreshAt` gerçek son başarılı veri zamanı olsun; sunucu filtresi varsa modülün kendi kuralını uygula; kanal kitlesine bağlı
içerikte `AudienceChannel` doldur (`AgendaArchitectureTests` okuma/yazma/sonuç kurallarını denetler). Görünüm komutu
eklemek için bir tür (`/ajanda spor:` seçeneği) gerekiyorsa `AgendaOptions` ve çeviriler güncellenir.

## Gizlilik

Ajanda hiçbir şey saklamaz: tablo, dosya, önbellek, sorgu geçmişi veya kullanım profili yoktur. Bellekte yalnızca kart
başına son tıklama anı tutulur (3 saniyelik bekleme için, en fazla 2000 giriş). Loglarda yalnızca kaynak türü ve sunucu
kimliği (kaynak hatasında); LFG başlıkları veya katılımcılar loglanmaz. Kart normal bir Discord mesajıdır ve kanal
geçmişinde kalır.

## Canlı doğrulama listesi (henüz yapılmadı)

- `/ajanda` ve `/gundem` istemcide (masaüstü + mobil) tek kart, zaman damgalarının okuyucu saatinde görünmesi, düğmeler.
- Başka üyenin düğmeye basması (özel uyarı), 30 dakika sonra süre dolması, gece yarısı sonrası Yenile.
- Herkese açık bir kanaldaki LFG ilanının başka kanaldan görünmesi; gizli/rol kısıtlı/thread/NSFW kanal ilanının görünmemesi.
- Gönderilmiş bir F1/CS2/voleybol kartına bağlantının açılması; silinmiş bir kartın bağlanmaması (yalnızca outbox bunu bir
  düzenlemede gördüyse); özel kanaldaki karta bağlanılmaması.
- `/gundem` ilk sayfasının yoğun bir günde iki günü de göstermesi.
