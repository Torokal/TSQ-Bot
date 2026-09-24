# ToroSquad Bot — PROJECT_STATE

> Tek doğruluk kaynağı: gerçek durum, kararlar, çalıştırılan testler, blocker'lar ve tek NEXT ACTION.
> Yeni oturumda önce bu dosyayı, sonra `git status` / `git log --oneline -10` çıktısını doğrula.

Son güncelleme: **2026-09-24** — Aşama A–E yerel olarak tamamlandı; Aşama F (canlı) BLOCKED.

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
| Kaynak/lisans (/bot about, /bot source, Export-Source.ps1, commit gömme) | IMPLEMENTED + TESTED_OFFLINE (ürün bilgisi); yayımlanmış kaynak URL'si **yok** |
| Türkçe varsayılan / İngilizce fallback, Europe/Istanbul (Windows'ta test edildi) | TESTED_OFFLINE |
| PowerShell scriptleri: Doctor, Start-Dev (+Simulate), Test, Sync-Commands, Export-Source | Doctor/Test/Start-Dev/Sync(BLOCKED yolu)/Simulate **çalıştırıldı** (PS 5.1); Export-Source henüz çalıştırılmadı |
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
- `gh` CLI yok. Codex CLI mevcut; **kullanıcı kararıyla kullanılmadı**. Bağımsız okuma-yalnız inceleme için bir Claude
  alt ajanı kullanıldı (dosya yazmadı).
- NuGet: bir kez NuGet istemcisinin indirme bağlantısı takıldı (ağ hızlıydı); süreç durdurulup restore yeniden yapıldı.
- Git: `main` = yalnızca şartname; çalışma dalı `feature/foundation`. Remote yok, push yok.

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

- `.\scripts\Test.ps1 -Repeat 3` (PS 5.1): Release build **0 uyarı / 0 hata**, `dotnet format --verify-no-changes` temiz,
  manifest geçerli (7 komut, hash `63a5f52b0827…`), testler **181/181 ×3 geçti**.
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
  `Sync-Commands.ps1 -GuildId …` (token yok → "BLOCKED … Nothing was changed").
- **Hiçbir canlı Discord veya canlı Liquipedia/Valve çağrısı yapılmadı.**

## Blocker'lar (sahibin eylemi gerekir)

| # | Blocker | Gereken eylem | Onay kapısı |
|---|---|---|---|
| B1 | Discord uygulaması / bot token yok | Developer Portal'da uygulama oluştur, token'ı user-secrets'a koy (docs/WINDOWS_SETUP.md §3–4) | Hesap/yetkilendirme — sahip |
| B2 | Test guild ve davet | Botu test sunucusuna davet et (izin 84992 / 268520448), `Discord:TestGuildIds` + `CommandSyncGuildIds` | Bot daveti — sahip |
| B3 | Liquipedia API erişimi | Başvuru/plan seçimi (ücretli olabilir; ücretsiz yalnızca açık kaynak + ticari olmayan, onaylı) | Ücret/abonelik — sahip |
| B4 | Kaynak yayımlama (AGPL) | Herkese açık kullanım öncesi repo/arşiv yayımla, `Bot:SourceUrl` | GitHub/yayın — sahip |
| B5 | Upstream geliştiriciye mesaj | docs/drafts/upstream-contact.md taslağı gönderilmedi | Dış iletişim — sahip |

## NEXT ACTION

**Sahip:** B1+B2 — Discord uygulamasını oluşturup token'ı `dotnet user-secrets` ile kaydetmek ve botu bir test sunucusuna
eklemek; ardından bu oturuma test guild ID'sini ve "test sunucusunda komut kaydı + test bildirimi" onayını vermek.
(Onay sonrası ajan: `Sync-Commands.ps1 -GuildId <id>` dry-run → `-Apply` → komut seçicisi, autocomplete, defer, yetki
ayrımı, rol paneli ve TEST/DEMO bildirimi doğrulaması → sonuçları VERIFIED_LIVE / başarısız olarak bu dosyaya yazmak.)
