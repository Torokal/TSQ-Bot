# ADR-0004 — Kalıcı outbox ve teslimat semantiği

- Durum: Kabul edildi (2026-09-24)

## Karar
- Mantıksal anahtar `live|dry | guild | module | kaynak:maçId | kanal | tür` üzerinde **unique index**. Aynı maçta iki
  takip edilen takım tek mesaj üretir (roller tek mesajda birleşir).
- Planlayıcı snapshot + outbox satırlarını **tek transaction**'da yazar.
- Durumlar: `Pending → InFlight → Sent | Failed | DeliveryUnknown`, ayrıca `Cancelled`, `Expired`, `Simulated`.
  InFlight, Discord'a istek atılmadan **önce** commit edilir. Yeniden başlatmada InFlight → DeliveryUnknown.
- Belirsiz sonuç (timeout, create sırasında 500/502/504): kör tekrar yok. Embed footer'daki `ref <marker>` ile son 50
  bot mesajında sınırlı uzlaştırma: bulunursa Sent, **doğrulanmış yoklukta** tek yeniden gönderim (ikinci kez belirsiz olursa
  Failed — döngü yok), uzlaştırma mümkün
  değilse (izin yok) 3 denemeden sonra operatöre bırakılır (doctor). **"Exactly once" iddiası yoktur.**
- Discord.Net `RetryMode.RetryRatelimit`: yalnızca 429'lar SDK tarafından Retry-After'a uyarak tekrarlanır; timeouts/502
  SDK'da tekrar edilmez (kopya mesaj riskini önlemek için) ve outbox'a "belirsiz" olarak gelir.
- 403/404/400 kalıcıdır; kanal sorunu guild konfigürasyonuna işlenir ve o kanala gönderim durur (diğer guild'ler etkilenmez).
- Düzeltmeler aynı mesajı **ping'siz** düzenler; silinmiş mesaj yeni mesajla değiştirilmez. Karşılaştırma Discord'da görünen
  içeriğe göre yapılır: pause sırasında düşen düzeltme, sonraki planlamada yeniden sıraya girer.
- Planlayıcı/dispatcher çakışması: satır başına iyimser eşzamanlılık belirteci (`Version`); dispatcher çakışmada yeniden
  yükleyip uygular, planlayıcı değişikliklerini bırakıp planı (deterministik) en fazla 3 kez yeniden hesaplar.
- Gönderimden hemen önce: modül kapısı + modül politikası (pause, kanal değişti, bildirim türü kapalı, kanal sorunu).
