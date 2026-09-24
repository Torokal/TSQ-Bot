# TSQ Bot — PROJECT_STATE

> Tek doğruluk kaynağı: gerçek durum, kararlar, çalıştırılan testler, blocker'lar ve tek NEXT ACTION.
> Yeni oturumda önce bu dosyayı, sonra `git status` / `git log --oneline -10` çıktısını doğrula.

Son güncelleme: **2026-09-24** — Foundation **main'e merge edildi** (PR #1, merge `27e7ab3`); depo PRIVATE (geliştirme);
Discord test-guild doğrulamasına hazır, ancak Discord uygulaması/token/test guild henüz yok (BLOCKED).

## Ürün kimliği ve depo

| | |
|---|---|
| Ürün adı | **TSQ Bot** (eski adı ToroSquad Bot — 2026-09-24'te değiştirildi) |
| Kanonik depo | `Torokal/TSQ-Bot` → https://github.com/Torokal/TSQ-Bot (**PRIVATE** geliştirme/test süresince — sahip kararı, 2026-09-24; bot herkese açılmadan önce **public** yapılacak: PRE-RELEASE REQUIREMENT) |
| Rename | **Tamamlandı** (yerel) — commit `671e7cc` `refactor(branding): rename product to TSQ Bot` |
| GitHub push | Depo **oluşturuldu** (Torokal hesabı, 2026-09-24; önce public, ardından sahip isteğiyle **private**). `main` (`177b5f7`) ve `feature/foundation` (`4912596`) **push edildi**, force/squash yok. İlk `feature/foundation` push'u GitHub push protection'a takıldı: `6c4b696`/`5570842` içindeki `tests/ToroSquad.Tests/Integration/OperationsTests.cs:86` **sahte** test dizesi "Discord Bot Token" sanıldı; sahip GitHub'da "used in tests" izni verdi. Geçmiş yeniden yazılmadı; `671e7cc` dizeyi çalışma anında birleştiriyor |
| GitHub'daki dallar | `main`, `feature/foundation` |
| Pull request | https://github.com/Torokal/TSQ-Bot/pull/1 — "Foundation: modular TSQ Bot core and esports module"; **MERGED** 2026-09-24T20:42:13Z, normal merge commit (squash/force/rebase yok). Merge öncesi inceleme: 10 commit, 159 dosya (158 eklenen + 1 yeniden adlandırılan), binary/veritabanı/runtime/`bin`/`obj`/`TestResults`/arşiv yok, makine yolu yok; tüm geçmişte secret taraması: yalnızca bilinen sahte test dizesi. GitHub PR diff'i 20.000 satır sınırını aştığı için inceleme aynı SHA üzerinde yerel `git diff` ile yapıldı. CI yok |
| Merge SHA | `27e7ab3290ca3dc739fe3d42b21a5128e209ea04` (ebeveynler `177b5f7` + `bd52f74`); `main` ağacı = `feature/foundation` ağacı |
| Güncel `main` | merge commit + bu durum kaydı commit'i (yalnızca bu dosya, doğrudan `main`'e, fast-forward push). `feature/foundation` dalı ve geçmişi GitHub'da korunuyor |
| Discord uygulama adı | "TSQ Bot" olarak varsayılır — **BLOCKED**: Developer Portal'da uygulama henüz oluşturulmadı/yapılandırılmadı |

