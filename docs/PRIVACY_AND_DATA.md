# Gizlilik ve veri saklama (teknik kayıt)

TSQ Bot mesaj akışı okumaz (mesaj olayları dinlenmez; gateway intent'i yalnızca Guilds), üye listesi indirmez (Guild
Members intent kapalı), presence izlemez. Profil, avatar, kullanıcı adı **saklanmaz**. Tek istisna TSQ Quote'tur: uygulama
bunun için Message Content erişimini (Developer Portal) kullanır ve `/quote` bir üyenin açıkça verdiği **tek** mesajı
(erişim denetimlerinden sonra) bir kez REST ile okur, yazarının görünen adını, kullanıcı adını ve profil fotoğrafını
(Discord CDN) alır, görseli üretip kanala gönderir. Akış: oku → düz metne çevir → çiz → at. Mesaj metni, isim, avatar URL'si
ve baytları yalnızca o isteğin belleğinde yaşar; veritabanına, önbelleğe ve loglara yazılmaz (loglarda yalnızca
kimlikler ve sonuç) ([quote/TSQ_QUOTE.md](quote/TSQ_QUOTE.md)).

## Tutulan kayıtlar

| Tablo | İçerik | Kimin | Silinme |
|---|---|---|---|
| `guild_settings`, `guild_module_state`, `esports_guild_config`, `esports_filter`, `esports_role_mapping` | Sunucu ayarları; değiştiren yöneticinin kullanıcı ID'si | sunucu | bot sunucudan çıkarıldıktan 30 gün sonra |
| `esports_team_follow`, `esports_user_pref` | Kullanıcı ID + takip edilen takım / spoiler tercihi | kullanıcı | `/privacy delete`, sunucu verisiyle birlikte |
| `esports_role_grant` | Kullanıcı ID + rol ID + botun verip vermediği | kullanıcı | takip bitince / `/privacy delete` |
| `outbox` | Sunucu kanalına gönderilen bildirimlerin içeriği ve durumu (kişisel veri içermez; rol ping ID'leri) | sunucu | sunucu verisiyle birlikte |
| `confirmation` | Kısa ömürlü silme onayı (kullanıcı ID, 5 dk) | kullanıcı | tüketilince / süresi dolunca |
| `esports_match_snapshot`, `esports_known_team`, `esports_provider_state` | Herkese açık maç/takım/sıralama verisi | — | 14 gün görülmeyen maçlar silinir |
| `f1_guild_config` | Formula 1 sunucu ayarları; değiştiren yöneticinin kullanıcı ID'si | sunucu | bot sunucudan çıkarıldıktan 30 gün sonra |
| `f1_session_snapshot`, `f1_result_snapshot`, `f1_standings_snapshot`, `f1_provider_state` | Herkese açık F1 takvim, seans durumu, sonuç ve puan durumu verisi (kişisel veri yok) | — | — |
| `lfg_listing` | TSQ LFG ekip ilanı: ilan sahibinin kullanıcı ID'si, kendi yazdığı oyun adı ve detay, kanal/mesaj ID, durum ve zamanlar; başkasının ilanını kapatan moderatörün kullanıcı ID'si | kullanıcı (sahip; kapatan moderatör) | `/privacy delete` (sahip: ilan oyuncularıyla silinir; moderatör: yalnızca "kapatan" kaydı temizlenir), sunucu verisiyle birlikte |
| `lfg_participant` | İlan ID + oyuncunun kullanıcı ID'si + cevabı (Katıldı / Belki / Bekleme listesinde) + cevap zamanı + bekleme listesindeyken sıra değeri (görünen ad saklanmaz) | kullanıcı | ayrılınca / `/privacy delete`, sunucu verisiyle birlikte |
| `outbox` (LFG etkinlik bildirimleri) | Etiketlenen Joined oyuncuların kullanıcı ID'leri (bildirim içeriği) | kullanıcı | teslimden/bitişten 24 saat sonra; `/privacy delete` ile hemen — o an Discord'a gönderilmekte olan (in-flight) ya da teslimi henüz uzlaştırılan satır hariç: o satır bittikten 24 saat sonra silinir |
| `lfg_guild_config` | İsteğe bağlı LFG kanalı; değiştiren yöneticinin kullanıcı ID'si | sunucu | bot sunucudan çıkarıldıktan 30 gün sonra |
| *(bellek, tablo değil)* LFG form taslakları | `/ekip` / Düzenle formunun adımları arasında: kullanıcı, sunucu, kanal ID'si, yazılan metinler ve seçimler | kullanıcı | **veritabanına hiç yazılmaz**; son kullanımdan 30 dk sonra, kayıt/iptal anında ya da restart'ta silinir; bu kısa ömür nedeniyle `/privacy export/delete` kapsamında değildir |
| `birthday_registration` | TSQ Doğum Günü: kullanıcı ID + **gün ve ay** (yıl yok) + kayıt/güncelleme zamanı; üyenin kendisi veya bir Administrator/sunucu sahibi (`/birthday-admin set`, logda denetim izi: yönetici ID, gün, ay) yazar; üye kendi kaydını `/birthday show` ile, bir Administrator/sunucu sahibi tek bir üyeninkini `/birthday-admin show` ile görür (denetim logu: yönetici ve hedef ID, bulundu mu; tarih yok). Toplu/herkese açık liste yok | kullanıcı | `/birthday remove` / `/privacy delete`, sunucu verisiyle birlikte |
| `birthday_celebration` | Kutlama kaydı: kullanıcı ID, yıl ve kutlanan yerel tarih, duyuruda adı geçti mi, botun verdiği rolün durumu (botun vermediği rol hiç kaldırılmaz) | kullanıcı | ertesi yıl sonunda; `/privacy delete` (botun verdiği rol de geri alınır), sunucu verisiyle birlikte |
| `birthday_announcement`, `birthday_guild_config` | Günlük duyurunun durumu (kişi sayısı, kullanıcı ID'si yok); duyuru kanalı ve değiştiren yöneticinin kullanıcı ID'si | sunucu | 30 gün sonra / bot sunucudan çıkarıldıktan 30 gün sonra |
| `outbox` (Doğum Günü duyurusu) | Duyuru metni: kutlananların mention'ları (kullanıcı ID'leri) | kullanıcı | teslimden/bitişten 2 gün sonra |

TSQ Döviz & Altın tablo kullanmaz ve kullanıcı verisi tutmaz: fiyatlar yalnızca bellekte kısa süre önbelleklenir, komut
kullanımları ve sağlayıcı yanıtları kaydedilmez; sağlayıcılara kullanıcıya ait hiçbir bilgi gönderilmez.

TSQ Quote tablo kullanmaz; `/privacy export/delete` kapsamında kaydı yoktur (gönderilen alıntı görseli normal bir kanal
mesajıdır; kanaldan Discord'da silinir).

Discord ID'leri kayıpsız (64-bit) saklanır.

## Kullanıcı hakları
- `/privacy export`: yalnızca çağıranın, yalnızca o sunucudaki kayıtları (JSON).
- `/privacy delete`: önizleme → onay (5 dk, tek kullanımlık, aynı kullanıcı + aynı sunucu). Takipler ve tercihler silinir;
  **botun verdiği** bildirim rolleri geri alınır; kullanıcının önceden sahip olduğu roller korunur. Rol geri alınamazsa
  uyarı gösterilir ve arka planda yeniden denenir.

## Sunucudan çıkarılma
Bot sunucudan çıkarıldığında zaman damgası tutulur; **30 gün** (`Bot:GuildDataRetentionDays`) sonra o sunucuya ait tüm
kayıtlar silinir. Bot bu süre içinde geri eklenirse silme iptal olur.

## Yedekler
Yedekler aynı verileri içerir; işletmeci yedekleri güvenli tutmalı ve saklama süresini (öneri 14 gün) uygulamalıdır.
Silinen bir kullanıcının verisi eski yedeklerde süre dolana kadar kalabilir — gizlilik politikasında belirtilir.

## Secret'lar
Token ve API anahtarları yalnızca env/user-secrets'tan okunur; loglarda maskelenir; repo testi token/anahtar desenlerini
tarar; kaynak arşivi (`Export-Source.ps1`) yalnızca git'teki dosyaları içerir.

Kullanıcıya yönelik taslak metinler: [policies/privacy-policy.md](policies/privacy-policy.md),
[policies/terms-of-service.md](policies/terms-of-service.md) — **hukuki inceleme gerektiren taslaklardır**.
