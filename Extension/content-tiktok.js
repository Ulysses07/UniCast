/**
 * UniCast Chat Bridge - TikTok Content Script
 * TikTok Live sayfasındaki yorumları izler ve UniCast'e gönderir
 * 
 * v1.0 - TikTok Live DOM yapısı için optimize edilmiş
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
            console.log('[UniCast TikTok]', ...args);
        }
    }

    function logError(...args) {
        console.error('[UniCast TikTok]', ...args);
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
            ws = new WebSocket(`ws://localhost:${UNICAST_WS_PORT}/tiktok`);

            ws.onopen = () => {
                log('WebSocket bağlandı ✓');
                isConnected = true;
                clearTimeout(reconnectTimer);
                
                // Kullanıcı adını URL'den çıkar
                const username = extractUsername();
                
                sendMessage({
                    type: 'connected',
                    platform: 'tiktok',
                    username: username,
                    url: window.location.href,
                    timestamp: Date.now()
                });

                try {
                    chrome.runtime.sendMessage({ action: 'setConnected', connected: true, platform: 'tiktok' });
                } catch (e) {}

                startPeriodicScan();
            };

            ws.onclose = () => {
                log('WebSocket kapandı, yeniden bağlanılıyor...');
                isConnected = false;
                stopPeriodicScan();
                try {
                    chrome.runtime.sendMessage({ action: 'setConnected', connected: false, platform: 'tiktok' });
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

    function extractUsername() {
        const match = window.location.pathname.match(/@([^/]+)/);
        return match ? match[1] : 'unknown';
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
                    platform: 'tiktok',
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
     * TikTok Live yorumlarını tara
     * TikTok DOM yapısı çeşitli class'lar kullanır
     */
    function scanForComments() {
        const comments = [];
        const foundPairs = new Set();

        // Strateji 1: Comment container class'larını ara
        const selectors = [
            '[class*="DivCommentItemContainer"]',
            '[class*="comment-item"]',
            '[data-e2e="chat-message"]',
            '[class*="ChatMessage"]',
            '[class*="tiktok-"][class*="comment"]'
        ];

        for (const selector of selectors) {
            document.querySelectorAll(selector).forEach(item => {
                const result = extractFromCommentItem(item);
                if (result && !foundPairs.has(result.pairKey)) {
                    foundPairs.add(result.pairKey);
                    comments.push(result.comment);
                }
            });
        }

        // Strateji 2: Genel div taraması - username:message yapısı
        if (comments.length === 0) {
            document.querySelectorAll('div').forEach(div => {
                // Username span ve message span ara
                const spans = div.querySelectorAll('span');
                if (spans.length >= 2) {
                    for (let i = 0; i < spans.length - 1; i++) {
                        const usernameSpan = spans[i];
                        const messageSpan = spans[i + 1];
                        
                        const username = usernameSpan?.textContent?.trim();
                        const message = messageSpan?.textContent?.trim();
                        
                        if (isValidComment(username, message)) {
                            const pairKey = `${username}|${message}`;
                            if (!foundPairs.has(pairKey)) {
                                foundPairs.add(pairKey);
                                comments.push({
                                    username: cleanUsername(username),
                                    text: message,
                                    source: 'span-pair'
                                });
                            }
                        }
                    }
                }
            });
        }

        // Strateji 3: Chat list container
        const chatLists = document.querySelectorAll('[class*="ChatList"], [class*="chat-list"], [data-e2e="chat-list"]');
        chatLists.forEach(list => {
            const items = list.children;
            for (const item of items) {
                const result = extractFromCommentItem(item);
                if (result && !foundPairs.has(result.pairKey)) {
                    foundPairs.add(result.pairKey);
                    comments.push(result.comment);
                }
            }
        });

        return comments;
    }

    /**
     * Comment item'dan username ve message çıkar
     */
    function extractFromCommentItem(item) {
        // Username selectors
        const usernameSelectors = [
            '[class*="SpanUserNameText"]',
            '[class*="user-name"]',
            '[class*="username"]',
            '[data-e2e="comment-username"]',
            'span[class*="Name"]'
        ];

        // Message selectors
        const messageSelectors = [
            '[class*="SpanCommentText"]',
            '[class*="comment-text"]',
            '[class*="message-text"]',
            '[data-e2e="comment-text"]',
            '[class*="CommentText"]'
        ];

        let username = null;
        let message = null;

        // Username bul
        for (const sel of usernameSelectors) {
            const el = item.querySelector(sel);
            if (el) {
                username = el.textContent?.trim();
                break;
            }
        }

        // Message bul
        for (const sel of messageSelectors) {
            const el = item.querySelector(sel);
            if (el) {
                message = el.textContent?.trim();
                break;
            }
        }

        // Fallback: tüm text content'i kullan
        if (!message && username) {
            const fullText = item.textContent?.trim();
            if (fullText && fullText.startsWith(username)) {
                message = fullText.substring(username.length).trim();
                // ":" işaretini kaldır
                if (message.startsWith(':')) {
                    message = message.substring(1).trim();
                }
            }
        }

        if (isValidComment(username, message)) {
            return {
                pairKey: `${username}|${message}`,
                comment: {
                    username: cleanUsername(username),
                    text: message,
                    source: 'item-extract'
                }
            };
        }

        return null;
    }

    function cleanUsername(username) {
        if (!username) return 'unknown';
        return username.replace(/^@/, '').replace(/:$/, '').trim();
    }

    function isValidComment(username, message) {
        if (!username || !message) return false;
        if (username.length === 0 || username.length > 50) return false;
        if (message.length === 0 || message.length > 1000) return false;
        if (username.includes('\n')) return false;
        if (username === message) return false;
        
        // TikTok UI elementlerini filtrele
        const uiTexts = [
            'LIVE', 'Follow', 'Share', 'Gift', 'Like', 'Comment',
            'Send', 'Rose', 'viewers', 'watching', 'joined',
            'Top', 'Gifts', 'Chat', 'Settings'
        ];
        
        for (const ui of uiTexts) {
            if (username.toLowerCase() === ui.toLowerCase()) return false;
        }
        
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
                    id: `tt-${Date.now()}-${hash}`,
                    username: username,
                    text: text,
                    timestamp: Date.now(),
                    platform: 'tiktok'
                };

                log(`✓ Yeni yorum [${source}]: @${username}: ${text.substring(0, 50)}${text.length > 50 ? '...' : ''}`);

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

        const liveContainer = document.querySelector('[class*="ChatList"]') ||
                             document.querySelector('[data-e2e="chat-list"]') ||
                             document.querySelector('[role="main"]') || 
                             document.body;

        log('Observer başlatılıyor:', liveContainer.tagName || 'body');

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

        observer.observe(liveContainer, {
            childList: true,
            subtree: true
        });

        log('MutationObserver aktif');
    }

    /**
     * Gift mesajlarını tara
     */
    function scanForGifts() {
        const gifts = [];
        
        const giftSelectors = [
            '[class*="GiftCombo"]',
            '[class*="gift-combo"]',
            '[data-e2e="gift-message"]'
        ];

        for (const selector of giftSelectors) {
            document.querySelectorAll(selector).forEach(item => {
                const userEl = item.querySelector('[class*="username"], [class*="user-name"]');
                const giftEl = item.querySelector('[class*="gift-name"], [class*="GiftName"]');
                const countEl = item.querySelector('[class*="combo-count"], [class*="count"]');

                if (userEl && giftEl) {
                    gifts.push({
                        username: cleanUsername(userEl.textContent),
                        giftName: giftEl.textContent?.trim() || 'Gift',
                        count: parseInt(countEl?.textContent) || 1
                    });
                }
            });
        }

        return gifts;
    }

    /**
     * Başlatma
     */
    function init() {
        log('=========================================');
        log('UniCast TikTok Bridge v1.0');
        log('TikTok Live sayfası tespit edildi');
        log('URL:', window.location.href);
        log('Yayıncı:', extractUsername());
        log('=========================================');

        connectWebSocket();

        setTimeout(() => {
            startObserver();
        }, 2000);

        // SPA navigation izle
        let lastUrl = location.href;
        new MutationObserver(() => {
            const url = location.href;
            if (url !== lastUrl) {
                lastUrl = url;
                log('Sayfa değişti:', url);
                
                if (url.includes('/live')) {
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
    window.__unicastTikTok = {
        scan: () => {
            const comments = scanForComments();
            console.log('Bulunan yorumlar:', comments);
            return comments;
        },
        gifts: () => {
            const gifts = scanForGifts();
            console.log('Bulunan gift\'ler:', gifts);
            return gifts;
        },
        send: sendMessage,
        status: () => ({
            connected: isConnected,
            wsState: ws?.readyState,
            seenCount: SEEN_COMMENTS.size,
            username: extractUsername()
        }),
        forceSend: () => {
            const comments = scanForComments();
            comments.forEach(c => {
                sendMessage({ type: 'comment', data: {
                    id: `tt-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
                    username: c.username,
                    text: c.text,
                    timestamp: Date.now(),
                    platform: 'tiktok'
                }});
            });
            return `${comments.length} yorum gönderildi`;
        }
    };

    log('Debug: window.__unicastTikTok.scan() ile manuel tarama yapabilirsiniz');

})();
