# TSQ Bot — PROJECT_STATE

> Tek doğruluk kaynağı: gerçek durum, kararlar, çalıştırılan testler, blocker'lar ve tek NEXT ACTION.
> Yeni oturumda önce bu dosyayı, sonra `git status` / `git log --oneline -10` çıktısını doğrula.

Son güncelleme: **2026-09-25** — Foundation main'de (PR #1). Canlı doğrulama PR #2'de (açık, merge edilmedi).
**PandaScore + yaşam döngüsü + sade kartlar** `feature/pandascore-notifications` dalında (PR #2 üzerine yığılı);
çevrimdışı **343/343 ×3**. Kart UI temizliği (başlıkta önek yok, TEST/DEMO yalnızca footer'da, footer'da ref yok) uygulandı. PandaScore canlı: BLOCKED (token yok). Liquipedia: **BLOCKED/OPTIONAL** (anahtar yok; başvuru
yayın aşamasında depo public olduktan sonra) — bot ona bağlı değil, `Esports:VerifiedMatchLinks` elle yedek.

## PandaScore / bildirim aşaması (2026-09-25)

| Konu | Durum |
|---|---|
| Varsayılan maç sağlayıcısı | **PandaScore** (`Esports:Provider:Name`), Liquipedia eski/isteğe bağlı; normal çalışma Liquipedia gerektirmez |
| PandaScore resmî doküman doğrulaması | Yapıldı (2026-09-25): uçlar, Bearer auth, 1.000 istek/saat (ücretsiz), sayfalama ≤100, durumlar, rescheduled/forfeit, "Source: PandaScore" atfı (docs/PROVIDERS.md) |
| PandaScore ayrıştırma, yaşam döngüsü, sayfalama, hata türleri, bütçe | TESTED_OFFLINE (sentetik) |
| PandaScore gerçek API (yaklaşan/oynanan/biten/ertelenen/yeniden planlanan/iptal) | **BLOCKED** — token yok; ücretsiz planda sonuç alanları dolu mu: NOT_VERIFIED (resmî sayfalar çelişkili) |
| Başladı / bitti / ertelendi / saat değişti / iptal / hükmen kartları | TESTED_OFFLINE; Discord'da görünüm: demo kartlarıyla doğrulanacak |
| Sade kart tasarımı (Greg referansı) | Uygulandı; Greg ekran görüntüsü bu turda paylaşılmadı → metin şablonuna göre |
| HLTV | Veri sağlayıcısı değil, **kazıma yok**. Araştırma: HLTV'nin resmî API'si yok; BOT Greg'in "Matchpage" bağlantısı Liquipedia maç verisindeki `links.hltv`'den geliyordu (Liquipedia Lua-Modules + upstream kodu). Sahip kararı (2026-09-25): veri PandaScore, HLTV bağlantısı Liquipedia'dan (aynı iki takım + ≤90 dk + tek aday). TESTED_OFFLINE; canlı **BLOCKED/OPTIONAL** (Liquipedia anahtarı yok); elle yedek `Esports:VerifiedMatchLinks` çalışır (TESTED_OFFLINE) |
| Kart başlığı tıklanınca maç sayfası | Uygulandı (Greg gibi); gerçek HLTV bağlantısı Liquipedia anahtarı gelince görünür; demo kartları bilinçli olarak bağlantısız |
| Yıldız (BOT Greg puanı) | DEFERRED — güvenilir kaynak yok, gösterilmez |
| Test guild'deki rol eşleme #2 (tüm maçlar, hatırlatma ping'i açık) | Sahibin yapılandırması, dokunulmadı; demo hatırlatması test rolünü etiketleyebilir → `/esports-admin roles unmap mapping:2` |

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
| Discord uygulaması | **"TSQ Bot"**, Application ID `1552783366963863592` (token user-secrets'ta; değer hiçbir çıktıda gösterilmedi) |
| Test guild | `618763184815472651` (TestGuildIds + CommandSyncGuildIds; yerel user-secrets). Global kayıt **yok** |
| Ağ | Türkiye'de Discord erişimi ISS düzeyinde engelli; SplitWire-Turkey (WireSock/WARP) `AllowedApps` listesine sahip onayıyla `dotnet.exe`, `ToroSquad.Bot.exe` eklendi (yedek: `wgcf-profile.conf.20260925-002819.bak`). Token'sız doğrulama: discord.com:443, gateway.discord.gg:443, `/api/v10/gateway` 200 |

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
| B3 | Liquipedia API erişimi (isteğe bağlı) | Basic/Premium geçici olarak kullanılamıyor, ticari: Enterprise; ücretsiz erişim başvuruyla (açık kaynak / ticari olmayan / topluluk, çoğu zaman süreli). **Sahip kararı:** depo public olduktan sonra, yayın aşamasında başvurulacak | Başvuru/ücret — sahip |
| B7 | Discord ağ erişimi | **Çözüldü** (2026-09-25) — SplitWire AllowedApps, sahip onayıyla | — |
| B5 | Upstream geliştiriciye mesaj | docs/drafts/upstream-contact.md taslağı gönderilmedi | Dış iletişim — sahip |

## Yetenek bazlı durum (Discord canlı doğrulama matrisi — 2026-09-25, test guild)

Bir işlemin başarılı olması tüm alt sistemi VERIFIED_LIVE yapmaz. Ortam: Development, Discord transport Gateway, gönderim
Send, sağlayıcı Fixture (TEST/DEMO), Example modülü kapalı (7 komut grubu).

| Yetenek | Durum |
|---|---|
| Token/Application ID eşleşmesi (senkron aracı Discord'dan uygulama kimliğini okur) | **VERIFIED_LIVE** |
| Discord gateway bağlantısı (Connected → Ready, "TSQ Bot", 1 guild) | **VERIFIED_LIVE** (6 başlatma) |
| Log'da token yok (token değeri ve gizli parçası log'da 0 kez) | **VERIFIED_LIVE** |
| Guild komut kaydı: önizleme → yalnızca 7 Create → uygula → tekrar önizleme "Nothing to change" | **VERIFIED_LIVE** (global yok, başka komuta dokunulmadı) |
| Komutların slash ile çağrılması (/bot, /modules, /esports, /esports-admin, /privacy, /setup) | **VERIFIED_LIVE** |
| `/bot status`, `/bot about` (TSQ Bot, sürüm+commit, AGPL, "resmî devamı değildir"), `/bot source` (URL + çalışan commit) | **VERIFIED_LIVE** |
| `/help` | **VERIFIED_LIVE** (sahip bildirimi) |
| `/modules list`; esports'u `/setup` ile etkinleştirme | **VERIFIED_LIVE** |
| `/setup` esports adımı: kanal seçimi kalıcı, sihirbaz mesajı güncelleniyor (düzeltme sonrası) | **VERIFIED_LIVE** |
| `/esports-admin configure reminder_minutes`, `/esports-admin doctor` | **VERIFIED_LIVE** |
| `/esports matches`, `/esports rankings`, `follow` / `subscriptions` / `unfollow` | **VERIFIED_LIVE** (etkileşim; veri TEST/DEMO) |
| `/privacy export` (JSON eki, "TSQ Bot", yalnızca çağıran); `/privacy delete` önizleme + onay + silme (DB'de takip/tercih 0) | **VERIFIED_LIVE** |
| TEST/DEMO bildirimi: doğru kanal, [TEST/DEMO] etiketi, ping yok, Türkçe, tek mesaj | **VERIFIED_LIVE** (mesaj `1552805032611815436`; eski düzen — 2026-09-25'ten beri TEST/DEMO yalnızca footer'da) |
| Eski render (başlıkta [TEST/DEMO], footer'da ref) Rescheduled TEST/DEMO kartının görünümü | VERIFIED_LIVE **yalnızca o eski render için** (sahip canlı gördü, 2026-09-25). Görünür yapı sonradan değişti (önek kaldırıldı, footer sadeleşti, ref görünür içerikten çıktı, ref yerine parmak izi uzlaştırması) → **güncel render için geçerli değildir** |
| Discord mesaj düzenleme/güncelleme teslimatı | **VERIFIED_LIVE** (2026-09-25: 7 demo kartı yeni render'a düzenlendi; Discord düzenlemeyi kabul etti — outbox Sent, EditPending=0, DeliveredPayloadHash=PayloadHash, hata yok) |
| Mevcut demo kartları kopya üretmeden güncellendi | **VERIFIED_LIVE** (7 satır EditScheduled → aynı mesajlar düzenlendi; yeni outbox satırı/mesaj yok) |
| Güncelleme rol/kullanıcı ping'i olmadan yapıldı | **VERIFIED_LIVE** (düzenlemeler her zaman allowed_mentions boş; demo kartların içeriği yok) |
| **Güncel** Started kartı görünümü (🔴 sürümü) | **VERIFIED_LIVE** (sahip 🔴 sürümünü Discord'da görüp onayladı, 2026-09-25; "🔴 Maç başladı · <Discord yerel göreli zaman>") |
| Started kartı görünümü (▶️ sürümü, düzen aynı) | **VERIFIED_LIVE** (sahip güncel kartı inceleyip onayladı, 2026-09-25; Discord'un yerel göreli zamanı `<t:…:R>` dahil — "43 minutes ago" gibi metni Discord kullanıcının diline/saat dilimine göre üretir, TSQ Bot çevirmez) |
| **Güncel** Finished (normal) kartı görünümü | **VERIFIED_LIVE** (sahip onayı, 2026-09-25) |
| **Güncel** Finished (spoiler) kartı görünümü | **VERIFIED_LIVE** (sahip onayı, 2026-09-25) |
| **Güncel** Postponed kartı görünümü | **VERIFIED_LIVE** (sahip onayı, 2026-09-25) |
| **Güncel** Rescheduled kartı görünümü | **VERIFIED_LIVE** (sahip onayı, 2026-09-25; güncel render) |
| **Güncel** Canceled kartı görünümü | **VERIFIED_LIVE** (sahip onayı, 2026-09-25) |
| **Güncel** Forfeit kartı görünümü | **VERIFIED_LIVE** (sahip tam kartı inceleyip onayladı, 2026-09-25) |
| Maç Sayfası bağlantısının görünümü | **VERIFIED_LIVE** (sahip onayı, 2026-09-25; tıklanabilir başlık + en altta "Maç Sayfası"). Kontrollü test: demo hükmen kartı RFC 2606 ayrılmış test alanına (`https://example.com/tsq-bot-demo-match-page`) bağlanır; HLTV değil, sahte üretim bağlantısı değil, hiçbir şey indirilmez |
| Zaman gösterimi | Kart göreli zamanı ve embed zaman damgası Discord'un yerel biçimlendirmesi (kullanıcının dili/saat dilimi); elle çeviri yok. "Yeni Saat" alanı onaylı mevcut biçim (sunucu saat dilimi, dd/MM/yyyy HH:mm) |
| Parmak izi tabanlı belirsiz-gönderim uzlaştırması | TESTED_OFFLINE (canlı timeout tetiklenemez) |
| Eski footer ref'i ile uzlaştırma (legacy) | TESTED_OFFLINE |
| Aynı çalışmada tekrar taramada kopya yok (`new=0`) | **VERIFIED_LIVE** |
| Yeniden başlatma: yeni mesaj yok, mevcut mesaj ping'siz düzenlendi (`updated=1`, tek outbox satırı) | **VERIFIED_LIVE** |
| Ayar/modül durumu yeniden başlatmada korunur | **VERIFIED_LIVE** |
| Takım autocomplete (`/esports follow team:`) | **VERIFIED_LIVE** (sahip bildirimi) |
| Eşleme autocomplete (`roles unmap/selfservice mapping:`) | **HATALI** — Discord "Loading options failed"; log yoktu. `148523b` hatayı loglar ve boş liste döner; kök neden bir canlı denemeyle loga düşecek |
| Bot izinleri = 268520448 (View Channels, Send Messages, Embed Links, Read Message History, Manage Roles); Administrator yok; ayrıcalıklı intent yok | **VERIFIED_LIVE** |
| Yönetici yetki ayrımı (normal üye) | **BLOCKED** — sahibin ikinci hesabı yok; TESTED_OFFLINE |
| Rol eşleme (`roles map`) ve self-service onayı | **VERIFIED_LIVE** (DB: eşleme #1 TSQ Test Bildirim → Toro Wolves, ping kapalı, self-service) |
| Rol verme/alma, önceden sahip olunan rolün korunması, güvensiz rol reddi, panel | Sahip testleri yarıda bıraktı (sonuç teyit edilmedi) — TESTED_OFFLINE |
| Başarısız işlemlerin takip kodu log'da (`2e7f9bd`) | Uygulandı; canlıda bir hata vakasıyla henüz gözlenmedi |
| Guild'ler arası izolasyon | TESTED_OFFLINE (tek gerçek guild) |
| Bildirim çökme kurtarma, 429, belirsiz teslimat | TESTED_OFFLINE (canlıda yıkıcı test yok) |
| Liquipedia canlı veri / HLTV bağlantı zenginleştirmesi | **BLOCKED/OPTIONAL** (onaylı API anahtarı yok; yayın aşamasında başvuru) |
| VRS canlı veri | NOT_RUN — TESTED_OFFLINE |
| Global komut kaydı, herkese açık bot | DEFERRED |
| GitHub deposunun public olması | PRE-RELEASE REQUIREMENT |

Canlıda bulunup düzeltilen hatalar (hepsi `feature/live-validation`, regresyon testli, 202/202 ×3):
1. `f4f3b4e` — hatalı yapılandırma değeri CLI'ı yığın izi ile çökertip değeri ekrana basıyordu (`<…>` ile kaydedilmiş Application ID).
2. `49f4363` — Discord.Net'in ±2^53−1 "sınır yok" değerleri metin/kanal seçeneklerine yazılıyordu → her senkronda sahte Update.
3. `1178946` — `/setup` kanal seçimi kaydediliyor ama sihirbaz yenilenmiyordu (Etkinleştir pasif kalıyordu); status/about metinleri İngilizceydi.
4. `12fbd76` — demo saatleri her taramada yeniden bazlanıyordu → 15 dk hatırlatma hiç tetiklenmiyordu.
5. `7eaa62f` — demo bildirimleri gerçek twitch/Liquipedia bağlantısı veriyor ve "Kaynak: Liquipedia" diyordu.
6. `3577e54` — demo sıralaması "Kaynak: Valve" diyordu.
7. `2e7f9bd` — kullanıcıya gösterilen takip kodları log'a yazılmıyordu.
8. `148523b` — autocomplete hataları loglanmıyor ve yanıtsız kalıyordu (eşleme autocomplete kök nedeni açık).

Test guild'de kalan yapılandırma: rol eşleme #2 (TSQ Test Bildirim → **tüm maçlar**, hatırlatma ping'i **açık**) — sahip
testi sırasında varsayılanla oluştu; ileride demo hatırlatmaları bu test rolünü etiketleyebilir. Kaldırma:
`/esports-admin roles unmap mapping:2`.

Demo saatleri (düzeltildi, 2026-09-25): fixture zaman çıpası artık veritabanında (tablo `esports_provider_state`, anahtar `fixture:anchor`) saklanır
ve 24 saate kadar yeniden kullanılır. Önceden her yeniden başlatma demo saatlerini kaydırıyor, planlayıcı da bunu haklı olarak
"saat değişti" sayıp test kanalına yeni bir TEST/DEMO kartı gönderiyordu. Artık yeniden başlatma yeni kart üretmez; 24 saatten
sonra demo zaman çizelgesi bir kez yenilenir (aksi hâlde tüm demo maçlar geçmişte kalırdı). TESTED_OFFLINE; canlıda bir sonraki
yeniden başlatmada gözlenecek (ilk yeniden başlatma çıpayı kaydeder, sonrakiler kart üretmemeli).

Discord yapılandırması (koddan doğrulandı): gateway intent yalnızca **Guilds** (ayrıcalıklı intent yok); zorunlu kanal
izinleri View Channel + Send Messages + Embed Links, isteğe bağlı Read Message History (uzlaştırma) ve Manage Roles
(self-service); davet tamsayıları 84992 = 1024+2048+16384+65536 ve 268520448 = 84992+268435456 bu listeyle birebir
eşleşiyor. Administrator istenmez; Mention Everyone önerilmez (rolü "bahsedilebilir" yapın).

## Liquipedia resmî durumu ve karar (2026-09-25)

| Konu | Durum |
|---|---|
| LPDB API Terms (canlı sayfa okundu, VERIFIED) | Tüm istekler için en fazla **60 istek/saat**; sonuçları mümkün olduğunca uzun önbelleğe al; anahtar paylaşılmaz |
| User-Agent | İletişim bilgili UA şartı Terms'te açıkça **MediaWiki API** bölümünde; LPDB bölümünde ayrıca belirtilmiyor. TSQ Bot MediaWiki API kullanmaz → UA artık zorunlu değil (önerilir); her istekte özel UA gider (ayarlı değilse `TSQBot (https://github.com/Torokal/TSQ-Bot)`); upstream kimliği reddedilir |
| Planlar (sahip bildirimi; plan sayfası araçlarımıza insan doğrulaması gösterdi, aşılmadı) | Basic/Premium **geçici olarak kullanılamıyor**; ticari: Enterprise; ücretsiz: başvuruyla, çoğu zaman süreli |
| Bütçe | Kod artık tüm LPDB tabloları için **tek ortak** bütçe kullanıyor (önceden tablo başınaydı); bağlantı kaynağı 30 dk × ≤5 sayfa = en kötü ≈10 istek/saat |
| Önbellek | HLTV bağlantı adayları veritabanında (tablo `esports_provider_state`, anahtar `liquipedia:hltv-links`); yeniden başlatma erken istek yapmaz — TESTED_OFFLINE |
| Karar | Depo geliştirme boyunca PRIVATE; yalnızca Liquipedia için erken public yapılmaz; başvuru yayın aşamasında. O zamana kadar zenginleştirme BLOCKED/OPTIONAL, `Esports:VerifiedMatchLinks` elle yedek, PandaScore Liquipedia'dan bağımsız (TESTED_OFFLINE) |

## Yayın öncesi gereksinimler (PRE-RELEASE REQUIREMENT)

Bunlar yerel geliştirme/test için blocker **değildir**; yalnızca bot herkese açılmadan önce ve sahip açıkça yayın
aşamasına geçtiğinde yapılır. Ayrıntılı kontrol listesi: docs/OPERATIONS.md → "Yayın öncesi güvenlik kapısı".

| Gereksinim | Durum |
|---|---|
| `Torokal/TSQ-Bot` deposunu public yapmak (`/bot source` URL'si zaten nihai adres) | PRE-RELEASE REQUIREMENT — yapılmadı, yetkilendirilmedi |
| Tüm git geçmişinde secret taraması (bilinen sahte test token'ı hariç) | PRE-RELEASE REQUIREMENT — yayın anında tekrar |
| İzlenen dosya + kaynak arşivi denetimi, lisans/provenance, README kamu incelemesi | PRE-RELEASE REQUIREMENT |
| Global komut kaydı, herkese açık bot | DEFERRED (yayın aşaması) |
| Depo public olduktan **sonra** Liquipedia ücretsiz API erişimine başvuru (depo bunun için erkenden public yapılmaz) | PRE-RELEASE REQUIREMENT — sahip kararı (2026-09-25) |

## NEXT ACTION (güncel)

1. **Sahip:** PR #2'yi (canlı doğrulama) incelemek/merge etmek; ardından PR #3 (PandaScore) `main`'e yönelir.
2. **Sahip (isteğe bağlı):** test sunucusunda `/esports-admin roles unmap mapping:2` (ping'li test eşlemesi).
3. Kart görselleri tamamlandı: tüm v2 türleri (Started 🔴, Finished normal/spoiler, Postponed, Rescheduled, Canceled,
   Forfeit) ve Maç Sayfası görünümü **VERIFIED_LIVE** (2026-09-25). Yeniden başlatmada yeni demo "saat değişti" kartı sorunu
   düzeltildi (kalıcı fixture çıpası) — TESTED_OFFLINE, canlı gözlem bekliyor.
4. **Sahip:** PandaScore token'ı (`dotnet user-secrets set "PandaScore:Token" …`) → ajan önce yalnızca okuma doğrulaması.

## NEXT ACTION (önceki kayıt)

**Sahip (isteğe bağlı, tek adım):** `/esports-admin roles unmap` yazıp `mapping` alanına tıklamak → ajan log'daki
"Autocomplete failed [TS-…]" satırından kök nedeni düzeltir. Aynı komutla `mapping:2` girilerek ping'li test eşlemesi kaldırılabilir.

**Karar bekleyen:** `feature/live-validation` dalını GitHub'a push edip `main'e PR açmak (sahip onayı gerekir; push edilmedi).
Kalan canlı maddeler (yetki ayrımı için ikinci hesap, rol verme/alma, güvensiz rol, panel) TESTED_OFFLINE olarak kalabilir.
Liquipedia canlı: onaylı API anahtarı gelene kadar BLOCKED.
