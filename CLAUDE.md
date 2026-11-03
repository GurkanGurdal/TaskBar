# IslandBar — Proje özeti, kararlar ve kurallar

Kullanıcıyla Türkçe konuş.

Bu dosya her oturumda yüklenir; kısa tutulur. Ölçümler, denemeler, "neden böyle" açıklamaları ve tuzakların ayrıntısı
`docs/notlar.md` içinde: bir bölüme dokunmadan önce orada o konuyu ara (ör. "Hızlı ayarlar", "Genie", "Düzenler",
"Otomatik gizleme", "Test notu", "WPF notu"). Yeni karar ve kurallar buraya kısaca, ayrıntı oraya.

## Proje
- Windows 10/11 için, Windows görev çubuğunun **yerine geçen** "yüzen ada" çubuk. C# + WPF, .NET 10
  (`net10.0-windows10.0.19041.0`), ManagedShell (AppBar, görev listesi, tepsi). Kullanıcının makinesi x64, iki monitör.
- Ad IslandBar (exe, ad alanı, `IslandBar/`, `%AppData%\IslandBar`). Depo klasörü ve GitHub deposu eski adla
  (GrupBar, https://github.com/GurkanGurdal/TaskBar, dal `main`). Eski ada yalnızca `LegacyData`'da izin var.
- Ayarlar `%AppData%\IslandBar\settings.json` (`AppSettings`), kayıtlar `%LocalAppData%\IslandBar` (`crash.log`,
  `autohide.log`). Çıktı klasörü `csproj`'da sabit (Windows'la başlat kaydı exe yolunu tutuyor).

## Çalışma şekli
- Kullanıcı **Release** derlemesini kullanıyor: değişiklikten sonra çalışan IslandBar'ı kapat, Release'i derle, yeniden
  başlat. Derleme başarısızsa eski exe çalışır: sonuca ve dll saatine bak.
- **"Gönder"** = commit + push (`main`). Kullanıcı istemeden commit etme. Mesaj Türkçe, sonunda Co-Authored-By satırı.
- **Tasarım işleri:** önce web'de araştır; sonra sohbette, kullanıcının gerçek ikonları ve temanın ölçüleriyle
  (48 piksellik çubuk, ada köşesi 15, düğme 40, ikon 25) yapılmış etkileşimli kopyada 3 seçenek göster (kullanıcının
  tercihi; soyut çizimler gerçeği yansıtmadı). Seçileni uygula. Reddedilenleri yeniden önerme (aşağıda).
- Görsel değişikliği temalarda ekran görüntüsüyle sına (`tools/test/tema_gorsel.ps1`), görüntüleri sonra sil.
- Test kitaplığı `tools/test/lib.ps1` (çubuğu başlat/durdur, ayarı yedekle, UI Automation ile düğme bul, fare, ekran
  görüntüsü). `Start-Bar`/`StartBar` Debug derlemesini başlatır: testten önce Debug da derle.

## Temel kararlar
- **Explorer'a dokunma** (kapatma, shell değiştirme yok). Windows çubuğu gizlenir (`ExplorerHelper`), IslandBar AppBar
  olarak kenara yerleşir; "Windows görev çubuğunu gizle" kapalıyken tamamlayıcı mod.
- **Geri getirme her yolda garanti:** temiz kapanış, `App` çöküş işleyicileri, oturum kapanışı, `TaskbarRecovery`
  (önceki çalışma temiz kapanmadıysa açılışta geri alır), bekçi süreç (`--watchdog <pid>`; hatada yeniden başlatır,
  5 dakikada en çok 3). Ayrıntı notlarda (MVP 9).
- Her ekrana bir `BarWindow` (ana ekran önce); ekran düzeni değişince hepsi kapatılıp açılır. Tepsi, hızlı ayarlar,
  şarkı kartı, ses göstergesi yalnızca ana ekranda.
- Tasarım: ekranın ortasında tek ada (Başlat, Düzenler | uygulamalar | tepsi düğmesi, hızlı ayarlar, saat).
  Kartlar (`GlassCard`) cam, açılış/kapanış Genie. Ayrı iş parçacıklı animasyonlar, gecikme bütçesi ölçülerek korunur.
- Araçlar çubukta değil: Alt + S basılı tutulunca imlecin yerinde tekerlek (`ToolsRing`), seçilen aracın kartı orada
  (`ToolCardWindow`, `ToolsPanel.Only`).
- **Temalar:** Cam (varsayılan, `GlassCapture` ile kendi camı), Beyaz, Grafit. Palet dosyaları `Theme*.xaml`, aynı
  anahtarlar; renkler `StaticResource` (tema değişince çubuklar yeniden açılır). Sabit renk yazma, palete anahtar ekle.
  Camsız temalar `UsesGlass` false; açık zemine özgü davranış `IsLight`'a bağlı. Yeni tema: palet + `BarTheme` +
  `ThemeManager.Apply` + `SettingsWindow` listesi.
- Yeni kart eklerken öteki kartların listelerine de ekle: `CardOf`, `CardAndButton`, `ClosePanel`, `CanRetract`,
  `IsAnyPanelActive`, cam izleyici, `WarmPopupsAsync`, `PanelButton_MouseEnter`.
- Çubukta uygulama başına bir düğme (pin + pencereleri tek düğme, `TaskGroup`); eşleşme `GroupItem.Matches`.
  Gösterge "bölmeli çizgi" (`WindowMarks`), etkin uygulama soluk `ActiveBrush` zemininde, ikonların altında yumuşak gölge.
- Gruplar artık yalnızca **Düzenler** (veri hâlâ `AppSettings.Groups`): mini ekran önizlemeli kart, "Yeni düzen"
  düzenleme modunu açar, kaydet ekrandaki pencereleri yerleriyle yazar, açınca yerlerine koyar (`LayoutEditor`,
  `WindowSpot`, `WindowDocument`).
