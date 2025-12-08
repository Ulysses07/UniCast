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
    /// </summary>
    public sealed class FacebookChatScraper : BaseChatIngestor
    {
        private readonly ConcurrentDictionary<string, bool> _seenCommentIds = new();
        private TaskCompletionSource<bool>? _initTcs;
        private bool _observerInjected;

        // WebView2 kontrolü için callback'ler
        private Func<Task>? _ensureWebViewReady;
        private Func<string, Task>? _navigateToUrl;
        private Func<string, Task<string>>? _executeScript;
        private Action<Action<string>>? _registerMessageHandler;
        private Action? _unregisterMessageHandler;

        public override ChatPlatform Platform => ChatPlatform.Facebook;

        public string? LiveVideoUrl { get; set; }
        public string? Cookies { get; set; }
        public string? UserId { get; set; }

        public FacebookChatScraper(string liveVideoUrl) : base(liveVideoUrl)
        {
            LiveVideoUrl = NormalizeUrl(liveVideoUrl);
        }

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

            if (url.Contains("fb.watch"))
                return url;

            if (System.Text.RegularExpressions.Regex.IsMatch(url, @"^\d+$"))
                return $"https://www.facebook.com/watch/live/?v={url}";

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

            await _ensureWebViewReady();
            _registerMessageHandler?.Invoke(OnWebMessageReceived);

            _initTcs = new TaskCompletionSource<bool>();
            await _navigateToUrl(LiveVideoUrl);

            await Task.Delay(3000, ct);
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

                    case "dom_debug":
                        var domInfo = root.TryGetProperty("info", out var domEl)
                            ? domEl.GetString() : "";
                        Log.Information("[FB Scraper] DOM Debug: {Info}", domInfo);
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

                // message veya text field'ını kontrol et
                var text = comment.TryGetProperty("message", out var msgEl)
                    ? msgEl.GetString()
                    : (comment.TryGetProperty("text", out var textEl) ? textEl.GetString() : "");

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
    
    if (window.__fbLiveChatObserver) {
        window.chrome.webview.postMessage(JSON.stringify({type: 'init', status: 'already_running'}));
        return;
    }
    
    var seenIds = new Set();
    var commentContainer = null;
    var observer = null;
    var retryCount = 0;
    var maxRetries = 30;
    
    function log(msg) {
        window.chrome.webview.postMessage(JSON.stringify({type: 'debug', message: msg}));
    }
    
    function domDebug(info) {
        window.chrome.webview.postMessage(JSON.stringify({type: 'dom_debug', info: info}));
    }
    
    function isValidCommentContainer(el) {
        if (!el || el === document.body || el === document.documentElement) return false;
        
        var rect = el.getBoundingClientRect();
        if (rect.height < 100 || rect.height > window.innerHeight * 1.5) return false;
        if (rect.width < 200 || rect.width > window.innerWidth * 0.8) return false;
        
        var links = el.querySelectorAll('a[href]');
        if (links.length < 2) return false;
        
        var avatars = el.querySelectorAll('img[src*=""scontent""], img[src*=""fbcdn""], image[href*=""scontent""], image[href*=""fbcdn""], svg image');
        if (avatars.length < 1) return false;
        
        var texts = el.querySelectorAll('[dir=""auto""]');
        if (texts.length < 2) return false;
        
        return true;
    }
    
    function findCommentContainer() {
        log('Script baslatildi - URL: ' + window.location.href);
        
        var avatarSelectors = [
            'img[src*=""scontent""]',
            'img[src*=""fbcdn""]', 
            'image[href*=""scontent""]',
            'image[href*=""fbcdn""]',
            'svg image[href*=""scontent""]'
        ];
        
        var allAvatars = [];
        for (var s = 0; s < avatarSelectors.length; s++) {
            try {
                var found = document.querySelectorAll(avatarSelectors[s]);
                for (var f = 0; f < found.length; f++) {
                    allAvatars.push(found[f]);
                }
            } catch(e) {}
        }
        
        var filteredAvatars = [];
        for (var a = 0; a < allAvatars.length; a++) {
            var rect = allAvatars[a].getBoundingClientRect();
            if (rect.width > 15 && rect.width < 100 && rect.height > 15 && rect.height < 100) {
                filteredAvatars.push(allAvatars[a]);
            }
        }
        
        log('Bulunan avatar sayisi: ' + filteredAvatars.length);
        
        if (filteredAvatars.length >= 2) {
            function getParentChain(el) {
                var chain = [];
                while (el && el !== document.body && el !== document.documentElement) {
                    chain.push(el);
                    el = el.parentElement;
                }
                return chain;
            }
            
            var chain1 = getParentChain(filteredAvatars[0]);
            
            for (var idx = 0; idx < chain1.length; idx++) {
                var ancestor = chain1[idx];
                var containsOthers = 0;
                for (var i = 1; i < Math.min(filteredAvatars.length, 5); i++) {
                    if (ancestor.contains(filteredAvatars[i])) {
                        containsOthers++;
                    }
                }
                
                if (containsOthers >= 1 && isValidCommentContainer(ancestor)) {
                    log('Container bulundu (avatar LCA): ' + containsOthers + ' avatar iceriyor');
                    return ancestor;
                }
            }
        }
        
        var pageletSelectors = [
            '[data-pagelet=""LiveVideoCommentList""]',
            '[data-pagelet*=""CommentList""]',
            '[data-pagelet=""WatchPermalinkComment""]',
            '[data-pagelet=""VideoComment""]',
            '[data-pagelet*=""Comment""]'
        ];
        
        for (var p = 0; p < pageletSelectors.length; p++) {
            try {
                var el = document.querySelector(pageletSelectors[p]);
                if (el && isValidCommentContainer(el)) {
                    log('Container bulundu (pagelet): ' + pageletSelectors[p]);
                    return el;
                }
            } catch(e) {}
        }
        
        var feeds = document.querySelectorAll('[role=""feed""]');
        for (var fd = 0; fd < feeds.length; fd++) {
            if (isValidCommentContainer(feeds[fd])) {
                log('Container bulundu (role=feed)');
                return feeds[fd];
            }
        }
        
        var allDivs = document.querySelectorAll('div');
        for (var d = 0; d < allDivs.length; d++) {
            var divEl = allDivs[d];
            var style = window.getComputedStyle(divEl);
            var isScrollable = style.overflowY === 'auto' || style.overflowY === 'scroll';
            var divRect = divEl.getBoundingClientRect();
            
            if (isScrollable && 
                divRect.height > 200 && divRect.height < window.innerHeight * 1.2 &&
                divRect.width > 200 && divRect.width < window.innerWidth * 0.6 &&
                isValidCommentContainer(divEl)) {
                log('Container bulundu (scrollable)');
                return divEl;
            }
        }
        
        var rightPanels = document.querySelectorAll('[role=""complementary""], [role=""region""]');
        for (var rp = 0; rp < rightPanels.length; rp++) {
            if (isValidCommentContainer(rightPanels[rp])) {
                log('Container bulundu (complementary/region)');
                return rightPanels[rp];
            }
        }
        
        log('UYARI: Spesifik container bulunamadi! Muhtemelen yorum yok veya DOM yapisi farkli');
        return document.body;
    }
    
    function parseComment(el, debug) {
        try {
            if (el.offsetHeight < 20 || el.offsetWidth < 50) {
                if (debug) log('Parse skip: boyut cok kucuk ' + el.offsetWidth + 'x' + el.offsetHeight);
                return null;
            }
            
            if (el.querySelector('textarea, [contenteditable=""true""]')) {
                if (debug) log('Parse skip: input alani iceriyor');
                return null;
            }
            
            var id = el.getAttribute('data-commentid') || 
                     el.getAttribute('data-ft') ||
                     el.getAttribute('id') ||
                     el.getAttribute('data-testid');
            
            var author = null;
            var authorId = null;
            var avatar = null;
            var commentText = null;
            
            var ariaLinks = el.querySelectorAll('a[aria-label]');
            if (debug) log('Parse: ' + ariaLinks.length + ' aria-label link bulundu');
            
            for (var i = 0; i < ariaLinks.length; i++) {
                var link = ariaLinks[i];
                var ariaLabel = link.getAttribute('aria-label');
                var href = link.getAttribute('href') || '';
                
                if (debug) log('Parse aria-label: ' + ariaLabel + ' href: ' + href.substring(0, 50));
                
                if (href.indexOf('/videos/') >= 0 || href.indexOf('/watch/') >= 0 || href.indexOf('/live/') >= 0) continue;
                if (href.indexOf('/hashtag/') >= 0 || href.indexOf('/groups/') >= 0 || href.indexOf('/events/') >= 0) continue;
                
                if (ariaLabel && ariaLabel.length > 1 && ariaLabel.length < 50) {
                    var firstChar = ariaLabel.charAt(0);
                    if (firstChar !== '/' && firstChar !== '@' && firstChar !== '#' && !/\d/.test(firstChar)) {
                        author = ariaLabel;
                        if (debug) log('Parse: Author bulundu (aria-label): ' + author);
                        
                        var idMatch = href.match(/facebook\.com\/([a-zA-Z0-9.]+)/);
                        if (idMatch) authorId = idMatch[1];
                        break;
                    }
                }
            }
            
            if (!author) {
                var allLinks = el.querySelectorAll('a[href]');
                for (var j = 0; j < allLinks.length; j++) {
                    var lnk = allLinks[j];
                    var hr = lnk.getAttribute('href') || '';
                    
                    if (hr.indexOf('/videos/') >= 0 || hr.indexOf('/watch/') >= 0 || hr.indexOf('/live/') >= 0) continue;
                    if (hr.indexOf('/hashtag/') >= 0 || hr.indexOf('/groups/') >= 0 || hr.indexOf('/events/') >= 0) continue;
                    if (hr.indexOf('?comment_id=') >= 0 || hr.indexOf('#') >= 0) continue;
                    
                    var linkText = lnk.textContent ? lnk.textContent.trim() : '';
                    if (linkText && linkText.length > 1 && linkText.length < 50) {
                        var fc = linkText.charAt(0);
                        if (fc !== '/' && fc !== '@' && fc !== '#' && !/\d/.test(fc)) {
                            author = linkText;
                            if (debug) log('Parse: Author bulundu (link text): ' + author);
                            
                            var idM = hr.match(/facebook\.com\/([a-zA-Z0-9.]+)/);
                            if (idM) authorId = idM[1];
                            break;
                        }
                    }
                }
            }
            
            if (!author) {
                if (debug) log('Parse skip: yazar bulunamadi');
                return null;
            }
            
            var avatarSelectors = [
                'svg image[href*=""scontent""]',
                'svg image[href*=""fbcdn""]', 
                'img[src*=""scontent""]',
                'img[src*=""fbcdn""]',
                'image[href*=""scontent""]',
                'image[href*=""fbcdn""]'
            ];
            
            for (var k = 0; k < avatarSelectors.length; k++) {
                var avatarEl = el.querySelector(avatarSelectors[k]);
                if (avatarEl) {
                    avatar = avatarEl.getAttribute('href') || avatarEl.getAttribute('src');
                    if (avatar) {
                        if (debug) log('Parse: Avatar bulundu');
                        break;
                    }
                }
            }
            
            var textContainers = el.querySelectorAll('[dir=""auto""]');
            if (debug) log('Parse: ' + textContainers.length + ' dir=auto container bulundu');
            
            for (var m = 0; m < textContainers.length; m++) {
                var container = textContainers[m];
                if (container.closest('a')) continue;
                
                var content = container.textContent ? container.textContent.trim() : '';
                
                if (content && content !== author && content.length > 0 && content.length < 1000) {
                    if (/^\d+\s*(saat|dakika|saniye|gun|hafta|ay|yil|h|m|s|d|w)/i.test(content)) continue;
                    if (/^(just now|now|\d+\s*(hours?|minutes?|seconds?|days?|weeks?)\s*ago)$/i.test(content)) continue;
                    
                    var lowerContent = content.toLowerCase();
                    if (lowerContent.indexOf('canli yayin') >= 0 || 
                        lowerContent.indexOf('simdi canli') >= 0 ||
                        lowerContent.indexOf('yayinda') >= 0 ||
                        lowerContent.indexOf('yanitla') >= 0 ||
                        lowerContent.indexOf('begen') >= 0 ||
                        lowerContent.indexOf('paylas') >= 0 ||
                        lowerContent.indexOf('like') >= 0 ||
                        lowerContent.indexOf('reply') >= 0 ||
                        lowerContent.indexOf('share') >= 0 ||
                        lowerContent.indexOf('izleyici') >= 0 ||
                        lowerContent.indexOf('viewer') >= 0) {
                        if (debug) log('Parse: UI metni atlandi: ' + content.substring(0, 30));
                        continue;
                    }
                    
                    if (debug) log('Parse: Potansiyel mesaj: ' + content.substring(0, 50));
                    
                    if (!commentText || content.length > commentText.length) {
                        commentText = content;
                    }
                }
            }
            
            if (!commentText) {
                var spans = el.querySelectorAll('span');
                for (var n = 0; n < spans.length; n++) {
                    var span = spans[n];
                    if (span.closest('a')) continue;
                    
                    var spContent = span.textContent ? span.textContent.trim() : '';
                    if (spContent && spContent !== author && spContent.length > 0 && spContent.length < 500) {
                        if (!/^\d+\s*(saat|dakika|saniye|gun|hafta|h|m|s|d|w)/i.test(spContent)) {
                            if (!commentText || spContent.length > commentText.length) {
                                commentText = spContent;
                            }
                        }
                    }
                }
            }
            
            if (!commentText || commentText.length === 0) {
                if (debug) log('Parse skip: mesaj bulunamadi, author=' + author);
                return null;
            }
            
            if (commentText === author) {
                if (debug) log('Parse skip: mesaj = yazar adi');
                return null;
            }
            
            if (commentText.indexOf(author) >= 0 && commentText.length > 100) {
                commentText = commentText.replace(author, '').trim();
                commentText = commentText.replace(/\n+/g, ' ').trim();
                if (debug) log('Parse: Mesaj temizlendi: ' + commentText.substring(0, 50));
            }
            
            if (!commentText || commentText.length < 1) {
                if (debug) log('Parse skip: temizlemeden sonra mesaj bos');
                return null;
            }
            
            if (!id) {
                var hash = 0;
                var str = author + commentText;
                for (var h = 0; h < str.length; h++) {
                    hash = ((hash << 5) - hash) + str.charCodeAt(h);
                    hash = hash & hash;
                }
                id = 'fb_' + Math.abs(hash) + '_' + Date.now().toString(36);
            }
            
            if (seenIds.has(id)) {
                if (debug) log('Parse skip: duplicate id');
                return null;
            }
            
            seenIds.add(id);
            
            log('Yorum parse edildi: ' + author + ': ' + commentText.substring(0, 50));
            
            return {
                id: id,
                author: author,
                authorId: authorId,
                message: commentText,
                avatar: avatar,
                timestamp: new Date().toISOString(),
                platform: 'Facebook'
            };
        } catch (e) {
            log('Parse error: ' + e.message);
            return null;
        }
    }
    
    function analyzeDOM() {
        if (!commentContainer) {
            domDebug('Container yok!');
            return;
        }
        
        var containerRect = commentContainer.getBoundingClientRect();
        domDebug('Container boyutu: ' + Math.round(containerRect.width) + 'x' + Math.round(containerRect.height) + 
                 ', tag: ' + commentContainer.tagName + 
                 ', class: ' + (commentContainer.className || 'none').substring(0, 50));
        
        var directChildren = commentContainer.children.length;
        var allDivs = commentContainer.querySelectorAll('div').length;
        var allSpans = commentContainer.querySelectorAll('span').length;
        var allLinks = commentContainer.querySelectorAll('a').length;
        var dirAutos = commentContainer.querySelectorAll('[dir=""auto""]').length;
        var articles = commentContainer.querySelectorAll('[role=""article""]').length;
        var avatars = commentContainer.querySelectorAll('img[src*=""scontent""], img[src*=""fbcdn""], image[href*=""scontent""], image[href*=""fbcdn""], svg image').length;
        
        domDebug('Container analizi: ' + directChildren + ' child, ' + 
                 allDivs + ' div, ' + allSpans + ' span, ' + 
                 allLinks + ' link, ' + dirAutos + ' dir=auto, ' +
                 articles + ' article, ' + avatars + ' avatar');
        
        var firstChildren = [];
        for (var c = 0; c < Math.min(commentContainer.children.length, 3); c++) {
            firstChildren.push(commentContainer.children[c]);
        }
        for (var i = 0; i < firstChildren.length; i++) {
            var child = firstChildren[i];
            var hasText = child.textContent ? child.textContent.trim().substring(0, 50) : '';
            var hasLink = child.querySelector('a') ? 'link+' : '';
            var hasImg = child.querySelector('img, image') ? 'img+' : '';
            var hasAvatar = child.querySelector('img[src*=""scontent""], img[src*=""fbcdn""], image[href*=""scontent""]') ? 'avatar+' : '';
            domDebug('Child[' + i + ']: ' + hasLink + hasImg + hasAvatar + ' text=' + hasText);
        }
    }
    
    function scanExistingComments() {
        if (!commentContainer) {
            log('scanExistingComments: Container yok!');
            return;
        }
        
        log('Container bulundu, mevcut yorumlar taraniyor...');
        analyzeDOM();
        
        var comments = [];
        var isBodyContainer = commentContainer === document.body;
        var searchRoot = isBodyContainer ? document : commentContainer;
        
        var blocks = searchRoot.querySelectorAll('[role=""article""]');
        log('Strateji 1 (article): ' + blocks.length + ' element');
        
        for (var i = 0; i < blocks.length; i++) {
            var comment = parseComment(blocks[i], true);
            if (comment) {
                comments.push(comment);
            }
        }
        
        if (comments.length === 0) {
            blocks = searchRoot.querySelectorAll('[data-commentid]');
            log('Strateji 2 (data-commentid): ' + blocks.length + ' element');
            
            for (var j = 0; j < blocks.length; j++) {
                var c = parseComment(blocks[j], false);
                if (c) {
                    comments.push(c);
                }
            }
        }
        
        if (comments.length === 0) {
            var avatarEls = searchRoot.querySelectorAll('img[src*=""scontent""], img[src*=""fbcdn""], image[href*=""scontent""], image[href*=""fbcdn""]');
            log('Strateji 3 (avatar parents): ' + avatarEls.length + ' avatar');
            
            var filteredAvatars = [];
            for (var fa = 0; fa < avatarEls.length; fa++) {
                var rect = avatarEls[fa].getBoundingClientRect();
                if (rect.width > 15 && rect.width < 100 && rect.height > 15 && rect.height < 100) {
                    filteredAvatars.push(avatarEls[fa]);
                }
            }
            log('Strateji 3 (filtered avatars): ' + filteredAvatars.length + ' avatar');
            
            var checkedParents = new Set();
            for (var k = 0; k < filteredAvatars.length; k++) {
                var parent = filteredAvatars[k].parentElement;
                for (var lvl = 0; lvl < 5 && parent; lvl++) {
                    if (checkedParents.has(parent)) {
                        parent = parent.parentElement;
                        continue;
                    }
                    checkedParents.add(parent);
                    
                    var hasText = parent.querySelector('[dir=""auto""]');
                    var hasLink = parent.querySelector('a[href]');
                    
                    if (hasText && hasLink) {
                        var cmt = parseComment(parent, false);
                        if (cmt) {
                            comments.push(cmt);
                            break;
                        }
                    }
                    parent = parent.parentElement;
                }
            }
        }
        
        if (comments.length === 0 && !isBodyContainer) {
            blocks = commentContainer.querySelectorAll(':scope > div, :scope > div > div');
            log('Strateji 4 (direct/nested div): ' + blocks.length + ' element');
            
            for (var m = 0; m < blocks.length; m++) {
                var el = blocks[m];
                if (el.querySelector('a[href]') && el.textContent && el.textContent.trim().length > 5) {
                    var cm = parseComment(el, false);
                    if (cm) {
                        comments.push(cm);
                    }
                }
            }
        }
        
        if (comments.length === 0) {
            var dirAutoEls = searchRoot.querySelectorAll('[dir=""auto""]');
            log('Strateji 5 (dir=auto parents): ' + dirAutoEls.length + ' element');
            
            var parentSet = new Set();
            for (var n = 0; n < dirAutoEls.length; n++) {
                var da = dirAutoEls[n];
                if (da.closest('textarea, [contenteditable=""true""], [role=""textbox""]')) continue;
                
                var pr = da.parentElement;
                for (var p = 0; p < 4 && pr && pr !== document.body; p++) {
                    if (!parentSet.has(pr)) {
                        parentSet.add(pr);
                        var co = parseComment(pr, false);
                        if (co) {
                            comments.push(co);
                            break;
                        }
                    }
                    pr = pr.parentElement;
                }
            }
        }
        
        var uniqueComments = [];
        var seenTexts = new Set();
        for (var u = 0; u < comments.length; u++) {
            var key = comments[u].author + ':' + comments[u].message;
            if (!seenTexts.has(key)) {
                seenTexts.add(key);
                uniqueComments.push(comments[u]);
            }
        }
        
        if (uniqueComments.length > 0) {
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'batch',
                comments: uniqueComments
            }));
            log('Toplam ' + uniqueComments.length + ' benzersiz yorum bulundu ve gonderildi');
        } else {
            log('Hic yorum parse edilemedi! DOM yapisi farkli olabilir.');
            var sampleHTML = commentContainer.innerHTML.substring(0, 1000);
            domDebug('Sample HTML (1000 char): ' + sampleHTML.replace(/\n/g, ' ').replace(/\s+/g, ' '));
        }
    }
    
    function handleMutations(mutations) {
        var newComments = [];
        var checkedElements = new Set();
        
        for (var i = 0; i < mutations.length; i++) {
            var mutation = mutations[i];
            if (mutation.type === 'childList') {
                for (var j = 0; j < mutation.addedNodes.length; j++) {
                    var node = mutation.addedNodes[j];
                    if (node.nodeType !== Node.ELEMENT_NODE) continue;
                    if (checkedElements.has(node)) continue;
                    checkedElements.add(node);
                    
                    if (node.matches && node.matches('textarea, input, [contenteditable]')) continue;
                    
                    var comment = parseComment(node, false);
                    if (comment) {
                        newComments.push(comment);
                        continue;
                    }
                    
                    var innerElements = node.querySelectorAll('[role=""article""], [data-commentid]');
                    for (var k = 0; k < innerElements.length; k++) {
                        var el = innerElements[k];
                        if (checkedElements.has(el)) continue;
                        checkedElements.add(el);
                        
                        var c = parseComment(el, false);
                        if (c) {
                            newComments.push(c);
                        }
                    }
                    
                    var avatarEls = node.querySelectorAll('image[href*=""fbcdn""], img[src*=""fbcdn""]');
                    for (var m = 0; m < avatarEls.length; m++) {
                        var parent = avatarEls[m].parentElement;
                        for (var lvl = 0; lvl < 5 && parent; lvl++) {
                            if (checkedElements.has(parent)) {
                                parent = parent.parentElement;
                                continue;
                            }
                            checkedElements.add(parent);
                            
                            var cm = parseComment(parent, false);
                            if (cm) {
                                newComments.push(cm);
                                break;
                            }
                            parent = parent.parentElement;
                        }
                    }
                }
            }
        }
        
        var uniqueComments = [];
        var seenTexts = new Set();
        for (var u = 0; u < newComments.length; u++) {
            var key = newComments[u].author + ':' + newComments[u].message;
            if (!seenTexts.has(key)) {
                seenTexts.add(key);
                uniqueComments.push(newComments[u]);
            }
        }
        
        for (var v = 0; v < uniqueComments.length; v++) {
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'comment',
                id: uniqueComments[v].id,
                author: uniqueComments[v].author,
                authorId: uniqueComments[v].authorId,
                message: uniqueComments[v].message,
                avatar: uniqueComments[v].avatar,
                timestamp: uniqueComments[v].timestamp,
                platform: uniqueComments[v].platform
            }));
        }
        
        if (uniqueComments.length > 0) {
            log('Mutation: ' + uniqueComments.length + ' yeni yorum algilandi');
        }
    }
    
    function startObserver() {
        log('startObserver cagrildi, retry: ' + retryCount);
        log('Document state: ' + document.readyState);
        log('URL: ' + window.location.href);
        
        commentContainer = findCommentContainer();
        
        if (!commentContainer) {
            retryCount++;
            if (retryCount < maxRetries) {
                log('Container bulunamadi, ' + retryCount + '/' + maxRetries + ' - 2sn sonra tekrar');
                setTimeout(startObserver, 2000);
                return;
            }
            
            log('Container bulunamadi (max retry asildi)');
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'error',
                message: 'Comment container bulunamadi (' + maxRetries + ' deneme)'
            }));
            
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'init',
                status: 'no_container'
            }));
            return;
        }
        
        scanExistingComments();
        
        log('MutationObserver kuruldu');
        observer = new MutationObserver(handleMutations);
        observer.observe(commentContainer, {
            childList: true,
            subtree: true
        });
        
        var bodyObserver = new MutationObserver(function() {
            if (!document.contains(commentContainer)) {
                log('Container kayboldu, yeniden araniyor...');
                if (observer) observer.disconnect();
                commentContainer = null;
                retryCount = 0;
                setTimeout(startObserver, 2000);
            }
        });
        bodyObserver.observe(document.body, { childList: true, subtree: true });
        
        window.__fbLiveChatObserver = observer;
        window.__fbLiveScanComments = scanExistingComments;
        window.__fbLiveAnalyzeDOM = analyzeDOM;
        
        window.chrome.webview.postMessage(JSON.stringify({
            type: 'init',
            status: 'started'
        }));
        
        log('Observer basariyla baslatildi!');
    }
    
    log('Script yuklendi, baslatma bekleniyor...');
    
    if (document.readyState === 'complete') {
        log('Document ready, 2 saniye sonra baslatilacak');
        setTimeout(startObserver, 2000);
    } else {
        log('Document loading, load event bekleniyor');
        window.addEventListener('load', function() {
            log('Load event fired, 2 saniye sonra baslatilacak');
            setTimeout(startObserver, 2000);
        });
    }
})();
";
        }

        protected override Task DisconnectAsync()
        {
            _observerInjected = false;
            _seenCommentIds.Clear();
            _unregisterMessageHandler?.Invoke();

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
            while (!ct.IsCancellationRequested && State == ConnectionState.Connected)
            {
                try
                {
                    await Task.Delay(30000, ct);

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