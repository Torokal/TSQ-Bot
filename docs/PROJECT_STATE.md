# TSQ Bot — PROJECT_STATE

> Tek doğruluk kaynağı: gerçek durum, kararlar, çalıştırılan testler, blocker'lar ve tek NEXT ACTION.
> Yeni oturumda önce bu dosyayı, sonra `git status` / `git log --oneline -10` çıktısını doğrula.

Son güncelleme: **2026-09-24** — Aşama A–E yerel olarak tamamlandı; ürün adı **TSQ Bot** oldu; GitHub'da yayımlandı, PR #1
incelemede (merge edilmedi); Aşama F (canlı) BLOCKED.

## Ürün kimliği ve depo

| | |
|---|---|
| Ürün adı | **TSQ Bot** (eski adı ToroSquad Bot — 2026-09-24'te değiştirildi) |
| Kanonik depo | `Torokal/TSQ-Bot` → https://github.com/Torokal/TSQ-Bot (**PRIVATE** — sahip isteğiyle 2026-09-24'te public → private yapıldı) |
| Rename | **Tamamlandı** (yerel) — commit `671e7cc` `refactor(branding): rename product to TSQ Bot` |
| GitHub push | Depo **oluşturuldu** (Torokal hesabı, 2026-09-24; önce public, ardından sahip isteğiyle **private**). `main` (`177b5f7`) ve `feature/foundation` (`4912596`) **push edildi**, force/squash yok. İlk `feature/foundation` push'u GitHub push protection'a takıldı: `6c4b696`/`5570842` içindeki `tests/ToroSquad.Tests/Integration/OperationsTests.cs:86` **sahte** test dizesi "Discord Bot Token" sanıldı; sahip GitHub'da "used in tests" izni verdi. Geçmiş yeniden yazılmadı; `671e7cc` dizeyi çalışma anında birleştiriyor |
| GitHub'daki dallar | `main`, `feature/foundation` (git ls-remote ile doğrulandı) |
| Pull request | https://github.com/Torokal/TSQ-Bot/pull/1 — `feature/foundation → main`, "Foundation: modular TSQ Bot core and esports module"; **açık, merge edilmedi**. GitHub farkı: 8 commit, 159 dosya; binary/veritabanı/secret dosyası yok. CI yok — yalnızca yerel test sonuçları |
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
| Kaynak/lisans (/bot about, /bot source, Export-Source.ps1, commit gömme) | TESTED_OFFLINE; `Bot:SourceUrl` varsayılanı https://github.com/Torokal/TSQ-Bot (depo şu an **private** → başkalarına açık kaynak bağlantısı sağlamıyor; foundation kodu `feature/foundation` / PR #1) |
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
| B4 | PR #1'in main'e merge edilmesi | https://github.com/Torokal/TSQ-Bot/pull/1 incelenip merge edilmeli (merge = sahip kararı) | main değişikliği — sahip |
| B6 | AGPL kaynak erişimi (depo private) | Bot yalnızca sahibin kendisi/özel test için çalışırken sorun yok. Bot **başkalarına** sunulmadan önce: depoyu yeniden public yap **veya** çalışan commit'in `Export-Source.ps1` arşivini erişilebilir bir yerde yayımlayıp `Bot:SourceUrl`'i ona çevir. Not: Liquipedia'nın ücretsiz erişimi açık kaynak şartı arar (B3) | Görünürlük/yayın — sahip |
| B5 | Upstream geliştiriciye mesaj | docs/drafts/upstream-contact.md taslağı gönderilmedi | Dış iletişim — sahip |

## NEXT ACTION

**Sahip:** B1+B2 — Discord Developer Portal'da **"TSQ Bot"** uygulamasını oluşturup token'ı `dotnet user-secrets` ile kaydetmek,
botu bir test sunucusuna eklemek ve bu oturuma test guild ID'si ile "test sunucusunda komut kaydı + test bildirimi" onayını
vermek. (Paralelde: PR #1'i incelemek/merge etmek — sahip kararı.)
(Onay sonrası ajan: `Sync-Commands.ps1 -GuildId <id>` dry-run → `-Apply` → komut seçicisi, autocomplete, defer, yetki
ayrımı, rol paneli ve TEST/DEMO bildirimi doğrulaması → sonuçları VERIFIED_LIVE / başarısız olarak bu dosyaya yazmak.)
