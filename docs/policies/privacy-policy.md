# TSQ Bot — Gizlilik Politikası (TASLAK)

> Taslaktır; yayımlamadan önce işletmeci bilgileriyle doldurulmalı ve hukuken gözden geçirilmelidir.
> Son güncelleme: 2026-09-24. İşletmeci: `<ad / iletişim>`.

**TSQ Bot** bağımsız bir Discord botudur; BOT Greg'in resmî devamı değildir.

## Hangi verileri işliyoruz
- **Sunucu ayarları**: sunucu, kanal ve rol kimlikleri; dil ve zaman dilimi tercihi; hangi modüllerin açık olduğu;
  bildirim filtreleri; ayarı son değiştiren yöneticinin Discord kullanıcı kimliği.
- **Kişisel tercihleriniz** (yalnızca siz kullanırsanız): takip ettiğiniz takımlar, sonuçları spoiler olarak görme
  tercihiniz ve botun size verdiği bildirim rollerinin kaydı.
- **Doğum gününüz** (`/birthday set` ile siz ya da bir sunucu yöneticisi — Administrator — kaydederse, TSQ Doğum Günü
  modülü açıksa): yalnızca gün ve ay — doğum yılı sorulmaz ve saklanmaz — ile kutlama/rol kaydı. Doğum gününüzde duyuruda
  etiketlenirsiniz. Kendi kaydınızı `/birthday show` ile görürsünüz; sunucunun Administrator yetkili yöneticileri veya
  sunucu sahibi yalnızca sizin kaydınızı tek tek görebilir (`/tsq-admin modul:birthday islem:show`, kayıt altına alınır). Doğum günlerinin
  toplu veya herkese açık bir listesi yoktur. `/birthday remove` veya `/privacy delete` ile silinir.
- **Randomizer komutları** (`/zarat`, `/randomsayi`, `/sec`, `/yazitura`; TSQ Randomizer modülü açıksa): yazdığınız girdi
  ve sunucudaki görünen adınız yalnızca o anki sonuç kartı için işlenir; sonuçlar, seçenek metinleri ve adınız saklanmaz ve
  kayıt (log) dosyalarına yazılmaz.
- **Okumadığımız veriler**: mesaj akışları, üye listeleri, çevrimiçi durum, profil bilgileri. Tek istisna `/quote`
  (TSQ Quote, modül açıksa): bir üye açıkça istediğinde yalnızca o tek mesajın metni ve yazarının görünen adı, kullanıcı
  adı ve profil fotoğrafı istek sırasında bir kez işlenir ve alıntı görseli olarak kanala gönderilir; mesaj içeriği ve
  profil bilgisi kalıcı olarak depolanmaz ve kayıt (log) dosyalarına yazılmaz.
- **Herkese açık esports verisi**: PandaScore (varsayılan), isteğe bağlı olarak Liquipedia (CC BY-SA 3.0) ve Valve Regional Standings kaynaklarından maç, turnuva ve
  sıralama bilgileri. Bunlar kişisel veri değildir.

## Amaç
Yalnızca sunucunuzun istediği esports bildirimlerini göndermek, komutlara yanıt vermek ve talep ettiğiniz bildirim
rollerini yönetmek. Veriler satılmaz, reklam veya profil çıkarma için kullanılmaz.

## Saklama
- Kişisel tercihleriniz siz silene veya bot sunucudan çıkarıldıktan sonraki 30 gün dolana kadar saklanır.
- Bot bir sunucudan çıkarıldığında o sunucunun tüm verileri 30 gün sonra silinir.
- Yedekler en fazla `<14>` gün tutulur; silinen veriler bu süre sonunda yedeklerden de kalkar.

## Haklarınız
- `/privacy export`: bu sunucuda sizinle ilgili kayıtların bir kopyası.
- `/privacy delete`: onayınızla bu sunucudaki kayıtlarınızın silinmesi; botun size verdiği bildirim rolleri geri alınır,
  önceden sahip olduğunuz roller etkilenmez.
- Diğer talepler: `<iletişim>`.

## Güvenlik
Erişim anahtarları kod deposunda tutulmaz; kayıtlar tek bir sunucuda yerel veritabanında saklanır.

## Kaynak kod
Bot AGPL-3.0 lisanslıdır; çalışan sürümün kaynak kodu `/bot source` komutuyla gösterilen adrestedir.
