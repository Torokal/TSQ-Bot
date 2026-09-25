# Gizlilik ve veri saklama (teknik kayıt)

TSQ Bot mesaj içeriği okumaz (Message Content intent kapalı), üye listesi indirmez (Guild Members intent kapalı),
presence izlemez. Profil, avatar, kullanıcı adı **saklanmaz**.

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
