# ADR-0002 — Sağlayıcı dürüstlüğü: sonuç tipleri, canlı durum, fixture ayrımı

- Durum: Kabul edildi (2026-09-24)

## Karar
1. Her sağlayıcı çağrısı `ProviderResult<T>` döner: `Success` (boş liste dahil), `Partial`, `NotConfigured`,
   `AuthFailed`, `QuotaExceeded(+RetryAfter)`, `Timeout`, `TransportError`, `SchemaError`, `Unavailable`.
   Hata asla "maç yok" olarak gösterilmez; son iyi veri korunur ve bayat (stale) işaretlenir.
2. Liquipedia doğrulanmış canlı durum sunmaz → `VerifiedLiveStatus` yeteneği yok → "maç başladı/canlı" bildirimi yok;
   hatırlatmalar "planlanan saat" hatırlatmasıdır. Saat değişikliği yalnızca "saat güncellendi" olarak anlatılır.
3. `ProviderMode` (Fixture/Live), `DiscordTransport` (Fake/Gateway) ve `DeliveryMode` (DryRun/Send) bağımsız ayarlardır.
   Fixture modu gerçek HTTP istemcisini ve parser'ı bir fixture `HttpMessageHandler` ile çalıştırır; Live modda bu handler
   DI'a hiç eklenmez (sessiz geri dönüş yolu fiziksel olarak yoktur).
4. Kota: yerel token bucket (tablo başına, doğrulanmış 60/saat × %80) + başlangıçta polling yapılandırması doğrulaması.
5. Bayat veri yeni bildirim üretmez; planlayıcı yalnızca taze başarılı fetch sonrası çalışır.
