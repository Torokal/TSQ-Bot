# ADR-0006 — PandaScore varsayılan sağlayıcı, maç yaşam döngüsü ve doğrulanmış maç bağlantıları

- Durum: Kabul edildi (2026-09-25)

## Bağlam
Liquipedia canlı erişimi onaylı anahtar gerektiriyor (2026-09-25: Basic/Premium geçici olarak kullanılamıyor, ücretsiz erişim başvuruyla) ve doğrulanmış "maç başladı" durumu sunmuyor.
Sahip, bildirimlerin BOT Greg benzeri sade kartlar olmasını ve başladı / bitti / ertelendi / saat değişti / iptal
bildirimlerini istedi. HLTV maç sayfası tercih edilen dış bağlantı, ancak HLTV kazınamaz.

## Karar
1. **PandaScore varsayılan maç sağlayıcısı** (`Esports:Provider:Name=PandaScore`). Liquipedia **eski/isteğe bağlı**
   sağlayıcı olarak kalır (`Liquipedia` seçilirse). Sağlayıcı soyutlaması (`IEsportsDataProvider`) değişmedi; planlayıcı,
   önbellek, komutlar ve kartlar yalnızca normalize modeli görür. Valve VRS sıralama sağlayıcısı olarak aynen kalır.
2. Resmî dokümantasyondan doğrulananlar (2026-09-25, docs/PROVIDERS.md): durumlar `not_started/running/finished/postponed/
   canceled`, `rescheduled` + `original_scheduled_at`, `forfeit` + `winner_id`; Bearer başlığı; `page[number]/page[size]`
   (≤100) + `X-Total`; ücretsiz plan 1.000 istek/saat; CS2 uçları `/csgo/`; koşullar md. 6.4: "Source: PandaScore" atfı.
3. **Yaşam döngüsü** yalnızca iki **bilinen** sağlayıcı durumu arasındaki geçiş gözlendiğinde kaydedilir (anlık görüntüde
   zaman damgası): planlandı/ertelendi → oynanıyor = **başladı**; planlandı → ertelendi; herhangi → iptal; sağlayıcının
   `rescheduled` bayrağı + ≥15 dk kayma (veya ertelenmiş maça yeni tarih) = **saat değişti**. Saatten, ilk görüşten,
   `Unknown`'dan veya sağlayıcı kesintisinden asla çıkarılmaz; ilk açılış sağlayıcı bazındadır.
4. Her kart sunucu+maç+tür başına **bir** kez (saat değişikliği: yeni saat başına) kalıcı outbox üzerinden gider;
   watermark ve tazelik penceresi uygulanır. Yalnızca "başladı" takım rolünü pingleyebilir.
5. **Kartlar sade**: başlık maçı söyler, tek durum satırı, en fazla Etkinlik + Format (+ Yeni Saat), isteğe bağlı
   "Maç Sayfası", kaynak atfı. Harita skorları, yayınlar, aşama, iç kimlikler, "yıldız" yok.
6. **Dış bağlantılar veri alımından ayrıdır**: `MatchLinks { HltvMatchUrl, OfficialMatchUrl, ProviderMatchUrl }`.
   HLTV bağlantısı yalnızca güvenilir, deterministik bir kaynaktan gelir (bugün: işletmecinin `Esports:VerifiedMatchLinks`
   listesi; ileride yetkili bir sağlayıcı alanı veya yönetici komutu). Yalnızca `https://www.hltv.org/matches/<id>/<slug>`
   kabul edilir; sayfa **asla indirilmez**, kimlik **asla tahmin edilmez**. Öncelik: HLTV → resmî → sağlayıcı → yok.

## Sonuçlar
- PandaScore canlı verisi token ile doğrulanana kadar **BLOCKED**; ücretsiz planda kazanan/skor alanlarının dolu gelip
  gelmediği resmî sayfalar arasında çelişkili (docs/PROVIDERS.md) → canlıda doğrulanmalı.
- HLTV'yi veri sağlayıcısı olarak kullanmak (kazıma, Cloudflare aşma, gizli uçlar) proje tasarımı gereği **yasak**; yetkili
  erişim olmadan **DEFERRED**.
- "Yıldız" (eski BOT Greg puanı) güvenilir, belgelenmiş bir kaynak olmadan gösterilmez (**DEFERRED**).

## Değişiklik (2026-09-26): ayrı "saat değişti" kartı kaldırıldı

Sahip, canlı görsel incelemede ayrı "Maçın saati değişti" kartından vazgeçti. 3. maddedeki geçiş anlık görüntüde iç durum
olarak kaydedilmeye devam eder ama **kart üretmez**: saat değişince mevcut hatırlatma mesajı ping'siz düzenlenir (yeni saat
Discord'un göreli zamanıyla + "Başlangıç saati güncellendi (önceki: …)"); hatırlatma yoksa yeni saat yalnızca maç durumudur
ve zamanı gelince normal hatırlatma gider. 4. maddedeki "(saat değişikliği: yeni saat başına)" ve 5. maddedeki "(+ Yeni
Saat)" artık geçersizdir. Ertelendi kartı korunur. Eski `rescheduled-*` outbox satırları ve gönderilmiş kartlar silinmez,
düzenlenmez. Ayrıntı: docs/NOTIFICATIONS.md "Saat değişikliği".
