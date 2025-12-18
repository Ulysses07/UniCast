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
    public sealed class FacebookChatScraper : BaseChatIngestor
    {
        private readonly ConcurrentDictionary<string, bool> _seenCommentIds = new();
        private TaskCompletionSource<bool>? _initTcs;
        private bool _observerInjected;
        private static string? _cachedScript;
        private static FacebookSelectors? _cachedSelectors;

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
            if (string.IsNullOrWhiteSpace(url)) return url;
            url = url.Trim();
            if (url.Contains("fb.watch")) return url;
            if (System.Text.RegularExpressions.Regex.IsMatch(url, @"^\d+$"))
                return $"https://www.facebook.com/watch/live/?v={url}";
            if (!url.Contains("facebook.com"))
                return $"https://www.facebook.com/watch/live/?v={url}";
            return url;
        }

        protected override async Task ConnectAsync(CancellationToken ct)
        {
            if (_ensureWebViewReady == null || _navigateToUrl == null || _executeScript == null)
                throw new InvalidOperationException("WebView2 kontrolleri ayarlanmamış.");
            if (string.IsNullOrEmpty(LiveVideoUrl))
                throw new InvalidOperationException("Live Video URL gerekli.");

            // Selector'ları sunucudan çek
            try
            {
                var newSelectors = await SelectorConfigService.Instance.GetFacebookSelectorsAsync(ct);
                if (_cachedSelectors == null ||
                    _cachedSelectors.CommentContainer != newSelectors.CommentContainer)
                {
                    _cachedSelectors = newSelectors;
                    _cachedScript = null; // Yeni selector'lar geldi, script'i yeniden oluştur
                    Log.Information("[FB Scraper] Selector'lar güncellendi: {Container}", newSelectors.CommentContainer);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[FB Scraper] Selector'lar alınamadı, fallback kullanılıyor");
                _cachedSelectors ??= SelectorConfigService.Instance.GetFacebookSelectors();
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
                    json = JsonSerializer.Deserialize<string>(json) ?? json;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) return;
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
                            foreach (var comment in comments.EnumerateArray())
                                ProcessComment(comment);
                        break;
                    case "error":
                        Log.Warning("[FB Scraper] JS hatası: {Error}",
                            root.TryGetProperty("message", out var m) ? m.GetString() : "Unknown");
                        break;
                    case "debug":
                        Log.Debug("[FB Scraper] JS: {Message}",
                            root.TryGetProperty("message", out var d) ? d.GetString() : "");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FB Scraper] Message processing hatası");
            }
        }

        private void ProcessComment(JsonElement comment)
        {
            try
            {
                var id = comment.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (!string.IsNullOrEmpty(id) && _seenCommentIds.ContainsKey(id)) return;
                if (!string.IsNullOrEmpty(id)) _seenCommentIds.TryAdd(id, true);

                var author = comment.TryGetProperty("author", out var aEl) ? aEl.GetString() : "Unknown";
                var authorId = comment.TryGetProperty("authorId", out var aiEl) ? aiEl.GetString() : author;
                var text = comment.TryGetProperty("message", out var mEl) ? mEl.GetString()
                    : (comment.TryGetProperty("text", out var tEl) ? tEl.GetString() : "");
                var avatar = comment.TryGetProperty("avatar", out var avEl) ? avEl.GetString() : null;

                if (string.IsNullOrWhiteSpace(text)) return;

                PublishMessage(new ChatMessage
                {
                    Platform = ChatPlatform.Facebook,
                    Username = authorId ?? author ?? "unknown",
                    DisplayName = author ?? "Unknown",
                    Message = text,
                    AvatarUrl = avatar,
                    Timestamp = DateTime.UtcNow,
                    Metadata = string.IsNullOrEmpty(id) ? new() : new() { ["comment_id"] = id }
                });
                Log.Debug("[FB Scraper] Yorum: {Author}: {Text}", author, text);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FB Scraper] Comment parse hatası");
            }
        }

        private async Task InjectObserverAsync(CancellationToken ct)
        {
            if (_observerInjected || _executeScript == null) return;

            try
            {
                var script = GetObserverScript();
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
                    Log.Warning("[FB Scraper] Observer init timeout");
                }

                Log.Debug("[FB Scraper] Observer inject edildi");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FB Scraper] Observer inject hatası");
                throw;
            }
        }

        private static string GetObserverScript()
        {
            if (_cachedScript != null) return _cachedScript;

            // Selector'ları al
            var selectors = _cachedSelectors ?? SelectorConfigService.Instance.GetFacebookSelectors();
            var mainSelector = selectors.CommentContainer ?? ".xv55zj0.x1vvkbs";
            var fallbacks = selectors.FallbackContainers ?? new[] { "div[role='article']", "[aria-label*='yorum']" };
            var authorSelector = selectors.AuthorLink ?? "a";

            var sb = new StringBuilder();
            sb.Append("(function(){");
            sb.Append("'use strict';");
            sb.Append("if(window.__fbLiveChatObserver){");
            sb.Append("window.chrome.webview.postMessage(JSON.stringify({type:'init',status:'already_running'}));");
            sb.Append("return;}");
            sb.Append("var seenIds=new Set();");
            sb.Append("var observer=null;");

            // Selector'ları JavaScript'e aktar
            sb.Append($"var mainSelector='{EscapeJs(mainSelector)}';");
            sb.Append($"var fallbackSelectors={JsonSerializer.Serialize(fallbacks)};");
            sb.Append($"var authorSelector='{EscapeJs(authorSelector)}';");

            // log function
            sb.Append("function log(m){");
            sb.Append("window.chrome.webview.postMessage(JSON.stringify({type:'debug',message:m}));");
            sb.Append("}");

            // getContainers function - selector'ları dene
            sb.Append("function getContainers(){");
            sb.Append("var containers=document.querySelectorAll(mainSelector);");
            sb.Append("if(containers.length===0){");
            sb.Append("for(var i=0;i<fallbackSelectors.length;i++){");
            sb.Append("containers=document.querySelectorAll(fallbackSelectors[i]);");
            sb.Append("if(containers.length>0)break;");
            sb.Append("}");
            sb.Append("}");
            sb.Append("return containers;");
            sb.Append("}");

            // extractComments - DOM tabanlı (dinamik selector)
            sb.Append("function extractComments(){");
            sb.Append("var comments=[];");
            sb.Append("var containers=getContainers();");
            sb.Append("log('Selector sonucu: '+containers.length+' container');");
            sb.Append("for(var i=0;i<containers.length;i++){");
            sb.Append("var c=containers[i];");
            // Debug: container içeriğini logla
            sb.Append("var innerTextPre=c.innerText||'';");
            sb.Append("if(i<3)log('Container['+i+'] innerText (ilk 200): '+innerTextPre.substring(0,200).replace(/\\n/g,' | '));");
            sb.Append("if(i<3)log('Container['+i+'] tagName: '+c.tagName+', className: '+(c.className||'').substring(0,50));");
            // Yazar - dinamik selector
            sb.Append("var authorLink=c.querySelector(authorSelector);");
            sb.Append("var author=authorLink?authorLink.textContent.trim():'Unknown';");
            sb.Append("if(i<3)log('Container['+i+'] author: '+author);");
            // Mesaj - innerText'ten yazar adını çıkar
            sb.Append("var fullText=c.innerText||'';");
            sb.Append("var lines=fullText.split('\\n').filter(function(l){return l.trim().length>0;});");
            sb.Append("if(i<3)log('Container['+i+'] lines count: '+lines.length+', lines: '+JSON.stringify(lines.slice(0,5)));");
            sb.Append("var message='';");
            sb.Append("for(var j=0;j<lines.length;j++){");
            sb.Append("var line=lines[j].trim();");
            sb.Append("if(line!==author&&line.indexOf('·')===-1&&line!=='Beğen'&&line!=='Yanıtla'&&line!=='Paylaş'&&line!=='Sabitle'){");
            sb.Append("message=line;break;");
            sb.Append("}");
            sb.Append("}");
            sb.Append("if(i<3)log('Container['+i+'] final message: '+message);");
            // Boş mesajları atla
            sb.Append("if(!message||message.length===0)continue;");
            // ID oluştur - comment_id varsa kullan
            sb.Append("var commentId='';");
            sb.Append("if(authorLink&&authorLink.href&&authorLink.href.indexOf('comment_id=')>-1){");
            sb.Append("var m=authorLink.href.match(/comment_id=([^&]+)/);");
            sb.Append("if(m)commentId=m[1];");
            sb.Append("}");
            sb.Append("var id=commentId||('fb_'+author+'_'+message.substring(0,20));");
            // Duplicate kontrolü
            sb.Append("if(seenIds.has(id))continue;");
            // Yorum ekle
            sb.Append("comments.push({id:id,author:author,message:message,platform:'Facebook'});");
            sb.Append("}");
            sb.Append("return comments;");
            sb.Append("}");

            // scan function
            sb.Append("function scan(){");
            sb.Append("var cs=extractComments();");
            sb.Append("log('DOM tarama: '+cs.length+' yorum bulundu');");
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
            sb.Append("log(unique.length+' yeni yorum gonderildi');");
            sb.Append("}");
            sb.Append("}");
            sb.Append("}");

            // onMutation function
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
            sb.Append("author:newOnes[k].author,");
            sb.Append("message:newOnes[k].message,");
            sb.Append("platform:'Facebook'");
            sb.Append("}));");
            sb.Append("}");
            sb.Append("if(newOnes.length>0)log('Mutation: '+newOnes.length+' yeni yorum');");
            sb.Append("}");
            sb.Append("}");

            // start function
            sb.Append("function start(){");
            sb.Append("log('Observer baslatiliyor - '+window.location.href);");
            sb.Append("log('Body HTML uzunlugu: '+document.body.innerHTML.length);");
            sb.Append("log('Body text uzunlugu: '+document.body.innerText.length);");
            // Sayfada yorum var mı kontrol et
            sb.Append("var hasCommentWord=document.body.innerText.indexOf('yorum')>-1||document.body.innerText.indexOf('Yorum')>-1;");
            sb.Append("log('Sayfada yorum kelimesi: '+hasCommentWord);");
            sb.Append("scan();");
            sb.Append("observer=new MutationObserver(onMutation);");
            sb.Append("observer.observe(document.body,{childList:true,subtree:true});");
            sb.Append("window.__fbLiveChatObserver=observer;");
            sb.Append("window.__fbLiveScanComments=scan;");
            // Periyodik tarama - her 5 saniyede bir
            sb.Append("setInterval(function(){");
            sb.Append("var containers=getContainers();");
            sb.Append("log('Periyodik tarama: '+containers.length+' container');");
            sb.Append("scan();");
            sb.Append("},5000);");
            sb.Append("window.chrome.webview.postMessage(JSON.stringify({type:'init',status:'started'}));");
            sb.Append("log('Observer hazir!');");
            sb.Append("}");

            // init
            sb.Append("log('Script yuklendi');");
            sb.Append("if(document.readyState==='complete'){");
            sb.Append("setTimeout(start,2000);");
            sb.Append("}else{");
            sb.Append("window.addEventListener('load',function(){setTimeout(start,2000);});");
            sb.Append("}");
            sb.Append("})();");

            _cachedScript = sb.ToString();
            return _cachedScript;
        }

        protected override Task DisconnectAsync()
        {
            _observerInjected = false;
            _seenCommentIds.Clear();
            _unregisterMessageHandler?.Invoke();
            try
            {
                _executeScript?.Invoke("if(window.__fbLiveChatObserver){window.__fbLiveChatObserver.disconnect();window.__fbLiveChatObserver=null;}");
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
                        var r = await _executeScript("window.__fbLiveChatObserver?'active':'inactive'");
                        if (r.Contains("inactive"))
                        {
                            Log.Warning("[FB Scraper] Observer durdu, yeniden başlatılıyor...");
                            _observerInjected = false;
                            await InjectObserverAsync(ct);
                        }
                    }
                    if (_seenCommentIds.Count > 5000) _seenCommentIds.Clear();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Debug(ex, "[FB Scraper] Health check hatası"); }
            }
        }

        public async Task RescanCommentsAsync()
        {
            if (_executeScript == null) return;
            try
            {
                await _executeScript("if(typeof window.__fbLiveScanComments==='function')window.__fbLiveScanComments();");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[FB Scraper] Rescan hatası");
            }
        }

        /// <summary>
        /// JavaScript string için escape - tek tırnak ve backslash
        /// </summary>
        private static string EscapeJs(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "");
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