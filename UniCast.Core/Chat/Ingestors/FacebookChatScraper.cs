using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// Facebook Live Chat Scraper - WebView2 + MutationObserver.
    /// Real-time yorum yakalama - polling yok, anlık tespit.
    /// 
    /// Kullanım:
    /// 1. FacebookLoginWindow ile cookie al
    /// 2. SetWebViewControls() ile WebView2 kontrollerini ayarla
    /// 3. Live video URL'sini ayarla
    /// 4. StartAsync() çağır
    /// 
    /// WebView2 arka planda çalışır ve MutationObserver ile
    /// yeni yorumları anında yakalar.
    /// </summary>
    public sealed class FacebookChatScraper : BaseChatIngestor
    {
        private readonly ConcurrentDictionary<string, bool> _seenCommentIds = new();
        private TaskCompletionSource<bool>? _initTcs;
        private bool _observerInjected;

        // WebView2 kontrolü için callback'ler (UI thread'den set edilecek)
        private Func<Task>? _ensureWebViewReady;
        private Func<string, Task>? _navigateToUrl;
        private Func<string, Task<string>>? _executeScript;
        private Action<Action<string>>? _registerMessageHandler;
        private Action? _unregisterMessageHandler;

        public override ChatPlatform Platform => ChatPlatform.Facebook;

        /// <summary>
        /// Facebook Live Video URL veya ID.
        /// </summary>
        public string? LiveVideoUrl { get; set; }

        /// <summary>
        /// Facebook cookie'leri (FacebookLoginWindow'dan alınır).
        /// </summary>
        public string? Cookies { get; set; }

        /// <summary>
        /// Facebook User ID (c_user cookie'sinden alınır).
        /// </summary>
        public string? UserId { get; set; }

        public FacebookChatScraper(string liveVideoUrl) : base(liveVideoUrl)
        {
            LiveVideoUrl = NormalizeUrl(liveVideoUrl);
        }

        /// <summary>
        /// WebView2 kontrollerini ayarlar.
        /// UI tarafından çağrılmalı (Dispatcher üzerinden).
        /// </summary>
        /// <param name="ensureReady">WebView2'nin hazır olmasını sağlayan fonksiyon</param>
        /// <param name="navigate">URL'ye navigate eden fonksiyon</param>
        /// <param name="executeScript">JavaScript çalıştıran fonksiyon</param>
        /// <param name="registerHandler">WebMessage handler kaydeden fonksiyon</param>
        /// <param name="unregisterHandler">WebMessage handler kaldıran fonksiyon</param>
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

            Log.Debug("[FB Scraper] WebView kontrolleri ayarlandı");
        }

        private string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            url = url.Trim();

            // fb.watch linklerini olduğu gibi bırak (redirect olacak)
            if (url.Contains("fb.watch"))
                return url;

            // Sadece ID verilmişse URL oluştur
            if (System.Text.RegularExpressions.Regex.IsMatch(url, @"^\d+$"))
                return $"https://www.facebook.com/watch/live/?v={url}";

            // facebook.com içermiyorsa ID olarak kabul et
            if (!url.Contains("facebook.com"))
                return $"https://www.facebook.com/watch/live/?v={url}";

            return url;
        }

        protected override async Task ConnectAsync(CancellationToken ct)
        {
            if (_ensureWebViewReady == null || _navigateToUrl == null || _executeScript == null)
            {
                throw new InvalidOperationException(
                    "WebView2 kontrolleri ayarlanmamış. SetWebViewControls() çağrılmalı.");
            }

            if (string.IsNullOrEmpty(LiveVideoUrl))
            {
                throw new InvalidOperationException("Live Video URL gerekli.");
            }

            Log.Information("[FB Scraper] Bağlanılıyor: {Url}", LiveVideoUrl);

            // WebView2 hazır olmasını bekle
            await _ensureWebViewReady();

            // Message handler'ı kaydet
            _registerMessageHandler?.Invoke(OnWebMessageReceived);

            // Sayfaya git
            _initTcs = new TaskCompletionSource<bool>();
            await _navigateToUrl(LiveVideoUrl);

            // Sayfa yüklenmesi için bekle
            await Task.Delay(3000, ct);

            // Observer'ı inject et
            await InjectObserverAsync(ct);

            Log.Information("[FB Scraper] Bağlantı başarılı");
        }

        private void OnWebMessageReceived(string json)
        {
            try
            {
                ProcessIncomingMessage(json);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FB Scraper] WebMessage parse hatası");
            }
        }

        private void ProcessIncomingMessage(string json)
        {
            try
            {
                // JSON string içinde escape karakterler olabilir
                if (json.StartsWith("\"") && json.EndsWith("\""))
                {
                    json = JsonSerializer.Deserialize<string>(json) ?? json;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl))
                    return;

                var type = typeEl.GetString();

                switch (type)
                {
                    case "init":
                        Log.Information("[FB Scraper] Observer başlatıldı");
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
                        var errorMsg = root.TryGetProperty("message", out var msgEl)
                            ? msgEl.GetString() : "Unknown error";
                        Log.Warning("[FB Scraper] JavaScript hatası: {Error}", errorMsg);
                        break;

                    case "debug":
                        var debugMsg = root.TryGetProperty("message", out var dbgEl)
                            ? dbgEl.GetString() : "";
                        Log.Debug("[FB Scraper] JS Debug: {Message}", debugMsg);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FB Scraper] Message processing hatası: {Json}",
                    json.Length > 200 ? json.Substring(0, 200) : json);
            }
        }

        private void ProcessComment(JsonElement comment)
        {
            try
            {
                var id = comment.TryGetProperty("id", out var idEl)
                    ? idEl.GetString() : null;

                // Duplicate kontrolü
                if (!string.IsNullOrEmpty(id))
                {
                    if (_seenCommentIds.ContainsKey(id))
                        return;
                    _seenCommentIds.TryAdd(id, true);
                }

                var author = comment.TryGetProperty("author", out var authorEl)
                    ? authorEl.GetString() : "Unknown";
                var authorId = comment.TryGetProperty("authorId", out var authorIdEl)
                    ? authorIdEl.GetString() : author;
                var text = comment.TryGetProperty("text", out var textEl)
                    ? textEl.GetString() : "";
                var avatarUrl = comment.TryGetProperty("avatar", out var avatarEl)
                    ? avatarEl.GetString() : null;

                if (string.IsNullOrWhiteSpace(text))
                    return;

                var chatMessage = new ChatMessage
                {
                    Platform = ChatPlatform.Facebook,
                    Username = authorId ?? author ?? "unknown",
                    DisplayName = author ?? "Unknown",
                    Message = text,
                    AvatarUrl = avatarUrl,
                    Timestamp = DateTime.UtcNow,
                    Metadata = string.IsNullOrEmpty(id)
                        ? new System.Collections.Generic.Dictionary<string, string>()
                        : new System.Collections.Generic.Dictionary<string, string> { ["comment_id"] = id }
                };

                PublishMessage(chatMessage);
                Log.Debug("[FB Scraper] Yorum: {Author}: {Text}", author, text);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FB Scraper] Comment parse hatası");
            }
        }

        private async Task InjectObserverAsync(CancellationToken ct)
        {
            if (_observerInjected || _executeScript == null)
                return;

            var script = GetObserverScript();

            try
            {
                await _executeScript(script);
                _observerInjected = true;

                // Init mesajı bekle (timeout ile)
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                try
                {
                    await _initTcs!.Task.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("[FB Scraper] Observer init timeout - devam ediliyor");
                }

                Log.Debug("[FB Scraper] Observer inject edildi");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FB Scraper] Observer inject hatası");
                throw;
            }
        }

        private string GetObserverScript()
        {
            return @"
(function() {
    'use strict';
    
    // Zaten çalışıyorsa çık
    if (window.__fbLiveChatObserver) {
        window.chrome.webview.postMessage(JSON.stringify({type: 'init', status: 'already_running'}));
        return;
    }
    
    const seenIds = new Set();
    let commentContainer = null;
    let observer = null;
    let retryCount = 0;
    const maxRetries = 30;
    
    function log(msg) {
        window.chrome.webview.postMessage(JSON.stringify({type: 'debug', message: msg}));
    }
    
    // Yorum container'ını bul
    function findCommentContainer() {
        const selectors = [
            '[data-pagelet=""LiveVideoCommentList""]',
            '[data-pagelet*=""Comment""]',
            '[aria-label*=""Comment""]',
            '[aria-label*=""comment""]',
            '[role=""complementary""]',
            '[data-testid=""UFI2CommentsList""]',
            '[data-testid=""comments_list""]'
        ];
        
        for (const selector of selectors) {
            const el = document.querySelector(selector);
            if (el) {
                log('Container bulundu: ' + selector);
                return el;
            }
        }
        
        // Live video sayfasında scrollable container ara
        const scrollables = document.querySelectorAll('[style*=""overflow""], [class*=""scroll""]');
        for (const el of scrollables) {
            // İçinde yorum benzeri yapı var mı?
            const hasComments = el.querySelector('[dir=""auto""]') && 
                               (el.querySelector('a[href*=""/user/""]') || 
                                el.querySelector('a[href*=""facebook.com""]'));
            if (hasComments) {
                log('Container bulundu (fallback scroll)');
                return el;
            }
        }
        
        return null;
    }
    
    // Tek bir yorum element'ini parse et
    function parseComment(el) {
        try {
            let id = el.getAttribute('data-commentid') || 
                     el.getAttribute('data-ft') ||
                     el.getAttribute('id');
            
            if (!id) {
                id = 'gen_' + Date.now() + '_' + Math.random().toString(36).substr(2, 9);
            }
            
            if (seenIds.has(id)) return null;
            
            // Yazar bilgisi
            let author = null;
            let authorId = null;
            let avatar = null;
            
            // Yazar linki bul
            const authorLinks = el.querySelectorAll('a[role=""link""], a[href*=""facebook.com""], a[href*=""/user/""]');
            for (const link of authorLinks) {
                const text = link.textContent?.trim();
                if (text && text.length > 0 && text.length < 100) {
                    author = text;
                    const href = link.getAttribute('href') || '';
                    const idMatch = href.match(/(?:id=|\/user\/|facebook\.com\/)(\d+)/);
                    if (idMatch) authorId = idMatch[1];
                    break;
                }
            }
            
            // Avatar
            const avatarImg = el.querySelector('image[href], img[src*=""fbcdn""]');
            if (avatarImg) {
                avatar = avatarImg.getAttribute('href') || avatarImg.getAttribute('src');
            }
            
            // Mesaj metni - dir=""auto"" içeren span'ları tara
            let text = null;
            const textElements = el.querySelectorAll('[dir=""auto""] span, [dir=""auto""]');
            
            for (const span of textElements) {
                const content = span.textContent?.trim();
                // Yazar adı değilse ve makul uzunluktaysa
                if (content && content !== author && content.length > 0 && content.length < 500) {
                    // Link içinde değilse
                    if (!span.closest('a')) {
                        text = content;
                        break;
                    }
                }
            }
            
            if (!text || !author) return null;
            
            seenIds.add(id);
            
            return {
                id: id,
                author: author,
                authorId: authorId || author,
                text: text,
                avatar: avatar
            };
        } catch (e) {
            log('Parse hatası: ' + e.message);
            return null;
        }
    }
    
    // Mevcut yorumları tara
    function scanExistingComments() {
        if (!commentContainer) return;
        
        const comments = [];
        
        // Tüm potansiyel yorum bloklarını bul
        const blocks = commentContainer.querySelectorAll('[data-commentid], [role=""article""], [class*=""comment""]');
        
        for (const el of blocks) {
            const comment = parseComment(el);
            if (comment) {
                comments.push(comment);
            }
        }
        
        // Alternatif: Her div'i dene
        if (comments.length === 0) {
            const divs = commentContainer.querySelectorAll(':scope > div > div');
            for (const el of divs) {
                const comment = parseComment(el);
                if (comment) {
                    comments.push(comment);
                }
            }
        }
        
        if (comments.length > 0) {
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'batch',
                comments: comments
            }));
            log('Mevcut yorumlar: ' + comments.length);
        }
    }
    
    // MutationObserver callback
    function handleMutations(mutations) {
        for (const mutation of mutations) {
            if (mutation.type === 'childList') {
                for (const node of mutation.addedNodes) {
                    if (node.nodeType !== Node.ELEMENT_NODE) continue;
                    
                    // Yeni node'u parse et
                    const comment = parseComment(node);
                    if (comment) {
                        window.chrome.webview.postMessage(JSON.stringify({
                            type: 'comment',
                            ...comment
                        }));
                        continue;
                    }
                    
                    // İçindeki yorumları da kontrol et
                    const innerBlocks = node.querySelectorAll('[data-commentid], [role=""article""]');
                    for (const el of innerBlocks) {
                        const c = parseComment(el);
                        if (c) {
                            window.chrome.webview.postMessage(JSON.stringify({
                                type: 'comment',
                                ...c
                            }));
                        }
                    }
                }
            }
        }
    }
    
    // Observer'ı başlat
    function startObserver() {
        commentContainer = findCommentContainer();
        
        if (!commentContainer) {
            retryCount++;
            if (retryCount < maxRetries) {
                log('Container bulunamadı, tekrar deneniyor... ' + retryCount);
                setTimeout(startObserver, 1000);
                return;
            }
            
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'error',
                message: 'Comment container bulunamadı (max retry)'
            }));
            
            // Yine de init gönder, belki daha sonra bulunur
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'init',
                status: 'no_container'
            }));
            return;
        }
        
        // Mevcut yorumları tara
        scanExistingComments();
        
        // Observer kur
        observer = new MutationObserver(handleMutations);
        observer.observe(commentContainer, {
            childList: true,
            subtree: true
        });
        
        // Body'yi de izle (container değişebilir)
        const bodyObserver = new MutationObserver(() => {
            if (!document.contains(commentContainer)) {
                log('Container kayboldu, yeniden aranıyor...');
                observer?.disconnect();
                commentContainer = null;
                retryCount = 0;
                setTimeout(startObserver, 2000);
            }
        });
        bodyObserver.observe(document.body, { childList: true, subtree: true });
        
        window.__fbLiveChatObserver = observer;
        window.__fbLiveScanComments = scanExistingComments;
        
        window.chrome.webview.postMessage(JSON.stringify({
            type: 'init',
            status: 'started'
        }));
        
        log('Observer başlatıldı');
    }
    
    // Başlat
    if (document.readyState === 'complete') {
        setTimeout(startObserver, 1000);
    } else {
        window.addEventListener('load', () => setTimeout(startObserver, 1000));
    }
})();
";
        }

        protected override Task DisconnectAsync()
        {
            _observerInjected = false;
            _seenCommentIds.Clear();

            // Message handler'ı kaldır
            _unregisterMessageHandler?.Invoke();

            // Observer'ı temizle
            try
            {
                _executeScript?.Invoke(@"
                    if (window.__fbLiveChatObserver) {
                        window.__fbLiveChatObserver.disconnect();
                        window.__fbLiveChatObserver = null;
                    }
                ");
            }
            catch { }

            Log.Debug("[FB Scraper] Bağlantı kapatıldı");
            return Task.CompletedTask;
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            // MutationObserver zaten çalışıyor
            // Sadece periyodik sağlık kontrolü yap

            while (!ct.IsCancellationRequested && State == ConnectionState.Connected)
            {
                try
                {
                    await Task.Delay(30000, ct);

                    // Observer hala çalışıyor mu?
                    if (_executeScript != null)
                    {
                        var result = await _executeScript(
                            "window.__fbLiveChatObserver ? 'active' : 'inactive'");

                        if (result.Contains("inactive"))
                        {
                            Log.Warning("[FB Scraper] Observer durdu, yeniden başlatılıyor...");
                            _observerInjected = false;
                            await InjectObserverAsync(ct);
                        }
                    }

                    // Bellek temizliği
                    if (_seenCommentIds.Count > 5000)
                    {
                        _seenCommentIds.Clear();
                        Log.Debug("[FB Scraper] Seen IDs temizlendi");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[FB Scraper] Health check hatası");
                }
            }
        }

        /// <summary>
        /// Manuel olarak mevcut yorumları yeniden tarar.
        /// </summary>
        public async Task RescanCommentsAsync()
        {
            if (_executeScript == null) return;

            try
            {
                await _executeScript(@"
                    if (typeof window.__fbLiveScanComments === 'function') {
                        window.__fbLiveScanComments();
                    }
                ");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FB Scraper] Rescan hatası");
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