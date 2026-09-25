# Railway'e taşıma — özel test barındırma (PC kapatılabilsin)

Amaç: TSQ Bot'u Windows PC'den Railway'e taşımak; bot **yalnızca test sunucusunda** (618763184815472651) 7/24 çalışsın,
SQLite veritabanı kalıcı bir **Railway Volume**'de dursun. Bu **genel yayın değildir**: depo PRIVATE kalır, global komut
kaydı yok, herkese açık davet yok.

Resmî kaynaklar (okundu 2026-09-25): [Volumes](https://docs.railway.com/reference/volumes) ·
[Using Volumes](https://docs.railway.com/guides/volumes) · [Backups](https://docs.railway.com/reference/backups) ·
[Restart Policy](https://docs.railway.com/deployments/restart-policy) ·
[Config as Code](https://docs.railway.com/config-as-code/reference) ·
[Variables](https://docs.railway.com/reference/variables) · [CLI volume](https://docs.railway.com/cli/volume) ·
[Plans](https://docs.railway.com/reference/pricing/plans).

## Neler hazır (depoda)

| Dosya | Ne yapar |
|---|---|
| `Dockerfile` | SDK 10.0.401 ile derler (`global.json` ile aynı), yalnızca .NET 10 **runtime** imajında çalışır (ASP.NET yok, HTTP portu yok). Secret içermez; veri klasörü `/data` |
| `.dockerignore` | `.git`, `bin/obj`, testler, veritabanları (`*.db`, `-wal`, `-shm`), yedekler, `.env`, arşivler derlemeye gönderilmez |
| Uygulama korumaları | Railway'de volume yoksa veya veritabanı volume dışındaysa **başlamaz**; klasör yazılamıyorsa başlamaz (ipucu: `RAILWAY_RUN_UID=0`); veritabanı bütünlük kontrolü başarısızsa başlamaz (asla silmez/yeniden oluşturmaz); migration'lar Discord ve işçilerden **önce** çalışır |
| **Bekleme modu** (`TOROSQUAD_Bot__Standby=true`) | Yapılandırmayı ve volume'ü doğrular, sonra bekler: Discord'a **bağlanmaz**, işçi çalıştırmaz, veritabanı dosyasını **açmaz** → güvenli ilk kurulum ve veritabanı yükleme |

Railway **Config as Code (`railway.json`) kullanımdan kalkıyor**: panel (2026-09-25) "2026-08-28'den beri Config as Code'u hiç kullanmamış servisler bunu açamaz" diyor → ayarlar **panelden** yapılır, depoda `railway.json` yok.

Railway kuralları (resmî): volume çalışma anında bağlanır (derleme/pre-deploy sırasında değil); volume'ler root ile
bağlanır → imaj root olmayan kullanıcıyla çalıştığı için `RAILWAY_RUN_UID=0` gerekir; volume'lü serviste replika
kullanılamaz; servis başına tek volume.

## 0. Plan ve maliyet (karar sende — otomatik satın alma yok)

- **Free** planda "Always" yeniden başlatma yok (On Failure en fazla 10 kez) ve aylık $1 kredi var. TSQ Bot tahmini
  ~0,15–0,3 GB RAM ve çok düşük CPU kullanır → kaba tahmin **ayda ~$2–4** kaynak kullanımı; bu $1'ı aşar.
- **Hobby** ($5/ay, $5 kullanım dahil): "Always" yeniden başlatma, 5 GB volume. Bu bot için önerilen plan.
- Kullanımı **Workspace → Usage** sayfasından izle; orada kullanım limiti/uyarı ayarla (maliyet sürprizi olmasın).

## 1. Hesap ve depo bağlantısı (tarayıcıda, senin hesabınla)

1. https://railway.com → **Login** → GitHub ile giriş.
2. **New Project → Deploy from GitHub repo** → GitHub izin ekranında **Torokal/TSQ-Bot** deposuna erişim ver
   (depo private kalır; Railway'in GitHub uygulaması yalnızca seçtiğin depoyu okur).
3. Depoyu seç. Railway kökteki `Dockerfile`'ı bulur ve onunla derler. İlk otomatik deploy **başarısız olabilir**
   (henüz volume/değişken yok, bot bilerek başlamaz) — bu beklenen durumdur, Discord'a bağlanmaz.

## 2. Servis ayarları (Service → Settings)

| Ayar | Değer |
|---|---|
| Source → Branch | `feature/railway-deployment` (şimdilik; ileride `main`) |
| Builder | Dockerfile (kökte Dockerfile varsa Railway onu kullanır) |
| Region / Replicas | EU West (Amsterdam) önerisi; **1** replika (volume'lü serviste zaten tek) |
| Networking → Public Networking | **Kapalı** — domain oluşturma (Discord botu HTTP'ye ihtiyaç duymaz) |
| Deploy → Restart Policy | Hobby/Pro: **Always**. Free/Trial: On Failure (en fazla 10) — plan sınırı |
| Serverless | **Kapalı** (uyuyan konteyner Discord bağlantısını koparır) |

## 3. Volume (kalıcı veritabanı)

Proje tuvalinde sağ tık (veya ⌘K) → **Volume** → TSQ Bot servisine bağla → **Mount path: `/data`**.
Tek volume yeter. Veritabanı `/data/torosquad.db` olur (imaj `TOROSQUAD_Bot__DataDirectory=/data` ile gelir).

## 4. Değişkenler (Service → Variables) — değerleri sen girersin, sohbete yazma

.NET `Bölüm:Anahtar` → ortam değişkeni `TOROSQUAD_Bölüm__Anahtar` (önek `TOROSQUAD_`, `:` yerine `__`).

| Değişken | Değer | Secret? |
|---|---|---|
| `TOROSQUAD_Discord__Token` | Discord bot token'ı (Developer Portal) | **Evet** |
| `TOROSQUAD_PandaScore__Token` | PandaScore token'ı | **Evet** |
| `TOROSQUAD_Discord__ApplicationId` | `1552783366963863592` | hayır |
| `TOROSQUAD_Discord__Transport` | `Gateway` | hayır |
| `TOROSQUAD_Discord__TestGuildIds__0` | `618763184815472651` | hayır |
| `TOROSQUAD_Discord__CommandSyncGuildIds__0` | `618763184815472651` | hayır |
| `TOROSQUAD_Delivery__Mode` | `Send` | hayır |
| `TOROSQUAD_Esports__Provider__Mode` | `Live` | hayır |
| `TOROSQUAD_Bot__Standby` | **`true`** (ilk kurulum) → sonra `false` | hayır |
| `RAILWAY_RUN_UID` | `0` (volume root ile bağlanır) | hayır |

İsteğe bağlı: `TOROSQUAD_Esports__Liquipedia__ApiKey` (secret, yoksa HLTV zenginleştirme kapalı kalır),
`TOROSQUAD_Esports__Liquipedia__UserAgent`, `TOROSQUAD_Bot__OperatorContact`. Gerekmeyenler: `DOTNET_ENVIRONMENT`
(imajda `Production`), `TOROSQUAD_Bot__DataDirectory` (imajda `/data`), sağlayıcı adı (varsayılan PandaScore).
**Global komut kaydı açılmaz** (`Discord:AllowGlobalCommandSync` varsayılanı `false`); bot açılışta komut kaydetmez,
test sunucusundaki mevcut guild komutları olduğu gibi kalır.

Değişkenleri kaydedip **Deploy**'a bas. Loglarda şu görünmeli:
`STANDBY: TSQ Bot … — no Discord connection, no workers, database not opened. Database /data/torosquad.db: not present yet`.

## 5. Mevcut test verisini taşı (önerilen: B) veya boş başla (A)

**A) Boş veritabanı:** 6. adıma geç. Sonra Discord'da `/setup` (kanal + modül), `/esports-admin filters team` (Aurora
Gaming, Eternal Fire) ve istersen rol eşlemesini yeniden yap. İlk canlı tarama "baseline" olur, geçmiş duyurulmaz.

**B) Yerel veritabanını taşı (ayarlar, filtreler, bildirim kayıtları korunur):**

1. **Yerel botu düzgün kapat** (çalıştığı pencerede Ctrl+C). `ToroSquad.Bot` süreci kalmadığını doğrula:
   `Get-Process ToroSquad.Bot -ErrorAction SilentlyContinue` boş dönmeli.
2. Tutarlı kopya al (SQLite backup API; WAL'i kendisi toplar) ve bütünlüğünü kontrol et:
   ```powershell
   $env:DOTNET_ENVIRONMENT = 'Development'
   $env:TOROSQUAD_Bot__DataDirectory = "$PWD\data"      # Start-Dev.ps1 ile aynı veritabanı
   dotnet run --project src\ToroSquad.Bot -- db backup --out "$env:USERPROFILE\tsq-handoff"
   dotnet run --project src\ToroSquad.Bot -- db check "$env:USERPROFILE\tsq-handoff\torosquad-<tarih>.db"   # "Integrity OK" olmalı
   ```
   Çıkan dosya tek `.db` dosyasıdır; `-wal`/`-shm` **yüklenmez**.
3. Railway CLI (bir kez): `npm i -g @railway/cli` → `railway login` (tarayıcıda onay) → depo klasöründe `railway link`
   (projeyi ve TSQ Bot servisini seç). **Not (2026-09-25):** `railway volume files …` SSH kullanır; bilgisayarda bir SSH
   anahtarı olmalı ve Railway hesabına kayıtlı olmalı (`railway ssh keys add`). Anahtar yoksa A yolunu (boş veritabanı)
   seç — ilk kurulumda böyle yapıldı.
4. Servis **bekleme modundayken** yükle ve doğrula:
   ```powershell
   railway volume files upload "$env:USERPROFILE\tsq-handoff\torosquad-<tarih>.db" /torosquad.db --overwrite
   railway volume files list /
   ```
   `torosquad.db` görünmeli (volume kökü = `/data`).

## 6. Botu Railway'de başlat (yerel bot KAPALIYKEN)

1. Yerel bot kapalı olmalı (iki kopya aynı anda Discord'a bağlanırsa etkileşimler/bildirimler ikiye katlanır).
2. `TOROSQUAD_Bot__Standby` → `false` (veya sil) → Deploy.
3. Loglarda sırayla: `TSQ Bot <sürüm> (commit <sha>); environment Production; database /data/torosquad.db; Discord
   transport Gateway, test guilds 618763184815472651, global commands allowed False; match provider PandaScore (Live);
   delivery Send; host Railway` → `Database ready (migrations applied)` → `[Gateway] Ready` → `Esports poll: … filtered=…`.

## 7. Doğrulama

- Discord'da `/bot status` (commit Railway'deki SHA olmalı), `/esports-admin filters show`, `/esports-admin preview`.
- **Tek kopya:** yerel `ToroSquad.Bot` süreci yok; Railway'de tek deployment "Active".
- **Yeniden başlatma testi:** Service → ⋮ → **Restart** → bot yeniden bağlanır; ayarlar/filtreler duruyor; yeni kopya
  mesaj yok (loglarda `new=0`).
- **PC'yi kapat:** bot çalışmaya devam etmeli (Discord'da komut yanıtlıyor, Railway loglarında taramalar sürüyor).
  SplitWire/VPN artık bot için gerekmez (Railway sunucuları Discord'a doğrudan erişir).

## 8. Yedekler

**Plan sınırı (2026-09-25, panelde görüldü):** yedek oluşturma ve zamanlama yalnızca **Pro** planda var; Hobby'de Backups
sekmesi yalnızca mevcut yedeklerin geri yüklenmesine izin verir. Pro'da: Service → **Backups**: **Daily** zamanlaması
(6 gün saklanır). Ücret: artımlı + copy-on-write, **artımlı volume depolaması** olarak (volume birim fiyatı) faturalanır. Ayrıca elle yedek:
veritabanı taşımadan, büyük sürümden, sağlayıcı şeması değişikliğinden ve bakım işlemlerinden **önce**. Geri yükleme:
Backups → ilgili tarih → **Restore** → değişiklikleri incele → **Deploy**.

İndirmeli yerel kopya (isteğe bağlı): `railway volume files download /torosquad.db .\torosquad-railway.db` —
yalnızca servis **bekleme modundayken** (dosya açıkken indirilen kopya tutarsız olabilir).

## 9. Geri dönüş (rollback)

1. Railway servisini durdur (Deployments → aktif deployment → **Remove**, veya `Standby=true` ile yeniden deploy) ve
   loglarda Discord bağlantısının kapandığını gör.
2. Veritabanı sorunluysa önceki volume yedeğini geri yükle (8. adım).
3. Gerekirse bilinen iyi sürümü yerelde başlat (`.\scripts\Start-Dev.ps1`) — **Railway'deki kopya kapandıktan sonra**.
4. Kesintiyi asla iki kopyayı birden çalıştırarak "çözme".

## 10. Sorun giderme

| Log | Anlamı / çözüm |
|---|---|
| `STORAGE: … WITHOUT a volume` | Volume yok → 3. adım |
| `STORAGE: … outside the Railway volume` | `TOROSQUAD_Bot__DataDirectory` volume yoluna eşit olmalı (`/data`) |
| `STORAGE: … not writable … RAILWAY_RUN_UID=0` | Değişkeni ekle |
| `STORAGE: integrity check failed` | Veritabanı bozuk; hiçbir şey değiştirilmedi → yedeği geri yükle |
| `CONFIG: …` | Eksik/yanlış değişken (değer loglanmaz) → 4. adım |
| `STANDBY: …` | Bekleme modu açık → `TOROSQUAD_Bot__Standby=false` |

Loglara asla token/anahtar/Authorization başlığı yazılmaz (bilinen biçimler ayrıca maskelenir); tam ortam dökümü
yapılmaz.
