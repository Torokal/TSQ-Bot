# ADR-0005 — Slash komut şeması ve kaydı

- Durum: Kabul edildi (2026-09-24)

## Karar
- Şema üretimi kayıttan ayrıdır. Manifest, Discord.Net'in **public** `ModuleInfo`/`SlashCommandInfo` bilgilerinden
  çevrimdışı üretilir (Discord.Net'in kendi dönüştürücüsü `internal`); `docs/commands.manifest.json` olarak commit'lenir ve
  test kodla eşitliğini doğrular.
- Doğrulayıcı: Discord limitleri (ad regex, açıklama 1–100, ≤25 seçenek/choice, iç içe grup kuralı, 8000 karakter,
  ≤100 komut) + TSQ Bot politikası (yalnızca guild bağlamı/kurulumu, her açıklamada `tr` yerelleştirmesi, yönetici
  komutlarında `default_member_permissions`, kullanıcı komutlarında yok).
- Kayıt yalnızca `commands sync` CLI'si ile yapılır; **Ready/reconnect'te otomatik kayıt yok**. Varsayılan dry-run.
  Engelleyen koşullar: geçersiz/boş/eksik yüklenmiş manifest, token başka uygulamaya ait, guild allow-list'te değil,
  global için ayrı onay bayrağı. Guild'de ada göre upsert (Discord create=upsert); uygulamanın oluşturmadığı komutlar
  korunur, oluşturduğu ama artık tanımlı olmayanlar yalnızca `--prune` ile silinir.
- Modül kapatmak komut kaydını değiştirmez (kayıt kapsamı ≠ guild bazlı çalışma zamanı durumu).
- Yetki: `default_member_permissions` yalnızca menü görünürlüğüdür; her handler ve component işlemi sunucu tarafında
  `ActorContext` ile yeniden yetkilendirilir.
- Açıklamaların temel dili İngilizce (fallback), `tr` yerelleştirmesi zorunlu; yanıt dili sunucu ayarından (varsayılan tr).
