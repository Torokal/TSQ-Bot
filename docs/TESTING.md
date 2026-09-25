# Test kanıtları

Çalıştırma: `.\scripts\Test.ps1` (Release build, uyarılar=hata, analyzer'lar, `dotnet format --verify-no-changes`,
manifest doğrulaması, tüm testler; `-Repeat N` ile N kez). Rapor: `TestResults\*.trx` (git'e girmez).

Testler **gerçek SQLite dosyası** (her test için ayrı geçici veritabanı, gerçek migration'lar), `FakeTimeProvider`, sahte
Discord transport'u ve sahte guild geçidi kullanır; üretimle **aynı DI kaydı** (`ToroHost.AddToroSquad`) kurulur ve DI
scope doğrulaması açıktır. Ağ erişimi yoktur.

## Test sınıfları (Windows 11, .NET SDK 10.0.401 / runtime 10.0.12)

Güncel sayı ve sonuç her zaman `docs/PROJECT_STATE.md` → "Çalıştırılan testler" bölümündedir.

| Sınıf | Kapsam |
|---|---|
| `Architecture.ArchitectureTests` | Bağımlılık yönleri; esports iş kodunda Discord SDK yok; her interaction sınıfında `[ToroModule]`; registry kopya reddi |
| `Contract.LiquipediaParserContractTests` | LPDB v3 şekli: TBD, `[]`/`{}`, null'lar, BO1/BO3/BO5, hükmen, oynanmadı, eksik harita skoru, beraberlik, string sayılar, bozuk tarih, 1/3 rakip, UTC tarih |
| `Contract.PandaScoreContractTests` | PandaScore (varsayılan sağlayıcı): sabit tarihli sözleşme verisiyle yaklaşan/oynanan/biten/ertelenen/yeniden planlanan/iptal/hükmen ayrıştırma; bilinmeyen durum Unknown kalır, rakiplerden olmayan winner_id kazanan olmaz, oyuncu/TBD rakip, geçersiz tarih, id'siz kayıt; boş dizi=başarılı boş; 401/403/404/400/429(+Retry-After)/5xx/timeout/bozuk JSON/dizi olmayan gövde; sayfalama (X-Total, kısa sayfa, sayfa limiti=Partial, sayfa ortasında hata=Partial); Bearer başlığı, URL'de token yok, `range[scheduled_at]`+`sort`; token yoksa NotConfigured ve istek yok; retry'lar yerel bütçeden düşer; fixture modunda gerçek istemci+ayrıştırıcı+sayfalama |
| `Contract.ProviderOutcomeTests` | Boş≠hata; 401/403/404/429/5xx/timeout/bozuk JSON sonuç tipleri; Retry-After; sınırlı retry; sayfalama (tam/sınırlı/ilerlemeyen/kısmi); yerel kota; live mod yapılandırma kuralları; başlıklar ve sorgu penceresi; canlı durum yeteneği yok |
| `Integration.AuthorizationAndIsolationTests` | 14 yönetici işlemi normal üyeye kapalı; ManageRoles gereksinimi; Guild A↔B izolasyonu; onay/ayar kapsamı; modül kapısı precondition'ı (DM reddi, kapalı modül, setup istisnası); ActorContext etkileşimden |
| `Integration.LifecycleNotificationTests` | Gerçek SQLite + planlayıcı + outbox: planlandı→oynanıyor tam bir "başladı" (tekrar poll ve yeniden başlatmada kopya yok); saatin geçmesi başlama değil; oynanıyor→bitti tek sonuç; ertelendi/iptal tek kart, ping yok; saat değişikliği yalnızca sağlayıcı bayrağı + ≥15 dk ile, yeni saat başına tek kart, İstanbul saati; ertelenene yeni tarih = saat değişti; ilk görüşte hiçbir geçiş yok; sağlayıcı ilk açılışı geçmişi/süren maçları duyurmaz; Unknown geçiş üretmez ve son durumu silmez; aynı adlı başka takım (farklı kimlik) rol ping'i tetiklemez; pause sırasında olan geçiş sonradan gönderilmez; sağlayıcı kesintisi (timeout/401/429/şema/taşıma) hiçbir geçiş/mesaj üretmez; demo kartları bir kez kuyruğa girer, tekrar çalıştırma yeni kayıt üretmez; yaşam döngüsü kartları hatırlatma anahtarına uyar |
| `Integration.OperationsTests` | Güvenli varsayılanlar; canlıya eksik ayarla geçiş reddi; demo→gerçek Discord için test guild şartı; ürün/kaynak bilgisi; secret redaction; repoda secret taraması; scriptler ASCII; localization tr/en eşliği; İstanbul/Berlin saat dilimi (Windows); ulong kayıpsızlığı; tek instance kilidi; migration eşliği + yedek/geri yükleme; fixture modunda gerçek istemci+parser+sayfalama; hata sonrası son iyi verinin korunması |
| `Integration.OutboxDeliveryTests` | Tekil anahtar; düzeltme=aynı mesajı ping'siz düzenleme; belirsiz timeout→içerik parmak izi ile uzlaştırma (kopya yok; belirsizlik sırasında değişen yük; başka satıra ait benzer mesaj alınmaz; eski footer ref'i); doğrulanmış yoklukta tek yeniden gönderim; çökme sonrası InFlight→DeliveryUnknown; uzlaştırma imkânsızsa sınırlı deneme; 429 Retry-After; geçici hatalarda sınırlı deneme; izin kaybı→kalıcı hata+kanal işareti, diğer guild etkilenmez; silinmiş mesaj yeni mesajla değiştirilmez; kapalı modül/pause gönderimden hemen önce iptal ve yeniden açılınca canlanmaz; süre aşımı; dry-run gönderilmez; kullanıcıya görünen footer'da ref yok |
| `Integration.PlannerTests` | İlk çalıştırma baseline; tekrar poll/yeniden başlatmada tek hatırlatma; iki takip edilen takım→tek mesaj, birleşik roller; saat ilerlemesi≠canlı; saat değişikliği→düzenleme ve yalnızca "saat güncellendi"; sonuç + düzeltme düzenlemesi; değişmeyen veride düzenleme yok; kesinti sonrası sınırlı catch-up; etkinleştirme öncesi sonuçlar gönderilmez; pause'daki guild planlanmaz; bayat veri; VRS yokken fail-closed |
| `Integration.RolesAndPrivacyTests` | Güvensiz rollerin (izin veren, özel kanal açan, botun üstündeki) self-service olamaması; managed/@everyone; onaylayan hiyerarşisi; takip→rol ver/kaldır; önceden sahip olunan rol korunur; paylaşılan rol son takibe kadar kalır; başarısız rol Failed kalır ve yeniden denenir; onay sonrası güvensizleşen rol verilmez; export yalnızca çağıran+bu guild; silme onayı kullanıcı+guild'e bağlı, tek kullanımlık, süreli, bot rolünü geri alır; ayrılan guild verisi saklama süresi sonunda silinir |
| `Unit.CommandManifestTests` | Gerçek slash şeması (tam komut/alt komut listesi); yönetici/kullanıcı ayrımı (`default_member_permissions`); guild-only + tüm açıklamalarda `tr`; ham ID yerine autocomplete/kanal/rol seçici; commit'lenmiş manifest = kod; yeni modül esports/çekirdeği değiştirmez; doğrulayıcı hataları; boş/eksik yüklenmiş manifest senkronu engeller; yanlış uygulama/izinsiz guild/onaysız global engellenir; diff yönetilmeyen komutları korur, prune yalnızca açık bayrakla; geçersiz manifestte uzak duruma dokunulmaz; dry-run hiçbir şey değiştirmez |
| `Unit.FilterAndRankingTests` | VEYA/VE kuralı; takım iki taraftan biri; turnuva/üst turnuva; eksik tier; VRS Top-N; VRS yok→engel; belirsiz eşleşme kullanılmaz; eşleştirme sırası; Türkçe İ/ı; Valve markdown ayrıştırma (başlıkla sütun bulma, tekrar eden başlık, bozuk satır); dosya adından yayın tarihi |
| `Unit.MatchCardTests` | Sade kartlar için birebir (golden) sonuç/başladı/ertelendi/saat değişti kartları, iptal/hükmen (kazanan yoksa yazılmaz); kartlarda harita, yayın, aşama, kimlik, "son veri", yıldız yok; spoiler: başlık/açıklama/alan/footer'da skor-kazanan-hükmen sızmaz, düzen kazanandan bağımsız; Discord sınırları (500 karakterlik adlarla); sağlayıcı metni ping/bağlantı/markdown üretemez; HLTV URL doğrulama (geçerli/normalize; javascript/data/discord/file/http/benzer host/yol-sorgu hilesi/kimlik bilgisi/port/takım sayfası/yol atlatma/localhost reddi); HLTV → resmî → sağlayıcı → yok önceliği, "HLTV" etiketi yalnızca HLTV'de; demo kartları bağlantısız; küratörlü liste doğrulaması; demo kartları her türü kapsar |
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

## Bağımsız inceleme sonrası eklenen regresyon testleri

`Changing_the_channel_or_re_enabling_results…`, `Correction_dropped_while_paused…`, `Catch_up_after_a_gap…` (genişletildi),
`Verified_absence_allows_only_a_single_resend`, `Unfollow_after_a_failed_grant…`, `Role_approved_for_self_service_later…`,
`Admin_without_mention_everyone…`, `Spoiler_result_hides_winner_width_and_map_count`,
`Provider_text_cannot_create_clickable_links…`, `Retries_spend_request_budget_too`,
`Prune_never_deletes_a_same_named_command_with_a_different_id`. Toplam: **193 test**.

## PandaScore / yaşam döngüsü / sade kart aşaması (2026-09-25)

88 yeni test (28 sağlayıcı sözleşmesi, 20 yaşam döngüsü, 41 kart/bağlantı; bazıları teori). Değişen eski testler: harita
skoru ve yayın bağlantısı artık kartta olmadığı için `Non_spoiler_result…`, `Incomplete_maps…` (→ "varsayılan kartta
harita yok") ve demo bağlantı testi yeni tasarıma göre güncellendi; canlıya geçiş testi PandaScore token'ını ve
Liquipedia anahtarını ayrı ayrı doğrular. Eski testler Liquipedia fixture'larıyla (`Esports:Provider:Name=Liquipedia`)
çalışmaya devam eder. Toplam: **290 test**.


## Liquipedia koşulları / önbellek aşaması (2026-09-25)

8 yeni test: `Without_an_operator_user_agent_the_product_default_is_sent`, `All_tables_share_one_hourly_budget`,
`A_contact_user_agent_is_only_detected_as_a_recommendation` (4 durum), `A_restart_reuses_the_persisted_links_without_an_extra_request`,
`Manual_verified_links_work_with_the_liquipedia_source_off`. Değişen: UA doğrulama teorisi (iletişimsiz/boş UA artık
sorun değil; upstream kimliği hâlâ reddedilir), bağlantı kaynağı kapalılık testi (anahtarsız canlı mod → kapalı, istek yok;
UA'sız ama anahtarlı → açık). Toplam: **319 test**.

## Kart UI temizliği + görünmez uzlaştırma (2026-09-25)

16 yeni test: 6 tür × gerçek mod (`Every_kind_has_a_plain_title_compact_fields_match_page_last_and_an_attribution_only_footer`:
önekisiz başlık, Etkinlik/Format(/Yeni Saat) satır içi, Maç Sayfası en altta ve yalnızca bağlantı varsa, footer tam olarak
"Kaynak: PandaScore"), 6 tür × demo modu (footer tam olarak TEST/DEMO metni, bağlantı yok), 4 outbox testi
(`Sent_messages_show_no_internal_reference…`, `A_payload_replaced_while_delivery_is_unknown…`,
`An_identical_message_owned_by_another_delivery…`, `Messages_sent_before_the_change…`) ve Discord.Net `Embed` dönüşümünün
parmak izini koruduğunu doğrulayan test. Güncellenen: demo başlık/footer beklentileri (MatchCardTests, MessageSafetyTests),
footer marker testi. Toplam: **335 test**. Gerçek Discord'da belirsiz timeout simüle edilemez → uzlaştırma TESTED_OFFLINE.

## Görsel sonlandırma (2026-09-25)

6 yeni test: `Demo_cards_link_only_to_the_reserved_test_domain` (example.com ve alt alanları evet; `example.com.evil.net`,
başka alan, HLTV, http hayır) ve demo hükmen kartının tam hedef biçimi (başlık, 🏳️ satırı, Etkinlik/Format, en altta Maç
Sayfası). Göreli zaman Discord'a ait olduğu için elle "… dakika önce" üreten test yoktur/eklenmedi; `<t:…:R>` biçimi ve doğru
olay zamanı golden testlerde doğrulanır. Toplam: **341 test**, ×3 temiz (kilit hatası tekrarlanmadı).

## Kalıcı fixture çıpası (2026-09-25)

2 yeni test (`FixtureRestartTests`): aynı veritabanında 40 dk sonra "yeniden başlatılan" ikinci host demo saatlerini
değiştirmez ve yeni `rescheduled-*` satırı üretmez (düzeltmeden önce çalıştırıldı ve **başarısız oldu**, sonra geçti); çıpa
24 saat içinde yeniden kullanılır, sonra yenilenir, gelecekteki çıpaya güvenilmez. Toplam: **343 test**.

## Canlı okuma kontrolü (2026-09-25)

`esports provider-check` (yeni CLI komutu): yapılandırılmış sağlayıcıdan normal pencereyi ve etkinlikleri **bir kez** okur,
özet yazdırır; Discord'a bağlanmaz (Fake taşıyıcı), DryRun, atılabilir veri klasörü, token yazdırılmaz. PandaScore ile
çalıştırıldı: Success, 163 maç, 35 etkinlik, kalan kota 994 (ayrıntı docs/PROVIDERS.md). Ağ gerektirdiği için otomatik test
paketinde yok; test paketi ağ çağrısı yapmaz.

## Bilinen gözlem

- İlk tam koşulardan birinde `SetUpEsportsGuildAsync` sırasında bir kez `DbUpdateException` görüldü; iç hata mesajı
  raporlanmadığı için kök nedeni belirlenemedi. Ardından **17 ardışık tam koşu** (181/181) temiz geçti. Olası neden:
  Windows'ta yeni oluşturulan SQLite dosyalarına yönelik geçici dosya kilidi (ör. antivirüs taraması). Test altyapısı artık
  EF iç hatasını mesaja ekler; tekrarlarsa neden raporda görünecek. Durum: **izleniyor**.
- 2026-09-25: 2. tekrarda `AuthorizationAndIsolationTests` teardown'unda "SQLite Error 5: database is locked" görüldü. Kök neden
  bulundu: `TestHost.DisposeAsync` `SqliteConnection.ClearAllPools()` çağırıyordu; bu, paralel çalışan **diğer** testlerin
  bağlantı havuzlarını da geri alıyordu. Artık yalnızca kendi havuzu temizleniyor (`ClearPool`). Önceki `DbUpdateException`
  gözleminin de olası nedeni budur. Düzeltme sonrası tam kapı ×3 temiz.
