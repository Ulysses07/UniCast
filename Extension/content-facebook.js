/**
 * UniCast Chat Bridge - Facebook Content Script
 * Facebook Live sayfasındaki yorumları izler ve UniCast'e gönderir
 * 
 * v1.0 - Facebook Live DOM yapısı için optimize edilmiş
 */

(function() {
    'use strict';

    const UNICAST_WS_PORT = 9876;
    const RECONNECT_INTERVAL = 3000;
    const SCAN_INTERVAL = 500;
    const SEEN_COMMENTS = new Set();
    const MAX_SEEN_CACHE = 500;

    let ws = null;
    let isConnected = false;
    let observer = null;
    let scanTimer = null;
    let reconnectTimer = null;
    let debugMode = true;

    function log(...args) {
        if (debugMode) {
            console.log('[UniCast Facebook]', ...args);
        }
    }

    function logError(...args) {
        console.error('[UniCast Facebook]', ...args);
    }

    /**
     * WebSocket bağlantısını başlat
     */
    function connectWebSocket() {
        if (ws && ws.readyState === WebSocket.OPEN) {
            return;
        }

        try {
            log('WebSocket bağlantısı deneniyor...');
            ws = new WebSocket(`ws://localhost:${UNICAST_WS_PORT}/facebook`);

            ws.onopen = () => {
                log('WebSocket bağlandı ✓');
                isConnected = true;
                clearTimeout(reconnectTimer);
                
                sendMessage({
                    type: 'connected',
                    platform: 'facebook',
                    url: window.location.href,
                    timestamp: Date.now()
                });

                try {
                    chrome.runtime.sendMessage({ action: 'setConnected', connected: true, platform: 'facebook' });
                } catch (e) {}

                startPeriodicScan();
            };

            ws.onclose = () => {
                log('WebSocket kapandı, yeniden bağlanılıyor...');
                isConnected = false;
                stopPeriodicScan();
                try {
                    chrome.runtime.sendMessage({ action: 'setConnected', connected: false, platform: 'facebook' });
                } catch (e) {}
                scheduleReconnect();
            };

            ws.onerror = (error) => {
                logError('WebSocket hatası:', error);
                isConnected = false;
            };

            ws.onmessage = (event) => {
                try {
                    const data = JSON.parse(event.data);
                    handleServerMessage(data);
                } catch (e) {
                    logError('Mesaj parse hatası:', e);
                }
            };

        } catch (error) {
            logError('WebSocket bağlantı hatası:', error);
            scheduleReconnect();
        }
    }

    function scheduleReconnect() {
        if (reconnectTimer) clearTimeout(reconnectTimer);
        reconnectTimer = setTimeout(connectWebSocket, RECONNECT_INTERVAL);
    }

    function sendMessage(data) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify(data));
            return true;
        }
        return false;
    }

    function handleServerMessage(data) {
        switch (data.type) {
            case 'ping':
                sendMessage({ type: 'pong' });
                break;
            case 'getStatus':
                sendMessage({
                    type: 'status',
                    platform: 'facebook',
                    observing: observer !== null,
                    commentCount: SEEN_COMMENTS.size,
                    url: window.location.href
                });
                break;
        }
    }

    /**
     * Benzersiz yorum hash'i oluştur
     */
    function createCommentHash(username, text) {
        const str = `${username}:${text}`.toLowerCase().trim();
        let hash = 0;
        for (let i = 0; i < str.length; i++) {
            const char = str.charCodeAt(i);
            hash = ((hash << 5) - hash) + char;
            hash = hash & hash;
        }
        return hash.toString(36);
    }

    /**
     * Facebook Live yorumlarını tara
     * Facebook DOM yapısı karmaşık, birden fazla strateji kullanıyoruz
     */
    function scanForComments() {
        const comments = [];
        const foundPairs = new Set();

        // Strateji 1: Facebook'un yorum container class'ları
        const selectors = [
            // Live video yorumları
            '[class*="x1lliihq"][class*="x1n2onr6"]', // Modern FB class pattern
            'div[role="article"]',
            '[data-testid="UFI2Comment/root_depth_0"]',
            '[data-testid="comment"]',
            // Eski FB yapısı
            '.UFICommentContent',
            '.UFICommentBody',
            // Live specific
            '[class*="live-video-comment"]',
            '[class*="comment-item"]'
        ];

        for (const selector of selectors) {
            try {
                document.querySelectorAll(selector).forEach(item => {
                    const result = extractFromFacebookComment(item);
                    if (result && !foundPairs.has(result.pairKey)) {
                        foundPairs.add(result.pairKey);
                        comments.push(result.comment);
                    }
                });
            } catch (e) {
                // Selector geçersiz olabilir
            }
        }

        // Strateji 2: Genel div taraması - Facebook'un dinamik class'ları için
        if (comments.length === 0) {
            // Yorum içeren container'ları bul
            document.querySelectorAll('div').forEach(div => {
                // Facebook yorumları genellikle:
                // - Bir link (kullanıcı adı) içerir
                // - Yanında metin içerir
                const links = div.querySelectorAll('a[role="link"], a[href*="/profile"], a[href*="facebook.com/"]');
                
                links.forEach(link => {
                    const username = link.textContent?.trim();
                    if (!username || username.length > 50) return;
                    
                    // Link'in parent'ında mesaj ara
                    const parent = link.closest('div');
                    if (!parent) return;
                    
                    // Mesaj metni - link dışındaki text
                    const fullText = parent.innerText || '';
                    let message = fullText.replace(username, '').trim();
                    
                    // Zaman bilgisini temizle (örn: "2 dk", "1 sa")
                    message = message.replace(/^\d+\s*(dk|sa|gün|sn|m|h|d|s)\s*/i, '').trim();
                    
                    if (isValidFacebookComment(username, message)) {
                        const pairKey = `${username}|${message}`;
                        if (!foundPairs.has(pairKey)) {
                            foundPairs.add(pairKey);
                            comments.push({
                                username: username,
                                text: message,
                                source: 'link-parent'
                            });
                        }
                    }
                });
            });
        }

        // Strateji 3: Span çiftleri (Instagram'daki gibi)
        document.querySelectorAll('div').forEach(div => {
            const childSpans = Array.from(div.children).filter(el => el.tagName === 'SPAN');
            
            if (childSpans.length === 2) {
                const username = childSpans[0]?.textContent?.trim();
                const message = childSpans[1]?.textContent?.trim();
                
                if (isValidFacebookComment(username, message)) {
                    const pairKey = `${username}|${message}`;
                    if (!foundPairs.has(pairKey)) {
                        foundPairs.add(pairKey);
                        comments.push({
                            username: username,
                            text: message,
                            source: 'span-pair'
                        });
                    }
                }
            }
        });

        return comments;
    }

    /**
     * Facebook yorum elementinden veri çıkar
     */
    function extractFromFacebookComment(item) {
        // Username için link ara
        const usernameSelectors = [
            'a[role="link"]',
            'a[href*="/profile"]',
            'a[href*="facebook.com/"]',
            'span[dir="auto"] a',
            '.UFICommentActorName',
            '[class*="author"]'
        ];

        let username = null;
        let usernameEl = null;

        for (const sel of usernameSelectors) {
            const el = item.querySelector(sel);
            if (el && el.textContent?.trim()) {
                username = el.textContent.trim();
                usernameEl = el;
                // Çok uzun veya URL gibi görünüyorsa atla
                if (username.length < 50 && !username.includes('http')) {
                    break;
                }
                username = null;
            }
        }

        if (!username) return null;

        // Message - username dışındaki text
        let message = '';
        
        // Mesaj için özel selector'lar dene
        const messageSelectors = [
            '[class*="comment-text"]',
            '[class*="CommentBody"]',
            '.UFICommentBody',
            'span[dir="auto"]:not(:first-child)',
            'div[dir="auto"]'
        ];

        for (const sel of messageSelectors) {
            const el = item.querySelector(sel);
            if (el && el.textContent?.trim() && el !== usernameEl) {
                message = el.textContent.trim();
                break;
            }
        }

        // Fallback: tüm text'ten username'i çıkar
        if (!message) {
            const fullText = item.innerText || '';
            const lines = fullText.split('\n').filter(l => l.trim());
            
            for (const line of lines) {
                const trimmed = line.trim();
                if (trimmed !== username && 
                    !isUIElement(trimmed) &&
                    trimmed.length > 0 && 
                    trimmed.length < 500) {
                    message = trimmed;
                    break;
                }
            }
        }

        if (isValidFacebookComment(username, message)) {
            return {
                pairKey: `${username}|${message}`,
                comment: {
                    username: username,
                    text: message,
                    source: 'fb-extract'
                }
            };
        }

        return null;
    }

    function isUIElement(text) {
        const uiTexts = [
            'Beğen', 'Like', 'Yanıtla', 'Reply', 'Paylaş', 'Share',
            'Gizle', 'Hide', 'Bildir', 'Report', 'Sabitle', 'Pin',
            'Yorum yap', 'Comment', 'Görüntüle', 'View',
            'dk', 'sa', 'gün', 'sn', 'm', 'h', 'd', 's',
            'CANLI', 'LIVE', 'izliyor', 'watching', 'izleyici', 'viewers'
        ];
        
        const lower = text.toLowerCase();
        return uiTexts.some(ui => lower === ui.toLowerCase() || /^\d+\s*(dk|sa|gün|sn)$/i.test(text));
    }

    function isValidFacebookComment(username, message) {
        if (!username || !message) return false;
        if (username.length === 0 || username.length > 50) return false;
        if (message.length === 0 || message.length > 1000) return false;
        if (username === message) return false;
        if (isUIElement(username) || isUIElement(message)) return false;
        if (username.includes('http') || username.includes('www.')) return false;
        
        return true;
    }

    /**
     * Yorumları işle ve gönder
     */
    function processComments(comments) {
        let newCount = 0;

        comments.forEach(({ username, text, source }) => {
            const hash = createCommentHash(username, text);
            
            if (!SEEN_COMMENTS.has(hash)) {
                SEEN_COMMENTS.add(hash);
                newCount++;

                const commentData = {
                    id: `fb-${Date.now()}-${hash}`,
                    username: username,
                    text: text,
                    timestamp: Date.now(),
                    platform: 'facebook'
                };

                log(`✓ Yeni yorum [${source}]: ${username}: ${text.substring(0, 50)}${text.length > 50 ? '...' : ''}`);

                if (sendMessage({ type: 'comment', data: commentData })) {
                    log('  → Gönderildi');
                } else {
                    log('  → HATA: WebSocket bağlı değil');
                }
            }
        });

        // Bellek temizliği
        if (SEEN_COMMENTS.size > MAX_SEEN_CACHE) {
            const arr = Array.from(SEEN_COMMENTS);
            arr.splice(0, arr.length - MAX_SEEN_CACHE / 2);
            SEEN_COMMENTS.clear();
            arr.forEach(h => SEEN_COMMENTS.add(h));
        }

        return newCount;
    }

    /**
     * Periyodik tarama başlat
     */
    function startPeriodicScan() {
        if (scanTimer) clearInterval(scanTimer);

        log('Periyodik tarama başlatıldı (' + SCAN_INTERVAL + 'ms aralıkla)');

        // İlk tarama
        const comments = scanForComments();
        log(`İlk tarama: ${comments.length} yorum bulundu`);
        if (comments.length > 0) {
            log('Bulunan yorumlar:', comments);
        }
        processComments(comments);

        // Periyodik tarama
        scanTimer = setInterval(() => {
            const comments = scanForComments();
            processComments(comments);
        }, SCAN_INTERVAL);
    }

    function stopPeriodicScan() {
        if (scanTimer) {
            clearInterval(scanTimer);
            scanTimer = null;
        }
    }

    /**
     * MutationObserver - DOM değişikliklerini izle
     */
    function startObserver() {
        if (observer) observer.disconnect();

        // Facebook'ta yorum alanını bulmaya çalış
        const commentContainer = 
            document.querySelector('[class*="comment"]') ||
            document.querySelector('[role="complementary"]') ||
            document.querySelector('[role="main"]') || 
            document.body;

        log('Observer başlatılıyor:', commentContainer.tagName || 'body');

        observer = new MutationObserver((mutations) => {
            let hasNewNodes = false;
            for (const mutation of mutations) {
                if (mutation.addedNodes.length > 0) {
                    hasNewNodes = true;
                    break;
                }
            }

            if (hasNewNodes) {
                const comments = scanForComments();
                processComments(comments);
            }
        });

        observer.observe(commentContainer, {
            childList: true,
            subtree: true
        });

        log('MutationObserver aktif');
    }

    /**
     * Facebook Live sayfası mı kontrol et
     */
    function isFacebookLivePage() {
        const url = window.location.href;
        return url.includes('/videos/') || 
               url.includes('/watch/live') ||
               url.includes('/watch/?v=') ||
               url.includes('live') ||
               document.querySelector('[data-testid="live_video"]') !== null;
    }

    /**
     * Başlatma
     */
    function init() {
        log('=========================================');
        log('UniCast Facebook Bridge v1.0');
        log('Facebook sayfası tespit edildi');
        log('URL:', window.location.href);
        log('Live sayfa:', isFacebookLivePage() ? 'Evet' : 'Muhtemelen');
        log('=========================================');

        connectWebSocket();

        setTimeout(() => {
            startObserver();
        }, 2000);

        // SPA navigation izle (Facebook SPA kullanıyor)
        let lastUrl = location.href;
        new MutationObserver(() => {
            const url = location.href;
            if (url !== lastUrl) {
                lastUrl = url;
                log('Sayfa değişti:', url);
                
                if (isFacebookLivePage()) {
                    setTimeout(() => {
                        SEEN_COMMENTS.clear();
                        startPeriodicScan();
                    }, 2000);
                }
            }
        }).observe(document, { subtree: true, childList: true });
    }

    // Başlat
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    // Debug için global erişim
    window.__unicastFacebook = {
        scan: () => {
            const comments = scanForComments();
            console.log('Bulunan yorumlar:', comments);
            return comments;
        },
        send: sendMessage,
        status: () => ({
            connected: isConnected,
            wsState: ws?.readyState,
            seenCount: SEEN_COMMENTS.size,
            isLive: isFacebookLivePage()
        }),
        forceSend: () => {
            const comments = scanForComments();
            comments.forEach(c => {
                sendMessage({ type: 'comment', data: {
                    id: `fb-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
                    username: c.username,
                    text: c.text,
                    timestamp: Date.now(),
                    platform: 'facebook'
                }});
            });
            return `${comments.length} yorum gönderildi`;
        },
        debug: (enable = true) => {
            debugMode = enable;
            return `Debug modu: ${enable ? 'Açık' : 'Kapalı'}`;
        }
    };

    log('Debug: window.__unicastFacebook.scan() ile manuel tarama yapabilirsiniz');

})();