- Dikkat isteği ManagedShell'de yarım saniye sürer (`HSHELL_REDRAW` sıfırlar): `TaskGroup.WantsAttention` kullan,
  `ApplicationWindow.State`'e bakma. Otomatik gizlemede çubuk dikkat isteyen pencere ve tepsi menüsü açıkken açık kalır.
- Claude kartı: Claude Code kancası (`IslandBar.exe --claude-hook`) → adlı boru → şarkı kartının Claude kipi; kancayı IslandBar kurar (`ClaudeHookSetup`, Release'de). Bildirim
  paneli yok: paketsiz uygulama Windows bildirimlerini okuyamıyor (ayrıntı notlarda).
- Zamanlayıcı: `DispatcherTimer` boş arayüz iş parçacığında geç tetiklenebiliyor; zamanlaması önemli izleme
  `Pulse` ile (otomatik gizlemenin açılmama sorunu buydu).

## Kullanıcının tercihleri ve reddettikleri (yeniden önerme)
- Genel: gradyan, parıltı, neon, mavi-mor geçiş yok; renk yalnızca anlam taşıyan yerde. Sürekli animasyon yasak
  değil (eski "sürekli animasyon yok" notu yanlıştı, kullanıcı öyle demedi). Solmalı geçiş yerine kayma. Tam yuvarlak hap çubuk/kart yok. İki ada yok.
  Otomatik gizlemede kenarda iz bırakma.
- Saat: süs yok (güneş/ay kadranı reddedildi), LED nokta matrisi reddedildi, yan yana tarih yok (üzerine gelince),
  eşit genişlik rakam kullanma.
- Hızlı ayarlar: yuvarlak düğmeler, iki sütunlu satırlar, bento kareler reddedildi; sayfa noktaları uzamaz.
- Ses göstergesi: ince çizgili geniş kart ve sıvı dolan kaydırıcı reddedildi.
- Düzenler: kehribar klasör çizimi reddedildi; eski bölümlü büyük panel reddedildi.
- Gösterge: eski çizgi + nokta reddedildi.
- Etkin uygulama vurgusu: yalnızca soluk zemin kaldı. Reddedilenler: belirgin cam plaka, ötekileri soldurma (odak),
  gömülü yuvadan yükselen tuş, büyüyen ve/veya yükselen ikon. Seçilmeyenler: sıvı damla, adadan taşan çıkıntı,
  vizör köşeleri, etiketli hap, cam mercek, ayrılan ada, yansıma, renk izi.
- Davranış: orta tık pencereyi kapatır (Windows'tan farklı, kullanıcının seçimi). Spotify Canvas yok (resmî API yok).

## Test güvenliği (her zaman)
- Wi-Fi / Bluetooth kutularına tıklama (bağlantıyı ve Bluetooth klavyeyi keser); alt sayfalarda onay alanında yalnızca
  "Vazgeç". Güç menüsünün Kilitle / Oturumu kapat / Uyku / Yeniden başlat / Kapat öğelerine basma.
- Parola ya da kimlik bilgisi girme. IconPackager ayarlarına dokunma.
- `settings.json`'ı testten önce yedekle, `finally`'de geri koy; betik çıktısını `Select-Object -First` ile kesme
  (betik durur, `finally` çalışmaz).
- Kullanıcının Spotify'ına dokunma (sahte medya oturumu yöntemi notlarda). Ses testinde düzeyi ve sessizi sakla, geri
  koy; yüksek düzey ayarlama.
- Düzen geri yüklemeyi kullanıcının gerçek pencereleriyle sınama; test düzenini adla değil kaydın döndürdüğü grupla bul.
- Fare testinden önce ve sonra sol tuşun basılı kalmadığına bak (`GetAsyncKeyState(1)`), basılıysa bırak.
- Ekran görüntülerine arkadaki pencereler (e-posta vb.) girebilir: işin bitince sil. Ekran görüntüsü aracı testleri
  Resimler'e dosya yazar ve panoyu değiştirir: yenileri sil, panoyu geri koy.
- Notepad kullanma (sekmeyi oturumunda saklıyor). Tarayıcı testinde Edge `--inprivate`.
- Çubuğun kenarını değiştirmek ve ekran modunu değiştirmek kullanıcının pencerelerini taşır: yapma.
- Ekranı tam kaplayan test penceresi tam ekran sayılır, çubuk gizlenir: biraz kısa aç.

## Sık düşülen tuzaklar (ayrıntı notlarda)
- Aynı CLSID için iki `[ComImport]` sınıf tanımlama (sessiz kapanma). Tek tür: `AudioEndpoint.MMDeviceEnumerator`.
- `Visibility=Collapsed` başlayan `IconButton`'ın Loaded animasyonu uygulamayı kapatır: gizleme, ağaçtan çıkar.
- Kapalı Popup'ta yerleşim askıda; boyutu açtıktan sonra ölç. Kapatırken `IsOpen = false` değil `ClosePopup`.
- `GlassCapture.AddTransient` yalnızca Genie kopyası gibi kısa ömürlü pencereler için.
- Stildeki `RenderTransform` dondurulur; animasyonlu dönüşümü şablonda ya da öğede tanımla.
- Kart açıkken çubuk kapatılırsa `PointToScreen` hata verir: `ClosePopup` `PresentationSource`'a bakar.
- Hata kaydı `crash.log`; çubuk kendiliğinden kapanırsa önce oraya bak.

## Bilinen eksikler
Balon bildirimleri.
Gerçek Explorer yeniden başlatması, monitör takıp çıkarma, yan/üst çubukta son görsel değişiklikler denenmedi.
