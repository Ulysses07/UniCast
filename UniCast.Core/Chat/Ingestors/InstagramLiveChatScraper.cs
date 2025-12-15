using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using UniCast.Core.Chat.Config;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// Instagram Live Chat Scraper - WebView2 tabanlı.
    /// 
    /// Çalışma prensibi:
    /// 1. WebView2 ile instagram.com/{broadcaster}/live/ sayfasına git
    /// 2. Kullanıcı adı ve yorum elementlerini scrape et
    /// 3. MutationObserver ile yeni yorumları yakala
    /// 
    /// Selector'lar sunucudan dinamik olarak çekilir.
    /// Sunucuya ulaşılamazsa fallback selector'lar kullanılır.
    /// </summary>
    public sealed class InstagramLiveChatScraper : BaseChatIngestor
    {
        private readonly ConcurrentDictionary<string, bool> _seenCommentIds = new();
        private TaskCompletionSource<bool>? _initTcs;
        private bool _observerInjected;

        // Dinamik selector'lar - sunucudan veya fallback'ten alınır
        private InstagramSelectors? _selectors;

        // WebView2 kontrolleri - dışarıdan set edilir
        private Func<Task>? _ensureWebViewReady;
        private Func<string, Task>? _navigateToUrl;
        private Func<string, Task<string>>? _executeScript;
        private Action<Action<string>>? _registerMessageHandler;
        private Action? _unregisterMessageHandler;

        public override ChatPlatform Platform => ChatPlatform.Instagram;

        /// <summary>
        /// Yayıncı kullanıcı adı (@ olmadan)
        /// </summary>
        public string BroadcasterUsername { get; set; }

        /// <summary>
        /// Live URL - otomatik oluşturulur veya manuel set edilebilir
        /// </summary>
        public string? LiveUrl { get; set; }

        public InstagramLiveChatScraper(string broadcasterUsername, string? liveUrl = null) : base(broadcasterUsername.TrimStart('@').ToLowerInvariant())
        {
            BroadcasterUsername = _identifier;
            LiveUrl = liveUrl;
        }

        /// <summary>
        /// WebView2 kontrollerini ayarlar - InstagramChatHost tarafından çağrılır
        /// </summary>
        public void SetWebViewControls(
            Func<Task> ensureReady,
            Func<string, Task> navigate,
            Func<string, Task<string>> executeScript,
            Action<Action<string>> registerHandler,
            Action unregisterHandler)
        {
            _ensureWebViewReady = ensureReady;
            _navigateToUrl = navigate;
            _executeScript = executeScript;
            _registerMessageHandler = registerHandler;
            _unregisterMessageHandler = unregisterHandler;
            Log.Debug("[IG Scraper] WebView kontrolleri ayarlandı");
        }

        protected override async Task ConnectAsync(CancellationToken ct)
        {
            if (_ensureWebViewReady == null || _navigateToUrl == null || _executeScript == null)
                throw new InvalidOperationException("WebView2 kontrolleri ayarlanmamış. SetWebViewControls() çağrılmalı.");

            if (string.IsNullOrEmpty(BroadcasterUsername))
                throw new InvalidOperationException("Yayıncı kullanıcı adı gerekli.");

            // URL oluştur
            var url = LiveUrl ?? $"https://www.instagram.com/{BroadcasterUsername}/live/";

            Log.Information("[IG Scraper] Bağlanılıyor: {Url}", url);

            // WebView2 hazır olana kadar bekle
            await _ensureWebViewReady();

            // Mesaj handler'ı kaydet
            _registerMessageHandler?.Invoke(OnWebMessageReceived);

            // Init tamamlanma sinyali için TCS
            _initTcs = new TaskCompletionSource<bool>();

            // Sayfaya git
            await _navigateToUrl(url);

            // Sayfanın yüklenmesi için bekle
            await Task.Delay(4000, ct);

            // Login sayfasına yönlendirilip yönlendirilmediğini kontrol et
            var currentUrl = await _executeScript("window.location.href");
            currentUrl = currentUrl?.Trim('"') ?? "";

            if (currentUrl.Contains("/accounts/login") || currentUrl.Contains("/challenge"))
            {
                Log.Error("[IG Scraper] Instagram login gerekli - mevcut URL: {Url}", currentUrl);
                throw new InvalidOperationException(
                    "Instagram'a giriş yapılmamış veya oturum süresi dolmuş. " +
                    "Lütfen Ayarlar > Instagram bölümünden hesap bilgilerini kontrol edin ve uygulamayı yeniden başlatın.");
            }

            // Observer script'i inject et
            await InjectObserverAsync(ct);

            Log.Information("[IG Scraper] Bağlantı başarılı - @{Username}", BroadcasterUsername);
        }

        private void OnWebMessageReceived(string json)
        {
            try
            {
                ProcessIncomingMessage(json);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[IG Scraper] WebMessage parse hatası");
            }
        }

        private void ProcessIncomingMessage(string json)
        {
            try
            {
                // Bazen JSON çift quote ile gelir
                if (json.StartsWith("\"") && json.EndsWith("\""))
                    json = JsonSerializer.Deserialize<string>(json) ?? json;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl))
                    return;

                var type = typeEl.GetString();

                switch (type)
                {
                    case "init":
                        Log.Information("[IG Scraper] Observer başlatıldı");
                        _initTcs?.TrySetResult(true);
                        break;

                    case "comment":
                        ProcessComment(root);
                        break;

                    case "batch":
                        if (root.TryGetProperty("comments", out var comments))
                        {
                            foreach (var comment in comments.EnumerateArray())
                            {
                                ProcessComment(comment);
                            }
                        }
                        break;

                    case "error":
                        var errorMsg = root.TryGetProperty("message", out var m) ? m.GetString() : "Unknown";
                        Log.Warning("[IG Scraper] JS hatası: {Error}", errorMsg);
                        break;

                    case "debug":
                        var debugMsg = root.TryGetProperty("message", out var d) ? d.GetString() : "";
                        Log.Debug("[IG Scraper] JS: {Message}", debugMsg);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[IG Scraper] Message processing hatası");
            }
        }

        private void ProcessComment(JsonElement comment)
        {
            try
            {
                var id = comment.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

                // Duplicate kontrolü
                if (!string.IsNullOrEmpty(id) && _seenCommentIds.ContainsKey(id))
                    return;

                if (!string.IsNullOrEmpty(id))
                    _seenCommentIds.TryAdd(id, true);

                var username = comment.TryGetProperty("username", out var uEl) ? uEl.GetString() : "Unknown";
                var message = comment.TryGetProperty("message", out var mEl) ? mEl.GetString() : "";

                if (string.IsNullOrWhiteSpace(message))
                    return;

                var chatMessage = new ChatMessage
                {
                    Platform = ChatPlatform.Instagram,
                    Username = username ?? "unknown",
                    DisplayName = username ?? "Unknown",
                    Message = message,
                    Timestamp = DateTime.UtcNow,
                    Type = ChatMessageType.Normal,
                    Metadata = string.IsNullOrEmpty(id) ? new() : new() { ["comment_id"] = id }
                };

                PublishMessage(chatMessage);
                Log.Debug("[IG Scraper] Yorum: @{Username}: {Message}", username, message);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[IG Scraper] Comment parse hatası");
            }
        }

        private async Task InjectObserverAsync(CancellationToken ct)
        {
            if (_observerInjected || _executeScript == null)
                return;

            try
            {
                var script = GetObserverScript();
                await _executeScript(script);
                _observerInjected = true;

                // Init sinyalini bekle (max 15 saniye)
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                try
                {
                    await _initTcs!.Task.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("[IG Scraper] Observer init timeout - devam ediliyor");
                }

                Log.Debug("[IG Scraper] Observer inject edildi");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[IG Scraper] Observer inject hatası");
                throw;
            }
        }

        private string GetObserverScript()
        {
            // Selector'ları al (cache'li veya sunucudan)
            _selectors ??= SelectorConfigService.Instance.GetInstagramSelectors();

            var usernameSelector = _selectors.Username ?? "span._ap3a._aaco._aacw._aacx._aad7";
            var messageSelector = _selectors.Message ?? "span._ap3a._aaco._aacu._aacx._aad7._aadf";

            Log.Debug("[IG Scraper] Selector'lar: User={User}, Msg={Msg}", usernameSelector, messageSelector);

            var sb = new StringBuilder();

            sb.Append("(function(){");

            // Eğer zaten çalışıyorsa tekrar başlatma
            sb.Append("if(window.__igLiveChatObserver){console.log('IG Observer zaten aktif');return;}");

            // Değişkenler
            sb.Append("var seenIds=new Set();");
            sb.Append("var observer=null;");

            // Log fonksiyonu
            sb.Append("function log(msg){");
            sb.Append("console.log('[IG Chat] '+msg);");
            sb.Append("window.chrome.webview.postMessage(JSON.stringify({type:'debug',message:msg}));");
            sb.Append("}");

            // Yorum çıkarma fonksiyonu - DİNAMİK SELECTOR'LAR
            sb.Append("function extractComments(){");
            sb.Append("var comments=[];");

            // Dinamik selector'lar - sunucudan veya fallback
            sb.AppendFormat("var usernames=document.querySelectorAll('{0}');", EscapeJs(usernameSelector));
            sb.AppendFormat("var messages=document.querySelectorAll('{0}');", EscapeJs(messageSelector));

            sb.Append("log('Bulunan: '+usernames.length+' kullanıcı, '+messages.length+' mesaj');");

            sb.Append("for(var i=0;i<usernames.length&&i<messages.length;i++){");
            sb.Append("var user=usernames[i].innerText||usernames[i].textContent||'';");
            sb.Append("var msg=messages[i].innerText||messages[i].textContent||'';");

            // Boş değerleri atla
            sb.Append("if(!user||!msg)continue;");

            // ID oluştur (kullanıcı + mesaj hash)
            sb.Append("var id='ig_'+user+'_'+msg.substring(0,30).replace(/\\s/g,'_');");

            // Duplicate kontrolü
            sb.Append("if(seenIds.has(id))continue;");

            sb.Append("comments.push({id:id,username:user,message:msg});");
            sb.Append("}");

            sb.Append("return comments;");
            sb.Append("}");

            // Scan fonksiyonu
            sb.Append("function scan(){");
            sb.Append("var cs=extractComments();");
            sb.Append("if(cs.length>0){");
            sb.Append("var unique=[];");
            sb.Append("for(var j=0;j<cs.length;j++){");
            sb.Append("if(!seenIds.has(cs[j].id)){");
            sb.Append("seenIds.add(cs[j].id);");
            sb.Append("unique.push(cs[j]);");
            sb.Append("}");
            sb.Append("}");
            sb.Append("if(unique.length>0){");
            sb.Append("window.chrome.webview.postMessage(JSON.stringify({type:'batch',comments:unique}));");
            sb.Append("log(unique.length+' yeni yorum gönderildi');");
            sb.Append("}");
            sb.Append("}");
            sb.Append("}");

            // Mutation handler
            sb.Append("function onMutation(){");
            sb.Append("var cs=extractComments();");
            sb.Append("if(cs.length>0){");
            sb.Append("var newOnes=[];");
            sb.Append("for(var j=0;j<cs.length;j++){");
            sb.Append("if(!seenIds.has(cs[j].id)){");
            sb.Append("seenIds.add(cs[j].id);");
            sb.Append("newOnes.push(cs[j]);");
            sb.Append("}");
            sb.Append("}");
            sb.Append("for(var k=0;k<newOnes.length;k++){");
            sb.Append("window.chrome.webview.postMessage(JSON.stringify({");
            sb.Append("type:'comment',");
            sb.Append("id:newOnes[k].id,");
            sb.Append("username:newOnes[k].username,");
            sb.Append("message:newOnes[k].message");
            sb.Append("}));");
            sb.Append("}");
            sb.Append("if(newOnes.length>0)log('Mutation: '+newOnes.length+' yeni yorum');");
            sb.Append("}");
            sb.Append("}");

            // Start fonksiyonu
            sb.Append("function start(){");
            sb.Append("log('Instagram Live Chat Observer başlatılıyor...');");
            sb.Append("log('URL: '+window.location.href);");

            // İlk tarama
            sb.Append("scan();");

            // MutationObserver başlat
            sb.Append("observer=new MutationObserver(function(mutations){");
            sb.Append("onMutation();");
            sb.Append("});");
            sb.Append("observer.observe(document.body,{childList:true,subtree:true});");

            // Global referanslar
            sb.Append("window.__igLiveChatObserver=observer;");
            sb.Append("window.__igLiveScanComments=scan;");

            // Periyodik tarama (her 3 saniyede bir - MutationObserver'ı destekler)
            sb.Append("setInterval(function(){scan();},3000);");

            // Init sinyali gönder
            sb.Append("window.chrome.webview.postMessage(JSON.stringify({type:'init',status:'started'}));");
            sb.Append("log('Observer hazır!');");
            sb.Append("}");

            // Başlat
            sb.Append("log('Script yüklendi');");
            sb.Append("if(document.readyState==='complete'){");
            sb.Append("setTimeout(start,1000);");
            sb.Append("}else{");
            sb.Append("window.addEventListener('load',function(){setTimeout(start,1000);});");
            sb.Append("}");

            sb.Append("})();");

            return sb.ToString();
        }

        /// <summary>
        /// JavaScript string escape
        /// </summary>
        private static string EscapeJs(string s)
        {
            return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"");
        }

        protected override Task DisconnectAsync()
        {
            _observerInjected = false;
            _seenCommentIds.Clear();
            _unregisterMessageHandler?.Invoke();

            try
            {
                _executeScript?.Invoke("if(window.__igLiveChatObserver){window.__igLiveChatObserver.disconnect();window.__igLiveChatObserver=null;}");
            }
            catch { /* ignore */ }

            Log.Debug("[IG Scraper] Bağlantı kapatıldı");
            return Task.CompletedTask;
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            // Ana döngü - observer health check
            while (!ct.IsCancellationRequested && State == ConnectionState.Connected)
            {
                try
                {
                    await Task.Delay(30000, ct); // 30 saniyede bir kontrol

                    if (_executeScript != null)
                    {
                        var result = await _executeScript("window.__igLiveChatObserver?'active':'inactive'");

                        if (result.Contains("inactive"))
                        {
                            Log.Warning("[IG Scraper] Observer durdu, yeniden başlatılıyor...");
                            _observerInjected = false;
                            await InjectObserverAsync(ct);
                        }
                    }

                    // Memory temizliği
                    if (_seenCommentIds.Count > 5000)
                    {
                        _seenCommentIds.Clear();
                        Log.Debug("[IG Scraper] Seen IDs temizlendi");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[IG Scraper] Health check hatası");
                }
            }
        }

        /// <summary>
        /// Manuel yorum tarama tetikler
        /// </summary>
        public async Task RescanCommentsAsync()
        {
            if (_executeScript == null)
                return;

            try
            {
                await _executeScript("if(typeof window.__igLiveScanComments==='function')window.__igLiveScanComments();");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[IG Scraper] Rescan hatası");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _unregisterMessageHandler?.Invoke();
                _ensureWebViewReady = null;
                _navigateToUrl = null;
                _executeScript = null;
                _registerMessageHandler = null;
                _unregisterMessageHandler = null;
            }
            base.Dispose(disposing);
        }
    }
}