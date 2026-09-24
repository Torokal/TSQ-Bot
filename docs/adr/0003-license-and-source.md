# ADR-0003 — Lisans: AGPL-3.0-only ve Corresponding Source

- Durum: Kabul edildi (2026-09-24). Sahip farklı bir lisans isterse: türetilmiş iki dosya sıfırdan yeniden yazılmalıdır.

## Karar
Upstream `BOT-Greg-v2_API` (AGPL v3) kodundan uyarlanmış kısımlar içerdiği için proje bütünü **AGPL-3.0-only**.
Türetilmiş dosyalarda kaynak, commit, lisans ve "değiştirildi" başlığı var. `/bot about` atıfları, `/bot source` çalışan
sürümün kaynağını (`Bot:SourceUrl`) gösterir; Gateway modunda `SourceUrl` yoksa başlatma reddedilir (yalnızca özel test
için açık bir bayrakla aşılabilir). `scripts/Export-Source.ps1` git'teki commit'in arşivini üretir (secret ve kullanıcı
verisi içermez, derleme talimatları dahil). Build, git commit'ini sürüm bilgisine gömer.
Kanonik depo: https://github.com/Torokal/TSQ-Bot (`Bot:SourceUrl` varsayılanı, `appsettings.json`). Yayın yaklaşımı
(2026-09-24): geliştirme ve özel test süresince depo **private**; bot herkese açılmadan **önce** public yapılır
(**PRE-RELEASE REQUIREMENT**) ve aynı URL dağıtılan sürümün kaynağını sunar. Private durum kalıcı bir lisans/mimari
kararı değildir. Değiştirilmiş sürümü işletenler kendi kaynak adreslerini vermelidir.
Policies deposu (lisanssız) içeriği kullanılmadı. Bu bir teknik karar kaydıdır, hukuki görüş değildir.
