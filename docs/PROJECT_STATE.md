# ToroSquad Bot — PROJECT_STATE

> Tek doğruluk kaynağı: gerçek durum, kararlar, çalıştırılan testler, blocker'lar ve tek NEXT ACTION.
> Yeni oturumda önce bu dosyayı, `git status`/`git log` çıktısını doğrula.

Son güncelleme: 2026-09-24 — Aşama A (başlangıç)

## Kısa uygulama planı

| Aşama | Kapsam | Durum |
|---|---|---|
| A | Ortam, upstream, lisans, provider erişimi incelemesi | IN_PROGRESS |
| B | Core, modül registry, ayarlar, slash schema/manifest, fake transport | TODO |
| C | Esports provider adaptörleri (Liquipedia, Valve VRS), fixture contract testleri, sorgu komutları | TODO |
| D | Filtreler, kalıcı abonelikler, güvenli roller, bildirim outbox'ı | TODO |
| E | Kurulum UX, privacy, PowerShell scriptleri, recovery testleri | TODO |
| F | Canlı provider/test-guild doğrulaması, release hazırlığı | BLOCKED (erişim yok) |

Mimari: modüler monolit, tek süreç, tek instance, SQLite + EF Core, Discord.Net Interaction Framework.
Projeler: `ToroSquad.Core`, `ToroSquad.Infrastructure`, `ToroSquad.Discord`, `ToroSquad.Modules.Esports`,
`ToroSquad.Modules.Example`, `ToroSquad.Bot` (composition root), `tests/ToroSquad.Tests`.

## Ortam (2026-09-24 doğrulandı)

- Windows 11 Pro 10.0.26200, PowerShell 5.1, git 2.55.0.
- .NET SDK başlangıçta YOKTU; kullanıcı onayıyla `winget install Microsoft.DotNet.SDK.10 --version 10.0.401` kuruldu
  (runtime 10.0.12, 2026-09-08 yayını, LTS, EOL 2028-11-14).
- `gh` CLI yok. Codex CLI mevcut; kullanıcı kararıyla KULLANILMIYOR.
- Git deposu bu oturumda başlatıldı: `main` (yalnızca şartname), çalışma dalı `feature/foundation`. Remote yok, push yok.

## NEXT ACTION

Aşama A bulgularını kaydet, çözüm iskeletini kur.