Adlandırma kuralı: kullanıcıya görünen ad yalnızca `ProductInfo.ProductName` sabitinden gelir (localization'da `{product}`
belirteci). Bilerek **değiştirilmeyen** teknik kimlikler (fayda yok, kırılma riski var): `ToroSquad.*` proje/namespace/
assembly adları ve klasörleri, `TOROSQUAD_` ortam değişkeni öneki (mevcut yapılandırmaları bozmamak için), `torosquad.db` /
`torosquad.instance.lock` veri dosyaları (mevcut veriyi korumak için), user-secrets kimliği `torosquad-bot-local-dev`
(kayıtlı secret'lar kaybolmasın), `torosquad-command-manifest/v1` biçim kimliği, test/trx geçici dosya adları.
Eski adın kalan eşleşmeleri yalnızca tarihsel notlar ve provenance başlıklarıdır ("formerly ToroSquad Bot").

## Aşamalar

| Aşama | Kapsam | Durum |
|---|---|---|
| A | Ortam, upstream, lisans, sağlayıcı erişimi incelemesi | **Tamam** — docs/PROVENANCE.md, docs/PROVIDERS.md |
| B | Core, modül registry, ayarlar, slash manifest, fake transport | **Tamam** (TESTED_OFFLINE) |
| C | Liquipedia/Valve adaptörleri, fixture contract testleri, sorgu komutları | **Tamam** (TESTED_OFFLINE); canlı sözleşme testi BLOCKED |
| D | Filtreler, abonelikler, güvenli roller, outbox | **Tamam** (TESTED_OFFLINE) |
| E | Kurulum UX, privacy, scriptler, recovery testleri | **Tamam** (TESTED_OFFLINE) |
| F | Canlı provider + test guild doğrulaması, sürüm hazırlığı | **BLOCKED** — token/test guild ve onaylı Liquipedia anahtarı yok |

## Özellik durumu

Etiketler: IMPLEMENTED (kod var) · TESTED_OFFLINE (otomatik testle yerelde kanıtlı) · VERIFIED_LIVE (gerçek Discord /
gerçek API'de doğrulandı) · BLOCKED · DEFERRED.

| Özellik | Durum |
|---|---|
| Modüler çekirdek (`IToroModule`, registry, kapı, yaşam döngüsü, sağlık, gizlilik/kurulum sözleşmeleri) | TESTED_OFFLINE |
| Gerçek slash komutları: /help, /bot, /privacy, /setup, /modules, /esports, /esports-admin (manifest + doğrulama) | TESTED_OFFLINE — Discord seçicisinde görünme **BLOCKED** |
| Komut senkronu (dry-run varsayılan, allow-list, uygulama kimliği, prune yalnızca açık bayrakla, global ayrı kapı) | TESTED_OFFLINE (sahte kaydedici); gerçek Discord'a kayıt **BLOCKED** |
| Sunucu tarafı yetkilendirme (slash + component + modal ortak servis yolu), guild izolasyonu | TESTED_OFFLINE |
| Liquipedia istemcisi/parser (sonuç tipleri, sayfalama, kota, retry, UTC) | TESTED_OFFLINE (sentetik fixture); canlı **BLOCKED** (onaylı API anahtarı yok, ücret/başvuru kararı sahibin) |
| Valve VRS istemcisi/parser, takım eşleştirme (kesin/alias/normalize/belirsiz) | TESTED_OFFLINE; canlı fetch **NOT_RUN** |
| Planlayıcı (baseline, watermark, catch-up, bayat veri, dedup, düzeltme→düzenleme) | TESTED_OFFLINE |
| Kalıcı outbox (InFlight-önce-commit, belirsiz teslimat uzlaştırması, 429/403/404, pause/kapı kontrolü) | TESTED_OFFLINE |
| Güvenli rol eşleştirme + self-service + panel | TESTED_OFFLINE (servis katmanı); gerçek rol verme **BLOCKED** |
| Spoiler, allowed_mentions, mention enjeksiyonu, URL allow-list | TESTED_OFFLINE |
| /privacy export/delete, saklama süresi, ayrılan guild temizliği | TESTED_OFFLINE |
| Kaynak/lisans (/bot about, /bot source, Export-Source.ps1, commit gömme) | TESTED_OFFLINE; `Bot:SourceUrl` varsayılanı https://github.com/Torokal/TSQ-Bot (depo geliştirme süresince private → bağlantı şimdilik yalnızca yetkili GitHub kullanıcılarına açılır; bu beklenen durum. Lansmandan önce public: PRE-RELEASE REQUIREMENT) |
| Türkçe varsayılan / İngilizce fallback, Europe/Istanbul (Windows'ta test edildi) | TESTED_OFFLINE |
| PowerShell scriptleri: Doctor, Start-Dev (+Simulate), Test, Sync-Commands, Export-Source | Hepsi **çalıştırıldı** (PS 5.1); Sync yalnızca BLOCKED (token yok) yolunda |
| Gateway bağlantısı (Discord.Net), gerçek interaction işleme | IMPLEMENTED, **BLOCKED** (token yok) |
| Haber bildirimleri, eski Greg "yıldız puanı" | DEFERRED (doğrulanmış kaynak yok; VRS yeniden adlandırılmaz) |
| Canlı maç durumu bildirimi | DEFERRED (Liquipedia doğrulanmış canlı durum sunmuyor) |
| DM komutları/bildirimleri | Kapsam dışı (şartname) |
| Docker dağıtımı, 7/24 barındırma | DEFERRED (seçilmedi, doğrulanmadı) |

## Ortam (2026-09-24 doğrulandı)

- Windows 11 Pro 10.0.26200, Windows PowerShell 5.1, git 2.55.0.
- .NET SDK başlangıçta **yoktu**; kullanıcı onayıyla `winget install Microsoft.DotNet.SDK.10 --version 10.0.401`
  (runtime 10.0.12, 2026-09-08, LTS, EOL 2028-11-14). `global.json`: 10.0.401 / latestPatch.
- `dotnet-ef` 10.0.12 repo-yerel araç (`.config/dotnet-tools.json`), global kurulum yok.
- `gh` 2.101.0 `C:\Program Files\GitHub CLI` altında (PATH'te değil), sahip **Torokal** hesabıyla giriş yaptı (keyring); git push, kalıcı ayar değiştirmeden tek seferlik `-c credential.helper` ile yapıldı. Codex CLI mevcut; **kullanıcı kararıyla kullanılmadı**. Bağımsız okuma-yalnız inceleme için bir Claude
  alt ajanı kullanıldı (dosya yazmadı).
- NuGet: bir kez NuGet istemcisinin indirme bağlantısı takıldı (ağ hızlıydı); süreç durdurulup restore yeniden yapıldı.
- Git: `main` = yalnızca şartname (`177b5f7`); çalışma dalı `feature/foundation`. Remote `origin` = https://github.com/Torokal/TSQ-Bot.git.

## Kararlar (ayrıntı: docs/adr/)

1. Modüler monolit, .NET 10 + Discord.Net 3.20.1 + EF Core 10/SQLite, tek instance (ADR-0001).
2. Sağlayıcı sonuç tipleri; canlı durum iddiası yok; fixture yalnızca Fixture modunda bağlı (ADR-0002).
3. **AGPL-3.0-only** (upstream'den uyarlanmış iki dosya nedeniyle); Corresponding Source zorunlu (ADR-0003).
4. Outbox semantiği, exactly-once iddiası yok (ADR-0004).
5. Komut manifesti çevrimdışı; kayıt yalnızca açık CLI ile, Ready'de asla (ADR-0005).
6. Upstream HEAD `3898b4e` (2026-09-23) = şartnamedeki SHA; V2 deposu bot değil, API iskeleti. Policies deposu lisanssız,
   kullanılmadı.
7. Hatırlatma uygunluğu: maç başlangıcı ≥ watermark (etkinleştirmeden hemen sonra başlayan maç kaçmasın).

## Çalıştırılan testler (gerçek çıktılar)

- **Merge öncesi son doğrulama (2026-09-24, `bd52f74` = merge edilen içerik)** — `.\scripts\Test.ps1 -Repeat 3`: build
  **0 uyarı / 0 hata**, format temiz, manifest geçerli (7 komut, hash `965cae891cfc…`), **195/195 ×3 geçti**; `git diff
  --check` temiz; `Doctor.ps1`: token/uygulama/guild listesi/Liquipedia anahtarı BLOCKED, geri kalanı OK; `Export-Source.ps1`:
  `tsq-bot-source-bd52f74f33c8.zip`, 212 dosya, veritabanı/secret/build çıktısı yok.
- **Rename sonrası doğrulama (2026-09-24, commit `671e7cc`)** — `.\scripts\Test.ps1 -Repeat 3`: build **0 uyarı / 0 hata**,
  format temiz, manifest geçerli (7 komut, yeni hash `965cae891cfc…` — yalnızca açıklama metinleri değişti, komut/alt
  komut/seçenek adları aynı), testler **195/195 ×3 geçti** (+2 yeni: marka metinleri, kanonik kaynak URL'si; export testine
  ürün alanı eklendi). `Doctor.ps1`: canlı eksikler BLOCKED, `Bot:SourceUrl` OK. `simulate`: başlık "TSQ Bot", 1 hatırlatma,
  ikinci poll kopya yok, spoiler satırı skor sızdırmıyor. `Export-Source.ps1`: `tsq-bot-source-671e7cc343cb.zip`,
  212 dosya, veritabanı/secret/bin/obj yok. Depo taraması: izlenen dosyalarda secret, `*.db`, `bin/obj`, `.env` yok.
- **Son doğrulama** — `.\scripts\Test.ps1 -Repeat 3` (PS 5.1, inceleme düzeltmelerinden sonra): Release build **0 uyarı /
  0 hata**, `dotnet format --verify-no-changes` temiz, manifest geçerli (7 komut, hash `63a5f52b0827…`), testler
  **193/193 ×3 geçti**. Önceki tur (düzeltmelerden önce): 181/181 ×3 + 14 ek tam koşu.
- İlk koşulardan birinde tek seferlik `DbUpdateException` (kök neden bilinmiyor, bkz. docs/TESTING.md "Bilinen gözlem");
  daha sonra yeniden üretilemedi.
- **Bağımsız kod incelemesi** (salt-okunur Claude alt ajanı): 1 yüksek, 8 orta, 5 düşük bulgu; **hepsi doğrulandı ve
  düzeltildi**, 11 regresyon testi eklendi (kanal değişiminde yeniden duyuru, spoiler genişlik sızıntısı, önceden sahip olunan
  rolün kaldırılması, bahsedilemez rol ping'i, sınırsız yeniden gönderim, retry'ların kotayı aşması, catch-up'ın bir sonraki
  poll'da tamamlanması, sağlayıcı metninde tıklanabilir link, pause'da düşen düzeltme, aynı adlı başka komutun prune'u,
  salt-okuma yönetici görünümlerinde yetki). Planlayıcı-dispatcher çakışmasında yeniden hesaplama **uygulandı ama doğrudan
  testi yok** (deterministik yeniden üretimi zor).
- Elle çalıştırılanlar: `Doctor.ps1` (canlı eksikleri BLOCKED gösterdi), `Start-Dev.ps1` 35 sn duman testi (Fake transport,
  8 komut çevrimdışı doğrulandı, ilk poll baseline), `Start-Dev.ps1 -Simulate` eşdeğeri `simulate` (1 hatırlatma sahte
  transport'a gitti, ikinci poll kopya üretmedi, 5 bitmiş maç baseline, spoiler render'ı skor sızdırmadı),
  `Sync-Commands.ps1 -GuildId …` (token yok → "BLOCKED … Nothing was changed"), `Export-Source.ps1` (212 dosya; veritabanı
  ve secret yok). Simulate inceleme düzeltmelerinden sonra yeniden koşuldu: spoiler satırı tek, harita satırı yok.
- **Hiçbir canlı Discord veya canlı Liquipedia/Valve çağrısı yapılmadı.**

## Blocker'lar (sahibin eylemi gerekir)

| # | Blocker | Gereken eylem | Onay kapısı |
|---|---|---|---|
| B1 | Discord uygulaması / bot token yok | Developer Portal'da uygulama oluştur, token'ı user-secrets'a koy (docs/WINDOWS_SETUP.md §3–4) | Hesap/yetkilendirme — sahip |
| B2 | Test guild ve davet | Botu test sunucusuna davet et (izin 84992 / 268520448), `Discord:TestGuildIds` + `CommandSyncGuildIds` | Bot daveti — sahip |
| B3 | Liquipedia API erişimi | Başvuru/plan seçimi (ücretli olabilir; ücretsiz yalnızca açık kaynak + ticari olmayan, onaylı) | Ücret/abonelik — sahip |
| B5 | Upstream geliştiriciye mesaj | docs/drafts/upstream-contact.md taslağı gönderilmedi | Dış iletişim — sahip |

## Yetenek bazlı durum (Discord canlı doğrulama matrisi)

Canlı hiçbir şey gözlenmedi. Bir işlemin başarılı olması tüm alt sistemi VERIFIED_LIVE yapmaz.

| Yetenek | Durum |
|---|---|
| Discord gateway bağlantısı, yeniden bağlanma, log'da token olmaması | BLOCKED (token yok) — log redaction TESTED_OFFLINE |
| Guild slash komut kaydı + `/` seçicisinde görünme | BLOCKED — kayıt planı/önizleme TESTED_OFFLINE |
| `/help`, `/bot status|about|source`, `/modules list` | BLOCKED — TESTED_OFFLINE |
| `/privacy export|delete` | BLOCKED — TESTED_OFFLINE |
| `/setup` akışı | BLOCKED — TESTED_OFFLINE |
| `/modules enable|disable` (guild kapsamı) | BLOCKED — TESTED_OFFLINE |
| `/esports …` Discord etkileşimi | BLOCKED — TESTED_OFFLINE |
| Liquipedia canlı veri (komutlar, autocomplete verisi, bildirimler) | BLOCKED (onaylı API anahtarı yok) — fixture ile TESTED_OFFLINE |
| Yönetici yetki ayrımı (sunucu tarafı) | BLOCKED — TESTED_OFFLINE |
| Autocomplete etkileşimi | BLOCKED — TESTED_OFFLINE |
| Rol paneli / self-service rol verme-alma | BLOCKED — TESTED_OFFLINE |
| TEST/DEMO bildirimi (kanal, format, kopya yok, spoiler, ping yok) | BLOCKED — TESTED_OFFLINE (simulate) |
| Yeniden başlatma sonrası kalıcılık | BLOCKED — TESTED_OFFLINE |
| Guild'ler arası izolasyon | TESTED_OFFLINE (ikinci gerçek guild yoksa öyle kalır) |
| Bildirim çökme kurtarma, 429, yarış durumları | TESTED_OFFLINE (canlıda yıkıcı test yapılmaz) |
| VRS canlı veri (Valve deposu) | NOT_RUN — TESTED_OFFLINE; ağ erişimiyle ayrıca doğrulanabilir |
| Global komut kaydı, herkese açık bot | DEFERRED |
| GitHub deposunun public olması | PRE-RELEASE REQUIREMENT |

Discord yapılandırması (koddan doğrulandı): gateway intent yalnızca **Guilds** (ayrıcalıklı intent yok); zorunlu kanal
izinleri View Channel + Send Messages + Embed Links, isteğe bağlı Read Message History (uzlaştırma) ve Manage Roles
(self-service); davet tamsayıları 84992 = 1024+2048+16384+65536 ve 268520448 = 84992+268435456 bu listeyle birebir
eşleşiyor. Administrator istenmez; Mention Everyone önerilmez (rolü "bahsedilebilir" yapın).

## Yayın öncesi gereksinimler (PRE-RELEASE REQUIREMENT)

Bunlar yerel geliştirme/test için blocker **değildir**; yalnızca bot herkese açılmadan önce ve sahip açıkça yayın
aşamasına geçtiğinde yapılır. Ayrıntılı kontrol listesi: docs/OPERATIONS.md → "Yayın öncesi güvenlik kapısı".

| Gereksinim | Durum |
|---|---|
| `Torokal/TSQ-Bot` deposunu public yapmak (`/bot source` URL'si zaten nihai adres) | PRE-RELEASE REQUIREMENT — yapılmadı, yetkilendirilmedi |
| Tüm git geçmişinde secret taraması (bilinen sahte test token'ı hariç) | PRE-RELEASE REQUIREMENT — yayın anında tekrar |
| İzlenen dosya + kaynak arşivi denetimi, lisans/provenance, README kamu incelemesi | PRE-RELEASE REQUIREMENT |
| Global komut kaydı, herkese açık bot | DEFERRED (yayın aşaması) |

## NEXT ACTION

**Sahip (B1+B2):**
1. Discord Developer Portal'da **"TSQ Bot"** uygulamasını oluştur (Bot sekmesinde ayrıcalıklı intent'leri açma).
2. Token ve uygulama kimliğini **sohbete yazmadan**, kendi terminalinde kaydet:
   `dotnet user-secrets set "Discord:Token" "<token>" --project src\ToroSquad.Bot`
   `dotnet user-secrets set "Discord:ApplicationId" "<id>" --project src\ToroSquad.Bot`
3. Botu yalnızca bir **test sunucusuna** davet et (`bot applications.commands`, izin 84992; self-service rol testleri için
   268520448). Test için zararsız, izinsiz ayrı roller oluştur; bot rolünü bu rollerin üstüne koy.
4. Bu oturuma test guild ID'sini ve "**test sunucusunda guild komut kaydı + TEST/DEMO bildirimi**" onayını ver.

**Onaydan sonra ajan:** token'ın doğru uygulamaya ait olduğunu doğrular → `Sync-Commands.ps1 -GuildId <id>` önizleme
(oluşturulacak/güncellenecek/silinecek; TSQ Bot'a ait olmayan komutlara dokunulmaz) → temizse `-Apply` (yalnızca guild,
global yok) → yukarıdaki matrisi madde madde canlı doğrular → sonuçları bu dosyaya yazar. Liquipedia anahtarı yoksa veri
kısmı BLOCKED kalır.
