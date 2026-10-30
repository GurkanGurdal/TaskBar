# GrupBar — Proje Özeti ve Kararlar

Bu dosya, projenin planlandığı sohbetten aktarılan bağlamdır. Kullanıcıyla Türkçe konuş.

## Fikir
Windows için, RetroBar gibi Windows görev çubuğunun **yerine geçen** özelleştirilmiş bir görev çubuğu.
Ana özellik: benzer programları çubukta **gruplar/klasörler** halinde toplamak
(tıklayınca içindeki uygulamaları gösteren küçük bir panel açılır).

Kullanıcı uygulamayı kapattığında (ya da uygulama çöktüğünde) her şey eski haline dönmeli.

## Alınan kararlar
- **Platform:** Sadece Windows (10/11). Kullanıcının makinesi Windows x64.
- **Teknoloji:** C# + WPF, .NET 10 (LTS, Kasım 2028'e kadar destekli; .NET 8 ve 9'un desteği
  10 Kasım 2026'da bitiyor). AppBar, görev listesi ve bildirim alanı için ManagedShell kütüphanesi
  (RetroBar'ın kullandığı, Apache-2.0 lisanslı .NET kütüphanesi) kullanılacak.
- **Explorer'a dokunma:** Explorer'ı kapatmak veya shell'i değiştirmek yok.
- **Varsayılan mod — Windows çubuğunun yerine geçme:** Windows görev çubuğu gizlenir,
  GrupBar ekranın bir kenarına (alt/üst/sol/sağ, ayarlanabilir) AppBar API'si
  (`SHAppBarMessage`) ile yerleşir, böylece pencereler onun altına girmez.
- **İsteğe bağlı mod — tamamlayıcı çubuk:** Ayarlardan açılır; Windows görev çubuğu açık kalır,
  GrupBar yanında ek çubuk olarak durur.
- **Windows çubuğunu gizleme ayrıntıları:**
  - `Shell_TrayWnd` ve `Shell_SecondaryTrayWnd` pencereleri `ShowWindow(SW_HIDE)` ile gizlenir.
  - Gizlenen çubuğun kapladığı çalışma alanı kodla geri verilmeli (`SystemParametersInfo SPI_SETWORKAREA`
    veya AppBar ile), yoksa tam ekran pencerelerin altında boşluk kalır.
  - Explorer yeniden başlarsa (`TaskbarCreated` mesajı) veya Windows çubuğu tekrar gösterirse
    periyodik kontrolle tekrar gizlenir.
  - **Geri getirme her yolda garanti olmalı:** normal kapanış, `AppDomain.UnhandledException`,
    `Dispatcher.UnhandledException`, oturum kapanışı (`SessionEnding`). Ek güvenlik olarak
    uygulama her açılışta önce çubuğu gösterir; mümkünse küçük bir "bekçi" süreç ana süreç
    ölünce çubuğu geri getirir.
  - Gizleme bir Windows güncellemesiyle çalışmazsa, kullanıcıya otomatik gizlemeyi açmasını öner
    (RetroBar'ın yaklaşımı).

## İlk sürüm (MVP) kapsamı
1. Ekran kenarına yerleşen AppBar penceresi (kenar ve kalınlık ayarlanabilir).
2. **Gruplar:** ad + ikon, içine .exe / .lnk / klasör / Store uygulaması eklenebilir.
   Tıklayınca açılan panel; "hepsini aç" butonu; sürükle-bırak ile ekleme.
3. Sabitlenmiş tekil uygulamalar.
4. Çalışan pencerelerin listesi (`EnumWindows`, görünür ve sahipsiz üst seviye pencereler),
   tıklayınca öne getirme / küçültme.
5. Saat.
6. Ayarlar penceresi; ayarlar ve gruplar `%AppData%\GrupBar\settings.json` içinde JSON.
7. Sistem tepsisi ikonu: Ayarlar / Çıkış.
8. "Windows'la başlat" seçeneği (HKCU Run anahtarı).
9. Windows görev çubuğunu gizleme ve her yolda geri getirme (yukarıdaki ayrıntılar).
10. Başlat düğmesi (Windows'un kendi Başlat menüsünü açar).
11. Bildirim alanı (tray ikonları).

Windows çubuğu gizlendiği için 9-11 MVP'ye alındı; yoksa Başlat ve tray ikonlarına erişilemez.
Atlama listeleri ve çoklu monitör ince ayarları **ikinci aşamaya** bırakıldı.

## Dikkat edilecekler
- Per-monitor DPI farkındalığı (app.manifest: `PerMonitorV2`).
- Tam ekran uygulamalarda (oyunlar) çubuk üste çıkmamalı (`ABN_FULLSCREENAPP`).
- Uygulama kapanırken AppBar kaydı mutlaka silinmeli (`ABM_REMOVE`).
- İmzasız exe SmartScreen uyarısı verir; dağıtım aşamasında ele alınacak.

## Benzer projeler (inceleme için)
- RetroBar (dremin/RetroBar) — C#/.NET, ManagedShell kullanır, Windows'un yanında çalışır.
- Taskbar Groups / Taskbar Groups Fluent — sadece grup özelliği, kısayol yaklaşımı.
- Windhawk — Windows görev çubuğunu modlarla değiştirir (farklı yaklaşım).

## Durum
Proje iskeleti kuruldu: `GrupBar.sln` + `GrupBar/` (WPF, `net10.0-windows`, `PerMonitorV2` manifest).
MVP 1 tamam: `BarWindow` (ManagedShell `AppBarWindow`), sağ tık menüsünden kenar / kalınlık / çıkış;
kenar ve kalınlık `settings.json`'a yazılıyor (`AppSettings`). Tray servisi `App` içinde şimdilik kapalı (MVP 11).
MVP 2 tamam: `AppGroup` / `GroupItem` (`settings.json` içinde), tıklayınca açılan panel, "Hepsini aç",
gruba sürükle-bırak, Ekle menüsü (dosya / klasör / `StoreAppPicker`), sağ tıkla ad / ikon / silme.
Store öğeleri `shell:AppsFolder\<AUMID>` yoluyla saklanıyor. Panel `StaysOpen=True` bir Popup; pencere etkinliğini kaybedince kapanıyor.
MVP 3 tamam: `AppSettings.Pinned` (`GroupItem` listesi), çubukta grupların önünde ikon düğmeleri; çubuğun boş yerine sürükle-bırak
ya da sağ tık > Uygulama sabitle ile ekleme, sağ tıkla kaldırma. Sıralama değiştirilemiyor (eklenme sırası).
MVP 4 tamam: ManagedShell `Tasks.GroupedWindows` (EnumWindows yerine; açılan/kapanan pencereleri ve başlık değişimini kendisi izliyor),
`TaskPanel` düğmeleri eşit daraltıyor, tıklayınca öne getirme / küçültme, sağ tıkla pencereyi kapatma. Etkin pencere vurgulu.
Görev servisinin yan etkileri (MVP 9 bekçisi hesaba katmalı): çalışırken `Shell_TrayWnd` üzerindeki `TaskbandHWND` özelliği GrupBar'ı gösterir,
yani Windows çubuğu ilerleme / rozet (`ITaskbarList`) almaz. Temiz kapanış ve yakalanan çöküşlerde geri dönüyor; süreç zorla öldürülürse
ölü pencereyi gösterir kalır, sonraki temiz kapanışta düzelir. `SPI_SETMINIMIZEDMETRICS` genişliği 140 olur ve geri alınmaz (oturumluk, görünür etkisi yok).
MVP 5 tamam: saat çubuğun sonunda (Windows bölge ayarıyla saat ve tarih; dikey çubukta yalnızca saat, tarih ipucunda).
MVP 6 tamam: `SettingsWindow` (çubuk menüsünden "Ayarlar..."): Genel sekmesinde kenar, kalınlık, saat / tarih; Düzen sekmesinde sabitlenenleri ve grupları
sıralama, kaldırma, yeniden adlandırma. Değişiklikler anında uygulanır. `AppSettings` artık `INotifyPropertyChanged`; `BarWindow.Settings_PropertyChanged` uygular ve kaydeder.
MVP 7 tamam: `TrayIcon` (WinForms `NotifyIcon`; sağ tık Ayarlar / Çıkış, sol tık Ayarlar), `GrupBar.ico` aynı zamanda exe ikonu.
Windows yeni ikonları varsayılan olarak taşma menüsünde (^) gösterir.
MVP 8 tamam: `StartupRegistration` (HKCU Run, durumun kaynağı kayıt defteri; Görev Yöneticisi'nden kapatma `StartupApproved` ile okunuyor),
Ayarlar > Genel'de "Windows'la başlat" kutusu, varsayılan kapalı. Kayıt tam exe yolunu tutar; exe taşınırsa kutu kapalı görünür, yeniden işaretlenmeli.
Tek örnek: `App` kurucusunda ShellManager'dan önce adlı mutex; ikinci kopya ilkine "Ayarları aç" sinyali gönderip çıkıyor.
MVP 10 tamam: Başlat düğmesi (`ShellHelper.ShowStartMenu`, sağ tık Win+X). Başlat menüsü bu makinede `SearchHost` sürecinde; tıklama menüyü zaten kapattığı için açıklık, basmadan önce fare hareketinde okunuyor.
MVP 11 tamam: `TrayList` (`NotificationArea.AllIcons`), tüm ikonlar görünür, taşma menüsü yok. Balon bildirimleri henüz gösterilmiyor (`NotificationBalloonShown` işlenmiyor).
Tepsi servisinin yan etkileri: gizli bir `Shell_TrayWnd` açıp Shell_NotifyIcon ve SHAppBarMessage mesajlarını alıyor, Windows çubuğunu en alta indiriyor (topmost kalkıyor) ve `TaskbandHWND`'yi ondan siliyor.
`App.ReleaseShell` temiz kapanış ve iki çöküş yolunda hepsini geri alıyor (topmost kodla geri veriliyor). Süreç zorla öldürülürse Windows çubuğu topmost olmadan ve `TaskbandHWND` olmadan kalıyor, çalışırken eklenen tepsi ikonları Explorer'da görünmüyor; sonraki temiz kapanış düzeltiyor.
MVP 9 tamam: Windows çubuğu ManagedShell `ExplorerHelper.HideExplorerTaskbar` ile gizleniyor (otomatik gizlemeyi de açar; 100 ms'de bir yeniden gizler). Ayarlar > Genel'de "Windows görev çubuğunu gizle" (varsayılan açık; kapalıyken tamamlayıcı mod).
`TaskbarRecovery`: ShellManager'dan önce `%AppData%\GrupBar\taskbar-state.txt` içine özgün ABState yazılır, temiz kapanışta silinir. Dosya varsa önceki çalışma temiz kapanmamıştır, sonraki açılış geri alır.
Bekçi süreci: aynı exe `--watchdog <pid>` (App kurucusunun başında yakalanır). Ana süreç ölünce görünürlük, ABState, topmost, `TaskbandHWND` ve `TaskbarCreated` yayınını geri alır; 1,5 sn sonra topmost'u yeniden verir (Explorer temiz kapanışta da çubuğu indiriyor). HWND_TOPMOST tek başına etkisiz kalıyor, önce HWND_NOTOPMOST gerekiyor.
Explorer yeniden başlarsa (`TaskbarCreated` ve Windows çubuğu tutamacı değiştiyse) `BarWindow.HandleTaskbarCreated` AppBar kaydını ve görev servisini yeniler. Bu yol yalnızca benzetimle denendi, gerçek Explorer yeniden başlatması denenmedi.
Çoklu monitör (kullanıcının makinesinde 2 monitör var; `ExplorerHelper` ikincil Windows çubuklarını da gizlediği için öne alındı): `App` her ekrana bir `BarWindow` açar (`AppBarScreen.FromAllScreens`, ana ekran önce). Sabitlenenler, gruplar, saat ve Başlat her çubukta; pencere düğmeleri pencerenin ekranındaki çubukta (`HMonitor` filtresi); tepsi yalnızca ana ekranda. `SetScreenProperties` -> `App.NotifyDisplayChange`: ekran düzeni değiştiyse tüm çubuklar kapatılıp açılır. Ayarlar penceresi ve kaydetme `App`'te, her çubuk yalnızca kendine uygular. Başlat menüsünün hangi ekranda açılacağını Windows seçer.
Test notu: PowerShell 5.1'de `FindWindow($null, ...)` `$null`'u boş metne çevirir ve pencere bulunamaz; başlıkla aramak için EnumWindows kullan. UI Automation `Invoke` çubuğa önce odak verdiği için `PreviewMouseLeftButtonDown` çalışmaz; öndeki pencereyi küçültme yolu UIA ile ölçülemez.
Otomatik gizleme: `AppSettings.AutoHideBar` (varsayılan kapalı; Ayarlar > Genel ve çubuk menüsü), ManagedShell `AppBarMode.AutoHide` ile. `BarRoot` tamamen ekran dışına kayar (`AutoHideShowMargin = 0`, kullanıcı kenarda iz istemedi); imleç konumu 70 ms'de bir okunur (`UpdateCursorZone`), ekranın en kenarındaki 2 piksel açar, açıkken adanın çevresindeki tüm şerit açık tutar. AppBar kaydı olmadığı için çalışma alanı ayrılmaz. Grup paneli ve (ana ekranda) Başlat açıkken gizlenmez.
Tasarım "yüzen ada": pencere saydam (`AllowsTransparency` hep açık), tek ada ekranın ortasında süzülür (kullanıcı iki ada istemedi): Başlat, sabitlenenler, gruplar, pencereler, ardından tepsi düğmesi, pil ve saat. Tepsi ikonlarından yalnızca pil (`NotificationArea.POWER_GUID`) çubukta; gerisi "Diğer simgeler" düğmesinin açtığı `TrayPopup` kartında (ikisi de `TrayIcons` üzerinde `ListCollectionView`). Pencere = `Thickness` + 2 x `FloatGap`; ayrılan alan da budur, adanın kendi yüksekliği `Thickness`. Renkler ve menü/ipucu stilleri `Theme.xaml`'da. Grup simgesi ilk dört öğeden ızgara (`AppGroup.PreviewItems`). Pencere düğmeleri yalnızca ikon, başlık ipucunda.
Cam (Apple Liquid Glass'a benzer, kullanıcının isteği): Windows'un hazır bulanıklığı kullanılmıyor, miktarı sabit ve masaüstündeki saat bileşenini tamamen siliyor; bulanıklıksız hali de okunmuyor. `GlassCapture` adanın arkasındaki ekranı alır, küçültür, doygunlaştırır (`Saturation`), kenarlarda kırar (`Refract`). `GlassBrush` ile çizilir, bulanıklık miktarı XAML'daki `BlurEffect Radius`. Üstünde parlaklığa göre değişen ton (`ApplyTint`, `TintOnDark` / `TintOnBright`), parlama ve kenar ışığı var. Gölge adanın dışına kırpılı (`ShadowHost`). Görüntü `Image` değil `Rectangle` + `ImageBrush`: `Image` kaynak boyutunu adaya dayatıyor.
Görüntü alma: ada ekran dışındayken (açılırken) doğrudan; görünürken `WDA_EXCLUDEFROMCAPTURE` ile bir an hariç tutularak (Windows 10 2004+). Hariç tutma ekran görüntülerini de etkiler ve durağan ekranda kapatınca kalkmaz: `ForceRecompose` şart, yoksa ada ekran görüntülerinin %71'inde çıkmıyordu. Bu yüzden görünürken yalnızca adanın iki yanındaki parlaklık değişince (200 ms'de bir bakılır) ya da 15 saniyede bir alınır.
Adanın ekran dışında olup olmadığı `IsIslandOffScreen` ile gerçek konumdan okunur, animasyon olaylarından bayrak tutulmaz: gizlenme yarıda kesilince tamamlandı olayı yine de geliyordu, ada ekrandayken gizli sanılıp hariç tutulmadan görüntü alınıyor, cam kendi simgelerini gösteriyordu (ok düğmesi `AllowAutoHide` üzerinden açılış animasyonunu tetiklediği için orada görülüyordu).
Tepsi kartı ve grup paneli de aynı camdan: `GlassCard` (görünüm `Theme.xaml`, ton `GlassTint`). Kart stilde görünmez başlar; açılınca kendi penceresi hariç tutularak arkası alınır, görüntü hazır olunca `AnimatePopupIn` ile belirir. Açıkken adanın tazelendiği anlarda birlikte tazelenir. Görüntü alınamazsa (eski Windows) `PopupFill` zemini görünür. Sağ tık menüleri ve ipuçları cam değil, düz `PopupFill`.
Pencere gruplama: çubukta pencere başına değil uygulama başına düğme (`TaskGroup`, `BarWindow.SyncTaskGroups`). Anahtar Windows'taki gibi `AppUserModelID`, yoksa exe yolu; düğmeler yer değiştirmez, yeni uygulama sona eklenir. Birden çok pencerede göstergenin yanında nokta var. Tek pencerede tıklama öne getirir / küçültür; çok pencerede sıradakine geçer (hiçbiri etkin değilse en son etkin olana). Sağ tık "Tüm pencereleri kapat".
Önizleme: düğmenin üzerinde 350 ms durunca `PreviewPopup` açılır, açıkken düğmeler arasında beklemeden geçer, imleç ayrılınca 250 ms sonra kapanır; kart açıkken çubuk gizlenmez. Küçük resimler `WindowThumbnail` (`DwmRegisterThumbnail`): görüntüyü Windows çizer, WPF saydamlığı ve dönüşümü işlemez, her şeyin üstünde durur. Bu yüzden önizleme kartı ölçeklenmez ve küçük resimler kart belirdikten sonra açılır (`WindowThumbnail.IsShown`, kendi soluşu DWM opaklığıyla). Saydam Popup üzerinde de çalışıyor, küçültülmüş pencerenin görüntüsü de geliyor. Tarayıcı sekmeleri ayrı pencere olmadığı için sekme başına önizleme yok. Aero Peek (kutunun üzerine gelince pencereyi tam boy gösterme) yok.
Test notu: Gerçek düğme davranışı için `SetCursorPos` + `mouse_event` kullan; UI Automation `Invoke` etkin pencereyi değiştirdiği için pencereler arası geçiş ölçülemez. Kapat düğmesi kutunun üzerine gelinmeden `Hidden`, UI Automation ağacında görünmez.
Görsel test notu: alan ayrılan modda Windows pencereleri çubuğun altından çeker, camın arkasını görmek için otomatik gizleme modunda ölç. Kullanıcının masaüstünde adanın tam arkasına gelen bir saat bileşeni var; koyu duvar kâğıdı üstünde bunu da dene.
WPF notları: stildeki `RenderTransform` dondurulur, animasyon için şablon veya öğe üzerinde tanımla. Kök `AppBarWindow`'a `x:Name` verilemez. Popup'ın doğrudan çocuğundaki `Margin` konuma katılmıyor; gölge payı için dışta `Padding`li bir sarmalayıcı kullan.
Bilinen eksik: balon bildirimleri; otomatik gizlemede pencere dikkat isteyince (`Flashing`) ve tepsi menüsü açıkken çubuk kendiliğinden görünür kalmıyor. Kullanıcının kendi Explorer yeniden başlatması ve gerçek monitör takıp çıkarma denenmedi.
İlk sürüm MVP listesi tamam. Git deposu yok.
