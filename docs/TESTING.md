# Test kanıtları

Çalıştırma: `.\scripts\Test.ps1` (Release build, uyarılar=hata, analyzer'lar, `dotnet format --verify-no-changes`,
manifest doğrulaması, tüm testler; `-Repeat N` ile N kez). Rapor: `TestResults\*.trx` (git'e girmez).

Testler **gerçek SQLite dosyası** (her test için ayrı geçici veritabanı, gerçek migration'lar), `FakeTimeProvider`, sahte
Discord transport'u ve sahte guild geçidi kullanır; üretimle **aynı DI kaydı** (`ToroHost.AddToroSquad`) kurulur ve DI
scope doğrulaması açıktır. Ağ erişimi yoktur.

## Son koşu (2026-09-24, Windows 11, .NET SDK 10.0.401 / runtime 10.0.12)

Güncel sayı ve sonuç her zaman `docs/PROJECT_STATE.md` → "Çalıştırılan testler" bölümündedir.

| Sınıf | Kapsam |
|---|---|
| `Architecture.ArchitectureTests` | Bağımlılık yönleri; esports iş kodunda Discord SDK yok; her interaction sınıfında `[ToroModule]`; registry kopya reddi |
| `Contract.LiquipediaParserContractTests` | LPDB v3 şekli: TBD, `[]`/`{}`, null'lar, BO1/BO3/BO5, hükmen, oynanmadı, eksik harita skoru, beraberlik, string sayılar, bozuk tarih, 1/3 rakip, UTC tarih |
| `Contract.ProviderOutcomeTests` | Boş≠hata; 401/403/404/429/5xx/timeout/bozuk JSON sonuç tipleri; Retry-After; sınırlı retry; sayfalama (tam/sınırlı/ilerlemeyen/kısmi); yerel kota; live mod yapılandırma kuralları; başlıklar ve sorgu penceresi; canlı durum yeteneği yok |
| `Integration.AuthorizationAndIsolationTests` | 14 yönetici işlemi normal üyeye kapalı; ManageRoles gereksinimi; Guild A↔B izolasyonu; onay/ayar kapsamı; modül kapısı precondition'ı (DM reddi, kapalı modül, setup istisnası); ActorContext etkileşimden |
| `Integration.OperationsTests` | Güvenli varsayılanlar; canlıya eksik ayarla geçiş reddi; demo→gerçek Discord için test guild şartı; ürün/kaynak bilgisi; secret redaction; repoda secret taraması; scriptler ASCII; localization tr/en eşliği; İstanbul/Berlin saat dilimi (Windows); ulong kayıpsızlığı; tek instance kilidi; migration eşliği + yedek/geri yükleme; fixture modunda gerçek istemci+parser+sayfalama; hata sonrası son iyi verinin korunması |
| `Integration.OutboxDeliveryTests` | Tekil anahtar; düzeltme=aynı mesajı ping'siz düzenleme; belirsiz timeout→marker ile uzlaştırma (kopya yok); doğrulanmış yoklukta tek yeniden gönderim; çökme sonrası InFlight→DeliveryUnknown; uzlaştırma imkânsızsa sınırlı deneme; 429 Retry-After; geçici hatalarda sınırlı deneme; izin kaybı→kalıcı hata+kanal işareti, diğer guild etkilenmez; silinmiş mesaj yeni mesajla değiştirilmez; kapalı modül/pause gönderimden hemen önce iptal ve yeniden açılınca canlanmaz; süre aşımı; dry-run gönderilmez; footer marker |
| `Integration.PlannerTests` | İlk çalıştırma baseline; tekrar poll/yeniden başlatmada tek hatırlatma; iki takip edilen takım→tek mesaj, birleşik roller; saat ilerlemesi≠canlı; saat değişikliği→düzenleme ve yalnızca "saat güncellendi"; sonuç + düzeltme düzenlemesi; değişmeyen veride düzenleme yok; kesinti sonrası sınırlı catch-up; etkinleştirme öncesi sonuçlar gönderilmez; pause'daki guild planlanmaz; bayat veri; VRS yokken fail-closed |
| `Integration.RolesAndPrivacyTests` | Güvensiz rollerin (izin veren, özel kanal açan, botun üstündeki) self-service olamaması; managed/@everyone; onaylayan hiyerarşisi; takip→rol ver/kaldır; önceden sahip olunan rol korunur; paylaşılan rol son takibe kadar kalır; başarısız rol Failed kalır ve yeniden denenir; onay sonrası güvensizleşen rol verilmez; export yalnızca çağıran+bu guild; silme onayı kullanıcı+guild'e bağlı, tek kullanımlık, süreli, bot rolünü geri alır; ayrılan guild verisi saklama süresi sonunda silinir |
| `Unit.CommandManifestTests` | Gerçek slash şeması (tam komut/alt komut listesi); yönetici/kullanıcı ayrımı (`default_member_permissions`); guild-only + tüm açıklamalarda `tr`; ham ID yerine autocomplete/kanal/rol seçici; commit'lenmiş manifest = kod; yeni modül esports/çekirdeği değiştirmez; doğrulayıcı hataları; boş/eksik yüklenmiş manifest senkronu engeller; yanlış uygulama/izinsiz guild/onaysız global engellenir; diff yönetilmeyen komutları korur, prune yalnızca açık bayrakla; geçersiz manifestte uzak duruma dokunulmaz; dry-run hiçbir şey değiştirmez |
| `Unit.FilterAndRankingTests` | VEYA/VE kuralı; takım iki taraftan biri; turnuva/üst turnuva; eksik tier; VRS Top-N; VRS yok→engel; belirsiz eşleşme kullanılmaz; eşleştirme sırası; Türkçe İ/ı; Valve markdown ayrıştırma (başlıkla sütun bulma, tekrar eden başlık, bozuk satır); dosya adından yayın tarihi |
| `Unit.MessageSafetyTests` | Spoiler modunda skor/kazanan/harita/renk sızıntısı yok (tr/en, normal/hükmen); normal sonuç içeriği; eksik harita notu; demo etiketi; allowed_mentions kapalı varsayılan; yalnızca açık roller; düzenleme/önizleme ping'siz; mention enjeksiyonu ve spoiler kırma engellenir; URL allow-list; embed limitleri |

## Kabul ölçütleri eşlemesi (şartname §13)

| # | Ölçüt | Kanıt | Durum |
|---|---|---|---|
| 1 | Gerçek slash şeması doğru, yetki grupları ayrı | CommandManifestTests (5 test) + `docs/commands.manifest.json` | TESTED_OFFLINE |
| 2 | Yetkisiz kullanıcı slash/modal/buton ile yönetici işlemi yapamaz | AuthorizationAndIsolationTests (servis katmanı tüm giriş yollarının ortak yolu; precondition testi) | TESTED_OFFLINE (Discord istemcisinde gerçek tıklama: NOT_RUN) |
| 3 | Esports kapalıyken çekirdek çalışır, gönderim durur | Core_stays_on…, Module_precondition…, Module_disabled_or_paused…, Disabled_or_paused_guilds… | TESTED_OFFLINE |
| 4 | Yeni modül esports'u değiştirmeden eklenir | New_module_adds_commands_without_changing…, mimari testler | TESTED_OFFLINE |
| 5 | Guild A ↔ B izolasyonu | Guild_a_cannot_modify_or_read…, Confirmations_and_settings…, export testi | TESTED_OFFLINE |
| 6 | API hatası≠maç yok; saat≠başladı | ProviderOutcomeTests, Provider_failure_keeps_last_good_data…, Passing_the_planned_start… | TESTED_OFFLINE |
| 7 | Sayfalama, eksik JSON, TBD, düzeltmeler | ProviderOutcomeTests (sayfalama), LiquipediaParserContractTests, New_result…corrections | TESTED_OFFLINE |
| 8 | Filtreler ve belirsiz VRS | FilterAndRankingTests | TESTED_OFFLINE |
| 9 | Tekrar poll/restart aynı bildirimi üretmez | Reminder_is_created_once…restarts, Staging_the_same… | TESTED_OFFLINE |
| 10 | Gönderimde çökme ve belirsiz HTTP | Ambiguous_timeout…, Crash_while_in_flight…, Reconciliation_impossible… | TESTED_OFFLINE |
| 11 | Rate limit, silinmiş kanal/rol, izin kaybı | Rate_limit…, Lost_permission…, Deleted_message…, rol UnknownRole yolu | TESTED_OFFLINE |
| 12 | Rol self-service yetki yükseltmesi / üyelik kaybı yok | RolesAndPrivacyTests (8 test) | TESTED_OFFLINE |
| 13 | Spoiler + allowed_mentions; önizleme ping atmaz | MessageSafetyTests | TESTED_OFFLINE |
| 14 | Kaynak/lisans, secret redaction, export/delete | OperationsTests, RolesAndPrivacyTests | TESTED_OFFLINE |
| 15 | Boş/hatalı manifest komut silmez; varsayılanlar canlıya geçmez | CommandManifestTests (sync), Shipped_defaults…, Going_live_without… | TESTED_OFFLINE |
| — | Gerçek test guild'inde: seçicide görünme, autocomplete, defer, yetki ayrımı, rol paneli, test bildirimi | — | **BLOCKED** (bot token / test guild yok) |

## Bilinen gözlem

- İlk tam koşulardan birinde `SetUpEsportsGuildAsync` sırasında bir kez `DbUpdateException` görüldü; iç hata mesajı
  raporlanmadığı için kök nedeni belirlenemedi. Ardından **17 ardışık tam koşu** (181/181) temiz geçti. Olası neden:
  Windows'ta yeni oluşturulan SQLite dosyalarına yönelik geçici dosya kilidi (ör. antivirüs taraması). Test altyapısı artık
  EF iç hatasını mesaja ekler; tekrarlarsa neden raporda görünecek. Durum: **izleniyor**.
