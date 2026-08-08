using System.Collections.Generic;

namespace CaYaTunnel.Ui;

/// <summary>
/// Every user-visible string in both applications, in English and Turkish.
/// Keys are grouped by screen; add to both languages or the fallback shows English.
/// </summary>
public static class Strings
{
    public static readonly IReadOnlyDictionary<string, LocalisedString> Table = new Dictionary<string, LocalisedString>
    {
        // ---- Shared chrome ----
        ["AppClient"] = new("CaYaTunnel Client", "CaYaTunnel İstemci"),
        ["AppServer"] = new("CaYaTunnel Server", "CaYaTunnel Sunucu"),
        ["Minimise"] = new("Minimise", "Simge durumuna küçült"),
        ["Close"] = new("Close", "Kapat"),
        ["Cancel"] = new("Cancel", "İptal"),
        ["Save"] = new("Save", "Kaydet"),
        ["Create"] = new("Create", "Oluştur"),
        ["Delete"] = new("Delete", "Sil"),
        ["Remove"] = new("Remove", "Kaldır"),
        ["Copy"] = new("Copy", "Kopyala"),
        ["Copied"] = new("Copied to clipboard", "Panoya kopyalandı"),
        ["Refresh"] = new("Refresh", "Yenile"),
        ["Rename"] = new("Rename", "Yeniden adlandır"),
        ["Enable"] = new("Enable", "Etkinleştir"),
        ["Disable"] = new("Disable", "Devre dışı bırak"),
        ["Yes"] = new("Yes", "Evet"),
        ["No"] = new("No", "Hayır"),
        ["Open"] = new("Open", "Aç"),
        ["OpenWindow"] = new("Open CaYaTunnel", "CaYaTunnel'ı aç"),
        ["ExitApp"] = new("Exit", "Çık"),
        ["StillRunningTitle"] = new("Still running", "Çalışmaya devam ediyor"),
        ["StillRunningBodyClient"] = new(
            "CaYaTunnel is in the tray and your tunnels stay up. Right-click the icon to exit.",
            "CaYaTunnel sistem tepsisinde ve tünellerin açık kalmaya devam ediyor. Çıkmak için ikona sağ tıkla."),
        ["StillRunningBodyServer"] = new(
            "The gateway is in the tray and keeps serving tunnels. Right-click the icon to exit.",
            "Ağ geçidi sistem tepsisinde ve tünelleri sunmaya devam ediyor. Çıkmak için ikona sağ tıkla."),
        ["Browse"] = new("Browse…", "Gözat…"),
        ["Never"] = new("Never", "Hiç"),
        ["Unknown"] = new("Unknown", "Bilinmiyor"),
        ["Working"] = new("Working…", "Çalışıyor…"),

        // ---- Navigation ----
        ["NavTunnels"] = new("Tunnels", "Tüneller"),
        ["NavDevices"] = new("Devices", "Cihazlar"),
        ["NavSettings"] = new("Settings", "Ayarlar"),
        ["NavOverview"] = new("Overview", "Genel Bakış"),
        ["NavClients"] = new("Client Builds", "İstemci Paketleri"),
        ["NavLog"] = new("Activity", "Etkinlik"),

        // ---- Connection state ----
        ["StateOffline"] = new("Offline", "Çevrimdışı"),
        ["StateConnecting"] = new("Connecting", "Bağlanıyor"),
        ["StateAuthenticating"] = new("Authenticating", "Kimlik doğrulanıyor"),
        ["StateOnline"] = new("Online", "Çevrimiçi"),
        ["StateReconnecting"] = new("Reconnecting", "Yeniden bağlanıyor"),
        ["StateUnauthorized"] = new("Not authorised", "Yetkisiz"),
        ["Connect"] = new("Connect", "Bağlan"),
        ["Disconnect"] = new("Disconnect", "Bağlantıyı kes"),
        ["Latency"] = new("Latency", "Gecikme"),
        ["ThisDevice"] = new("This device", "Bu cihaz"),

        // ---- Tunnels ----
        ["TunnelsTitle"] = new("Tunnels", "Tüneller"),
        ["TunnelsSubtitle"] = new(
            "Every tunnel on this server, from every device.",
            "Bu sunucudaki tüm cihazlara ait tüm tüneller."),
        ["NewTunnel"] = new("New tunnel", "Yeni tünel"),
        ["NoTunnels"] = new("No tunnels yet", "Henüz tünel yok"),
        ["NoTunnelsHint"] = new(
            "Create one to publish a local or LAN service through the gateway.",
            "Yerel veya ağ üzerindeki bir servisi ağ geçidi üzerinden yayınlamak için bir tane oluştur."),
        ["ShowAllDevices"] = new("All devices", "Tüm cihazlar"),
        ["ShowThisDevice"] = new("This device only", "Yalnızca bu cihaz"),
        ["PublicEndpoint"] = new("Public address", "Genel adres"),
        ["Target"] = new("Target", "Hedef"),
        ["Device"] = new("Device", "Cihaz"),
        ["Traffic"] = new("Traffic", "Trafik"),
        ["Connections"] = new("Connections", "Bağlantılar"),
        ["ActiveNow"] = new("active now", "şu an aktif"),
        ["LastActive"] = new("Last used", "Son kullanım"),
        ["TunnelDisabled"] = new("Disabled", "Devre dışı"),
        ["DeviceOffline"] = new("Device offline", "Cihaz çevrimdışı"),
        ["Edit"] = new("Edit", "Düzenle"),
        ["EditTunnelTitle"] = new("Edit tunnel", "Tüneli düzenle"),
        ["TunnelEnabled"] = new("Enabled", "Etkin"),
        ["PublicAddressFixed"] = new(
            "The public address cannot be changed here — that would be a different endpoint, and anyone already using this one would lose it. Delete and create a new tunnel instead.",
            "Genel adres burada değiştirilemez — bu farklı bir adres olurdu ve mevcut adresi kullananlar erişimini kaybederdi. Bunun yerine sil ve yeni bir tünel oluştur."),

        // ---- Connection test ----
        ["TestTunnel"] = new("Test", "Test et"),
        ["Testing"] = new("Testing…", "Test ediliyor…"),
        ["TestTargetOk"] = new("The local service answered.", "Yerel servis yanıt verdi."),
        ["TestTargetFailed"] = new(
            "Could not reach the local service. It is the target that is down, not the tunnel.",
            "Yerel servise ulaşılamadı. Sorun tünelde değil, hedef serviste."),
        ["TestPublicOk"] = new("The public address is reachable.", "Genel adrese erişilebiliyor."),
        ["TestPublicFailed"] = new(
            "The public address did not answer. Check that the port is open on the server's firewall and that DNS points at it.",
            "Genel adres yanıt vermedi. Portun sunucunun güvenlik duvarında açık olduğunu ve DNS'in oraya işaret ettiğini kontrol et."),
        ["TestPublicSkipped"] = new(
            "The public address was not tested because the local service is down — it would fail for that reason alone.",
            "Yerel servis çalışmadığı için genel adres test edilmedi — yalnızca bu yüzden başarısız olurdu."),
        ["TestRouted"] = new(
            "Traffic reached the local service through the tunnel end to end.",
            "Trafik tünel üzerinden uçtan uca yerel servise ulaştı."),
        ["TestWrongTarget"] = new(
            "The gateway answered but the traffic did not reach this tunnel's service. Check the hostname or port.",
            "Ağ geçidi yanıt verdi ama trafik bu tünelin servisine ulaşmadı. Alan adını veya portu kontrol et."),
        ["TestOfflineDevice"] = new(
            "The device carrying this tunnel is offline, so nothing can reach it.",
            "Bu tüneli taşıyan cihaz çevrimdışı, bu yüzden ona hiçbir şey ulaşamaz."),

        ["ConfirmDeleteTunnel"] = new(
            "Delete this tunnel? Its public address stops working immediately.",
            "Bu tünel silinsin mi? Genel adresi anında çalışmayı bırakır."),

        // ---- New tunnel dialog ----
        ["NewTunnelTitle"] = new("New tunnel", "Yeni tünel"),
        ["KindHttp"] = new("Website", "Web sitesi"),
        ["KindHttpHint"] = new(
            "Shares port 443 with other sites. Reached by hostname.",
            "443 portunu diğer sitelerle paylaşır. Alan adıyla erişilir."),
        ["KindMinecraft"] = new("Minecraft", "Minecraft"),
        ["KindMinecraftHint"] = new(
            "Shares one port with other Minecraft servers, split by hostname.",
            "Tek portu diğer Minecraft sunucularıyla paylaşır, alan adına göre ayrılır."),
        ["KindTcp"] = new("Any other service", "Diğer servisler"),
        ["KindTcpHint"] = new(
            "Gets a public port of its own, over TCP, UDP or both.",
            "Kendine ait bir genel port alır; TCP, UDP veya ikisi birden."),

        // ---- Transports ----
        ["Transports"] = new("Protocol", "Protokol"),
        ["TransportTcp"] = new("TCP", "TCP"),
        ["TransportUdp"] = new("UDP", "UDP"),
        ["TransportBoth"] = new("TCP + UDP", "TCP + UDP"),
        ["TransportsHint"] = new(
            "Both is the usual choice for game servers, which listen on the same port number for each.",
            "Oyun sunucuları genelde aynı port numarasını ikisi için de dinler; bu durumda ikisini birden seç."),
        ["UdpNote"] = new(
            "UDP travels over the same reliable link, so a lossy connection costs latency rather than dropped packets.",
            "UDP aynı güvenilir bağlantı üzerinden taşınır; paket kaybı olan bir hatta paket düşmek yerine gecikme artar."),

        // ---- HTTP access ----
        ["RewriteHost"] = new("Present the target's own address to the service", "Servise kendi adresini göster"),
        ["RewriteHostHint"] = new(
            "Turn on if the service answers 400 or 403 through the tunnel but works locally. Many local-only tools reject requests whose Host header is not localhost, as protection against DNS rebinding. Leave off otherwise: most web apps use the real Host to build their own links.",
            "Servis yerelde çalışıp tünel üzerinden 400 veya 403 veriyorsa aç. Birçok yerel araç, DNS rebinding koruması olarak Host başlığı localhost olmayan istekleri reddeder. Diğer durumlarda kapalı bırak: çoğu web uygulaması kendi bağlantılarını gerçek Host'a göre üretir."),
        ["HttpAccess"] = new("Reachable over", "Erişim şeması"),
        ["HttpAccessBoth"] = new("HTTP and HTTPS", "HTTP ve HTTPS"),
        ["HttpAccessHttpsOnly"] = new("HTTPS only", "Yalnızca HTTPS"),
        ["HttpAccessHttpOnly"] = new("HTTP only", "Yalnızca HTTP"),
        ["HttpAccessRedirect"] = new("Redirect HTTP to HTTPS", "HTTP'yi HTTPS'e yönlendir"),
        ["HttpAccessHint"] = new(
            "The gateway always listens on both ports; this decides what this hostname does with each.",
            "Ağ geçidi her iki portu da dinler; bu ayar bu alan adının her biriyle ne yapacağını belirler."),
        ["FieldName"] = new("Name", "Ad"),
        ["FieldNameHint"] = new("Shown in the list. Free text.", "Listede görünür. Serbest metin."),
        ["FieldSubdomain"] = new("Subdomain", "Alt alan adı"),
        ["FieldSubdomainHint"] = new(
            "Leave empty for a random one.",
            "Rastgele bir tane için boş bırak."),
        ["RandomName"] = new("Random", "Rastgele"),
        ["FieldTargetHost"] = new("Target address", "Hedef adres"),
        ["FieldTargetHostHint"] = new(
            "127.0.0.1 for a service on this machine, or a LAN address such as 192.168.1.20 for another one.",
            "Bu makinedeki bir servis için 127.0.0.1, başka bir makine için 192.168.1.20 gibi bir ağ adresi."),
        ["FieldTargetPort"] = new("Target port", "Hedef port"),
        ["FieldPublicPort"] = new("Public port", "Genel port"),
        ["UseSharedPort"] = new("Use the server's shared port", "Sunucunun paylaşılan portunu kullan"),
        ["UseSharedPortHint"] = new(
            "No extra port to open — this rides the one port the gateway already listens on. Only one TCP and one UDP tunnel can, because traffic that announces no destination has to have exactly one place to go.",
            "Açılacak ek port yok — ağ geçidinin zaten dinlediği tek portu kullanır. Bunu yalnızca bir TCP ve bir UDP tüneli yapabilir, çünkü hedefini belirtmeyen trafiğin gidebileceği tek bir yer olmalı."),
        ["SharedPortUnavailable"] = new(
            "The shared port is already taken by another tunnel, or single-port mode is off on the server.",
            "Paylaşılan port başka bir tünel tarafından alınmış ya da sunucuda tek port modu kapalı."),
        ["FieldPublicPortHint"] = new(
            "Leave empty to let the server pick a free one.",
            "Sunucunun boş bir tane seçmesi için boş bırak."),
        ["FieldDevice"] = new("Carried by", "Taşıyan cihaz"),
        ["FieldDeviceHint"] = new(
            "The machine that can reach the target. Only online devices can carry traffic.",
            "Hedefe erişebilen makine. Yalnızca çevrimiçi cihazlar trafik taşıyabilir."),
        ["TerminateTls"] = new("Terminate HTTPS at the gateway", "HTTPS'i ağ geçidinde sonlandır"),
        ["TerminateTlsHint"] = new(
            "Leave on when the local service speaks plain HTTP. Turn off only if it serves HTTPS itself.",
            "Yerel servis düz HTTP konuşuyorsa açık bırak. Yalnızca kendisi HTTPS sunuyorsa kapat."),
        ["HostnamesUnavailable"] = new(
            "This server has no base domain configured, so hostname tunnels are unavailable.",
            "Bu sunucuda temel alan adı tanımlı değil, bu yüzden alan adlı tüneller kullanılamaz."),
        ["UseLocalAddress"] = new("Use", "Kullan"),

        // ---- Devices ----
        ["DevicesTitle"] = new("Devices", "Cihazlar"),
        ["DevicesSubtitle"] = new(
            "Machines registered with this server.",
            "Bu sunucuya kayıtlı makineler."),
        ["Online"] = new("Online", "Çevrimiçi"),
        ["Offline"] = new("Offline", "Çevrimdışı"),
        ["Revoked"] = new("Revoked", "İptal edildi"),
        ["PendingApproval"] = new("Waiting for approval", "Onay bekliyor"),
        ["Approve"] = new("Approve", "Onayla"),
        ["Revoke"] = new("Revoke", "İptal et"),
        ["Restore"] = new("Restore", "Geri al"),
        ["LocalAddresses"] = new("Local addresses", "Yerel adresler"),
        ["LastSeen"] = new("Last seen", "Son görülme"),
        ["ClientVersion"] = new("Client", "İstemci"),
        ["TunnelCount"] = new("tunnels", "tünel"),
        ["ConfirmRemoveDevice"] = new(
            "Remove this device? Every tunnel it carries is deleted too.",
            "Bu cihaz kaldırılsın mı? Taşıdığı tüm tüneller de silinir."),

        // ---- Client settings ----
        ["SettingsTitle"] = new("Settings", "Ayarlar"),
        ["SectionConnection"] = new("CONNECTION", "BAĞLANTI"),
        ["SectionStartup"] = new("STARTUP", "BAŞLANGIÇ"),
        ["SectionApplication"] = new("APPLICATION", "UYGULAMA"),
        ["SectionIdentity"] = new("IDENTITY", "KİMLİK"),
        ["ProvisionedBuild"] = new(
            "This build was created by the server and already knows how to reach it.",
            "Bu paket sunucu tarafından oluşturuldu ve ona nasıl ulaşacağını zaten biliyor."),
        ["ManualBuild"] = new(
            "Enter the details from your server's admin app.",
            "Sunucunun yönetim uygulamasındaki bilgileri gir."),
        ["FieldServerHost"] = new("Server address", "Sunucu adresi"),
        ["FieldControlPort"] = new("Control port", "Kontrol portu"),
        ["FieldEnrollmentKey"] = new("Enrollment key", "Kayıt anahtarı"),
        ["FieldFingerprint"] = new("Certificate fingerprint", "Sertifika parmak izi"),
        ["FieldFingerprintHint"] = new(
            "Pinned so the client only ever talks to your server. Copy it from the server's overview.",
            "İstemcinin yalnızca senin sunucunla konuşması için sabitlenir. Sunucunun genel bakış ekranından kopyala."),
        ["StartWithWindows"] = new("Start with Windows", "Windows ile başlat"),
        ["StartMinimised"] = new("Start hidden in the tray", "Sistem tepsisinde gizli başlat"),
        ["StartElevated"] = new("Start as administrator", "Yönetici olarak başlat"),
        ["StartElevatedHint"] = new(
            "Registers a scheduled task so Windows starts it elevated without a prompt. Needs administrator rights once.",
            "Windows'un istem göstermeden yükseltilmiş başlatması için bir görev kaydeder. Bir kez yönetici hakkı gerekir."),
        ["ConnectOnLaunch"] = new("Connect on launch", "Açılışta bağlan"),
        ["CloseToTray"] = new("Close to tray instead of exiting", "Kapatınca çıkma, tepsiye küçült"),
        ["ShowNotifications"] = new("Show notifications", "Bildirimleri göster"),
        ["Language"] = new("Language", "Dil"),
        ["LanguageSystem"] = new("Match Windows", "Windows ile aynı"),
        ["LanguageTurkish"] = new("Türkçe", "Türkçe"),
        ["LanguageEnglish"] = new("English", "English"),
        ["DeviceName"] = new("Device name", "Cihaz adı"),
        ["DeviceNameHint"] = new(
            "How this machine appears to every other device.",
            "Bu makinenin diğer tüm cihazlarda nasıl göründüğü."),
        ["SettingsLocation"] = new("Settings file", "Ayar dosyası"),
        ["PortableMode"] = new(
            "Portable — settings live next to the executable.",
            "Taşınabilir — ayarlar çalıştırılabilir dosyanın yanında."),
        ["AppDataMode"] = new(
            "Settings are stored in your user profile.",
            "Ayarlar kullanıcı profilinde saklanıyor."),
        ["ForgetDevice"] = new("Forget this device", "Bu cihazı unut"),
        ["ForgetDeviceHint"] = new(
            "Clears the stored identity. The server will register this machine again as new.",
            "Saklı kimliği temizler. Sunucu bu makineyi yeniden yeni olarak kaydeder."),
        ["ConfirmForgetDevice"] = new(
            "Forget this device's identity and disconnect?",
            "Bu cihazın kimliği unutulsun ve bağlantı kesilsin mi?"),

        // ---- Client notices ----
        ["NoticeTunnelRemoved"] = new("Tunnel removed remotely", "Tünel uzaktan kaldırıldı"),
        ["UnauthorizedTitle"] = new("This client is no longer authorised", "Bu istemci artık yetkili değil"),
        ["UnauthorizedKeyRotated"] = new(
            "The server's key was changed. Ask for a new client build from the server's admin app.",
            "Sunucunun anahtarı değiştirildi. Sunucunun yönetim uygulamasından yeni bir istemci paketi iste."),
        ["UnauthorizedRevoked"] = new(
            "This device was revoked on the server.",
            "Bu cihaz sunucuda iptal edildi."),
        ["NotConfiguredTitle"] = new("No server configured", "Sunucu tanımlı değil"),
        ["NotConfiguredHint"] = new(
            "Open Settings and enter your server's address and key, or use a client build generated by the server.",
            "Ayarları aç ve sunucunun adresi ile anahtarını gir, ya da sunucunun oluşturduğu bir istemci paketi kullan."),

        // ---- Server overview ----
        ["OverviewTitle"] = new("Gateway", "Ağ Geçidi"),
        ["GatewayRunning"] = new("Running", "Çalışıyor"),
        ["GatewayStopped"] = new("Stopped", "Durduruldu"),
        ["StartGateway"] = new("Start gateway", "Ağ geçidini başlat"),
        ["StopGateway"] = new("Stop gateway", "Ağ geçidini durdur"),
        ["ConnectedDevices"] = new("Connected devices", "Bağlı cihazlar"),
        ["TotalTunnels"] = new("Tunnels", "Tünel"),
        ["ControlPortLabel"] = new("Control port", "Kontrol portu"),
        ["ControlPortHint"] = new(
            "Forward this port to this machine. Nothing else needs to be open.",
            "Bu portu bu makineye yönlendir. Başka hiçbir şeyin açık olması gerekmez."),
        ["CertificateFingerprint"] = new("Certificate fingerprint", "Sertifika parmak izi"),
        ["EnrollmentKeyLabel"] = new("Enrollment key", "Kayıt anahtarı"),
        ["RevealKey"] = new("Reveal", "Göster"),
        ["HideKey"] = new("Hide", "Gizle"),
        ["RotateKey"] = new("Rotate key", "Anahtarı yenile"),
        ["RotateKeyHint"] = new(
            "Generates a new key and immediately invalidates every client build carrying the old one.",
            "Yeni bir anahtar üretir ve eskisini taşıyan tüm istemci paketlerini anında geçersiz kılar."),
        ["ConfirmRotateKey"] = new(
            "Rotate the enrollment key? Every existing client build stops working until you hand out new ones.",
            "Kayıt anahtarı yenilensin mi? Mevcut tüm istemci paketleri, yenilerini dağıtana kadar çalışmayı durdurur."),

        // ---- Server settings ----
        ["SectionPublic"] = new("PUBLIC ADDRESSES", "GENEL ADRESLER"),
        ["SectionListeners"] = new("LISTENERS", "DİNLEYİCİLER"),
        ["SectionDns"] = new("DNS", "DNS"),
        ["SectionSecurity"] = new("SECURITY", "GÜVENLİK"),
        ["FieldServerName"] = new("Deployment name", "Kurulum adı"),
        ["FieldPublicHost"] = new("Public host or IP", "Genel adres veya IP"),
        ["FieldPublicHostHint"] = new(
            "What users connect to for port tunnels. Your VPS's public IP, or a hostname pointing at it.",
            "Port tünelleri için kullanıcıların bağlandığı adres. VPS'inin genel IP'si veya ona işaret eden bir alan adı."),
        ["FieldBaseDomain"] = new("Base domain", "Temel alan adı"),
        ["FieldBaseDomainHint"] = new(
            "Hostname tunnels are created under this, e.g. tunnel.example.com. Leave empty to use ports only.",
            "Alan adlı tüneller bunun altında oluşturulur, örn. tunnel.example.com. Yalnızca port kullanmak için boş bırak."),
        ["SinglePortMode"] = new("Share one port for everything possible", "Mümkün olan her şeyi tek portta topla"),
        ["SinglePortModeHint"] = new(
            "Agent links, websites and Minecraft all arrive on the control port, so only that one port has to be open. Visitors then reach websites on that port rather than 443, unless something in front maps it. Tunnels with their own public port are unaffected and still need theirs.",
            "Agent bağlantıları, web siteleri ve Minecraft hepsi kontrol portuna gelir; böylece yalnızca o tek portun açık olması yeterlidir. Bu durumda ziyaretçiler web sitelerine 443 yerine o porttan erişir (önünde eşleme yapan bir şey yoksa). Kendi genel portu olan tüneller bundan etkilenmez, onlar portlarını kullanmaya devam eder."),
        ["SinglePortActive"] = new("Only this port needs to be open:", "Yalnızca bu portun açık olması yeterli:"),

        ["SectionPublicTls"] = new("PUBLIC HTTPS CERTIFICATE", "GENEL HTTPS SERTİFİKASI"),
        ["PublicTlsHint"] = new(
            "Optional PKCS#12 (.pfx/.p12) certificate used for public HTTPS. If none is imported, CaYaTunnel uses its generated self-signed certificate.",
            "Genel HTTPS için isteğe bağlı PKCS#12 (.pfx/.p12) sertifikası. İçeri aktarılmazsa CaYaTunnel oluşturduğu kendinden imzalı sertifikayı kullanır."),
        ["PublicTlsPassword"] = new("Certificate password", "Sertifika parolası"),
        ["PublicTlsPasswordHint"] = new(
            "Enter the PFX/P12 password before importing. Leave empty for a passwordless certificate.",
            "İçe aktarmadan önce PFX/P12 parolasını gir. Parolasız sertifika için boş bırak."),
        ["ImportPublicTlsCertificate"] = new("Import PFX/P12…", "PFX/P12 içeri aktar…"),
        ["ImportPublicTlsPemCertificate"] = new("Select PEM certificate", "PEM sertifikasını seç"),
        ["ImportPublicTlsPrivateKey"] = new("Select PEM private key", "PEM private key dosyasını seç"),
        ["ImportPublicTlsPemButton"] = new("Import Cloudflare PEM + private key…", "Cloudflare PEM + private key içeri aktar…"),
        ["PublicTlsPemImportSuccess"] = new("PEM certificate and private key imported. Save settings to apply them.", "PEM sertifikası ve private key içeri aktarıldı. Uygulamak için ayarları kaydet."),
        ["ClearPublicTlsCertificate"] = new("Use automatic certificate", "Otomatik sertifikayı kullan"),
        ["PublicTlsAutomaticCertificate"] = new("Automatic self-signed certificate", "Otomatik kendinden imzalı sertifika"),
        ["PublicTlsCertificateLoaded"] = new("{0} — expires {1}", "{0} — son geçerlilik {1}"),
        ["PublicTlsPrivateKeyRequired"] = new("The certificate must include its private key.", "Sertifika özel anahtarını da içermelidir."),
        ["PublicTlsImportSuccess"] = new("Public HTTPS certificate imported. Save settings to apply it.", "Genel HTTPS sertifikası içeri aktarıldı. Uygulamak için ayarları kaydet."),
        ["PublicTlsImportFailed"] = new("Could not import certificate: {0}", "Sertifika içeri aktarılamadı: {0}"),
        ["PublicTlsCleared"] = new("The generated certificate will be used after saving.", "Kaydettikten sonra oluşturulan otomatik sertifika kullanılacak."),

        // ---- Firewall ----
        ["SectionFirewall"] = new("WINDOWS FIREWALL", "WINDOWS GÜVENLİK DUVARI"),
        ["FirewallHint"] = new(
            "Creates inbound rules for exactly the ports this configuration uses. Rules are tagged as CaYaTunnel's, so removing them never touches anything else.",
            "Bu yapılandırmanın kullandığı portlar için tam olarak gelen kural oluşturur. Kurallar CaYaTunnel etiketiyle işaretlenir, kaldırma işlemi başka hiçbir şeye dokunmaz."),
        ["FirewallApply"] = new("Create rules", "Kuralları oluştur"),
        ["FirewallRemove"] = new("Remove rules", "Kuralları kaldır"),
        ["FirewallWillOpen"] = new("Will open:", "Açılacak:"),
        ["FirewallNeedsAdmin"] = new(
            "Managing firewall rules needs administrator rights.",
            "Güvenlik duvarı kurallarını yönetmek için yönetici hakkı gerekir."),

        ["EnableHttpRouter"] = new("HTTP and HTTPS router", "HTTP ve HTTPS yönlendirici"),
        ["EnableMinecraftRouter"] = new("Minecraft router", "Minecraft yönlendirici"),
        ["FieldHttpPort"] = new("HTTP port", "HTTP portu"),
        ["FieldHttpsPort"] = new("HTTPS port", "HTTPS portu"),
        ["FieldMinecraftPort"] = new("Minecraft port", "Minecraft portu"),
        ["FieldPortRange"] = new("TCP tunnel port range", "TCP tünel port aralığı"),
        ["FieldPortRangeHint"] = new(
            "Ports handed out to TCP tunnels. Open this range on your firewall.",
            "TCP tünellerine dağıtılan portlar. Bu aralığı güvenlik duvarında aç."),
        ["DnsProvider"] = new("Provider", "Sağlayıcı"),
        ["DnsManual"] = new("Manual", "Elle"),
        ["DnsManualHint"] = new(
            "Point a wildcard record at this server yourself, e.g. *.tunnel.example.com.",
            "Bu sunucuya kendin bir joker kayıt yönlendir, örn. *.tunnel.example.com."),
        ["CloudflareToken"] = new("API token", "API anahtarı"),
        ["CloudflareTokenHint"] = new(
            "Needs only Zone / DNS / Edit on the zone holding your base domain. Stored encrypted on this machine.",
            "Yalnızca temel alan adının bulunduğu bölgede Zone / DNS / Edit yetkisi gerekir. Bu makinede şifreli saklanır."),
        ["CloudflareZoneId"] = new("Zone ID", "Bölge kimliği"),
        ["ProxyRecords"] = new("Route website records through Cloudflare", "Web sitesi kayıtlarını Cloudflare üzerinden geçir"),
        ["ProxyRecordsHint"] = new(
            "Only website tunnels are proxied. TCP and Minecraft tunnels always resolve straight here, because an HTTP proxy cannot carry them.",
            "Yalnızca web sitesi tünelleri proxy'lenir. TCP ve Minecraft tünelleri her zaman doğrudan buraya çözümlenir, çünkü bir HTTP proxy'si onları taşıyamaz."),
        ["TestConnection"] = new("Test", "Test et"),
        ["RequireApproval"] = new("Require approval for new devices", "Yeni cihazlar için onay iste"),
        ["RequireApprovalHint"] = new(
            "A newly seen machine waits until you approve it, even with a valid key.",
            "Yeni görülen bir makine, geçerli anahtarla bile sen onaylayana kadar bekler."),
        ["RunAsService"] = new("Run as a Windows service", "Windows hizmeti olarak çalıştır"),
        ["RunAsServiceHint"] = new(
            "Keeps the gateway running without anyone signed in. Needs administrator rights.",
            "Kimse oturum açmadan ağ geçidini çalışır tutar. Yönetici hakkı gerekir."),
        ["ServiceAlreadyRunning"] = new(
            "The CaYaTunnel service is already running and holds these ports. Stop the service before starting the gateway here, or just manage it from this window while the service serves the traffic.",
            "CaYaTunnel hizmeti zaten çalışıyor ve bu portları tutuyor. Ağ geçidini buradan başlatmadan önce hizmeti durdur, ya da trafiği hizmet taşırken bu pencereyi yalnızca yönetim için kullan."),
        ["InstallService"] = new("Install service", "Hizmeti kur"),
        ["UninstallService"] = new("Remove service", "Hizmeti kaldır"),
        ["AutoStartGateway"] = new("Start the gateway when this app opens", "Uygulama açıldığında ağ geçidini başlat"),
        ["SettingsSaved"] = new("Settings saved.", "Ayarlar kaydedildi."),
        ["RestartRequired"] = new("Listeners restarted with the new settings.", "Dinleyiciler yeni ayarlarla yeniden başlatıldı."),

        // ---- Client builds ----
        ["ClientBuildsTitle"] = new("Client builds", "İstemci paketleri"),
        ["ClientBuildsSubtitle"] = new(
            "Generate a ready-to-run client. It carries this server's address and key, so there is nothing to configure on the other machine.",
            "Çalışmaya hazır bir istemci üret. Bu sunucunun adresini ve anahtarını taşır, diğer makinede ayarlanacak hiçbir şey kalmaz."),
        ["StubMissing"] = new(
            "No client template found. Put CaYaTunnelClient.exe in the server's stub folder, or pick it below.",
            "İstemci şablonu bulunamadı. CaYaTunnelClient.exe dosyasını sunucunun stub klasörüne koy veya aşağıdan seç."),
        ["ImportStub"] = new("Choose client template…", "İstemci şablonunu seç…"),
        ["BuildClient"] = new("Build client", "İstemci oluştur"),
        ["BuildForDevice"] = new("For device", "Cihaz için"),
        ["BuildGeneric"] = new("Any machine (shared key)", "Herhangi bir makine (ortak anahtar)"),
        ["BuildGenericHint"] = new(
            "A shared-key build works on any machine. A per-device build can be revoked on its own.",
            "Ortak anahtarlı paket her makinede çalışır. Cihaza özel paket tek başına iptal edilebilir."),
        ["BuildSucceeded"] = new("Client build saved.", "İstemci paketi kaydedildi."),
        ["ShowInFolder"] = new("Show in folder", "Klasörde göster"),

        // ---- Activity ----
        ["ActivityTitle"] = new("Activity", "Etkinlik"),
        ["VerboseLogging"] = new("Verbose", "Ayrıntılı"),
        ["ClearLog"] = new("Clear", "Temizle"),
        ["NoActivity"] = new("Nothing has happened yet.", "Henüz bir şey olmadı."),
    };
}
