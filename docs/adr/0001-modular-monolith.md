# ADR-0001 — Modüler monolit, C#/.NET 10, Discord.Net, SQLite + EF Core

- Durum: Kabul edildi (2026-09-24)

## Bağlam
Tek işletmeci, küçük ölçek; ileride başka modüller eklenecek. Upstream kodu C#/.NET 10. Windows öncelikli, Docker/WSL
ön koşul olmamalı.

## Karar
- .NET 10 LTS (SDK 10.0.401, runtime 10.0.12 — 2026-09-08 yaması), `global.json` ile `latestPatch`.
- Discord.Net 3.20.1 (net10.0 hedefli, Interaction Framework), Generic Host, DI, `BackgroundService`.
- SQLite + EF Core 10.0.12, migration'lar `ToroSquad.Bot` içinde; tek `ToroDbContext`, modüller tablolarını
  `IModelContributor` ile ekler (Infrastructure modülleri tanımaz; model derleme başına sabittir).
- Proje sayısı sorumluluk sınırları kadar: Core / Infrastructure / Discord / modüller / Bot / tek test projesi.
- Upstream'in HTTP API katmanı alınmadı: veri kodu aynı süreçte kütüphane olarak çalışır. **Herkese açık HTTP uç noktası
  yok** (gerekçe: gereksiz saldırı yüzeyi; sağlayıcı kotası botun kendi kullanımına ayrılmalı).
- Tek instance: veri dizininde işletim sistemi seviyesinde özel kilit (`SingleInstanceLock`); ikinci instance başlamaz.

## Sonuçlar
Yatay ölçekleme desteklenmez (belgelendi). Modül ekleme = yeni proje + composition root'a tek satır kayıt.
Analyzer'lar ve uyarılar hata sayılır (`TreatWarningsAsErrors`), paket sürümleri merkezi ve kilit dosyalı.
