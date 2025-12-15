/**
 * UniCast Chat Bridge - Content Script
 * Instagram Live sayfasındaki yorumları izler ve UniCast'e gönderir
 * 
 * v3.0 - Instagram Live DOM yapısı: DIV > SPAN (username) + SPAN (message)
 */

(function() {
    'use strict';

    const UNICAST_WS_PORT = 9876;
    const RECONNECT_INTERVAL = 3000;
    const SCAN_INTERVAL = 500; // Her 500ms yeni yorum tara
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
            console.log('[UniCast Bridge]', ...args);
        }
    }

    function logError(...args) {
        console.error('[UniCast Bridge]', ...args);
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
            ws = new WebSocket(`ws://localhost:${UNICAST_WS_PORT}/instagram`);

            ws.onopen = () => {
                log('WebSocket bağlandı ✓');
                isConnected = true;
                clearTimeout(reconnectTimer);
                
                sendMessage({
                    type: 'connected',
                    platform: 'instagram',
                    url: window.location.href,
                    timestamp: Date.now()
                });

                try {
                    chrome.runtime.sendMessage({ action: 'setConnected', connected: true });
                } catch (e) {}

                // Taramayı başlat
                startPeriodicScan();
            };

            ws.onclose = () => {
                log('WebSocket kapandı, yeniden bağlanılıyor...');
                isConnected = false;
                stopPeriodicScan();
                try {
                    chrome.runtime.sendMessage({ action: 'setConnected', connected: false });
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
     * Instagram Live yorumlarını tara
     * Yapı: DIV > SPAN (username) + SPAN (message)
     */
    function scanForComments() {
        const comments = [];
        const foundPairs = new Set(); // Tekrar önleme

        // Ana strateji: 2 çocuk span'ı olan div'leri bul
        document.querySelectorAll('div').forEach(div => {
            // Sadece direkt çocuk span'ları al
            const childSpans = Array.from(div.children).filter(el => el.tagName === 'SPAN');
            
            if (childSpans.length === 2) {
                const username = childSpans[0]?.textContent?.trim();
                const message = childSpans[1]?.textContent?.trim();
                
                // Geçerlilik kontrolleri
                if (username && message &&
                    username.length > 0 && username.length < 50 &&
                    message.length > 0 && message.length < 1000 &&
                    !username.includes('\n') &&
                    !username.includes(' ') && // Username'de boşluk olmaz
                    username !== message && // Username ve message farklı olmalı
                    !/^(LIVE|Messages|Share|Like|Comment|Send|Follow)$/i.test(username) && // UI elementleri değil
                    !/^(LIVE|Messages|Share|Like|Comment|Send|Follow)$/i.test(message)) {
                    
                    const pairKey = `${username}|${message}`;
                    if (!foundPairs.has(pairKey)) {
                        foundPairs.add(pairKey);
                        comments.push({ 
                            username: username.replace('@', ''), 
                            text: message, 
                            source: 'div-2span' 
                        });
                    }
                }
            }
        });

        // Yedek strateji: prevSibling username olan span'lar
        document.querySelectorAll('span').forEach(span => {
            const text = span.textContent?.trim();
            const prevSibling = span.previousElementSibling;
            
            if (prevSibling?.tagName === 'SPAN' && text) {
                const username = prevSibling.textContent?.trim();
                
                if (username && text &&
                    username.length > 0 && username.length < 50 &&
                    text.length > 0 && text.length < 1000 &&
                    !username.includes(' ') &&
                    username !== text &&
                    !/^(LIVE|Messages|Share|Like|Comment|Send|Follow)$/i.test(username)) {
                    
                    const pairKey = `${username}|${text}`;
                    if (!foundPairs.has(pairKey)) {
                        foundPairs.add(pairKey);
                        comments.push({ 
                            username: username.replace('@', ''), 
                            text: text, 
                            source: 'sibling-span' 
                        });
                    }
                }
            }
        });

        return comments;
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
                    id: `ig-${Date.now()}-${hash}`,
                    username: username,
                    text: text,
                    timestamp: Date.now(),
                    platform: 'instagram'
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
            const newCount = processComments(comments);
            // Sadece yeni yorum varsa log bas (spam önleme)
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

        // Live video alanını bul
        const liveContainer = document.querySelector('[role="main"]') || 
                             document.querySelector('section') || 
                             document.body;

        log('Observer başlatılıyor:', liveContainer.tagName);

        observer = new MutationObserver((mutations) => {
            let hasNewNodes = false;
            for (const mutation of mutations) {
                if (mutation.addedNodes.length > 0) {
                    hasNewNodes = true;
                    break;
                }
            }

            if (hasNewNodes) {
                // Yeni node eklendiğinde hemen tara
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
     * Başlatma
     */
    function init() {
        log('=========================================');
        log('UniCast Bridge v3.0');
        log('Instagram Live sayfası tespit edildi');
        log('URL:', window.location.href);
        log('=========================================');

        connectWebSocket();

        setTimeout(() => {
            startObserver();
        }, 1500);

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
    window.__unicastBridge = {
        scan: () => {
            const comments = scanForComments();
            console.log('Bulunan yorumlar:', comments);
            return comments;
        },
        send: sendMessage,
        status: () => ({
            connected: isConnected,
            wsState: ws?.readyState,
            seenCount: SEEN_COMMENTS.size
        }),
        forceSend: () => {
            const comments = scanForComments();
            comments.forEach(c => {
                sendMessage({ type: 'comment', data: {
                    id: `ig-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
                    username: c.username,
                    text: c.text,
                    timestamp: Date.now(),
                    platform: 'instagram'
                }});
            });
            return `${comments.length} yorum gönderildi`;
        }
    };

    log('Debug: window.__unicastBridge.scan() ile manuel tarama yapabilirsiniz');

})();