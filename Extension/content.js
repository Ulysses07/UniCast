/**
 * UniCast Chat Bridge - Content Script
 * Instagram Live sayfasındaki yorumları izler ve UniCast'e gönderir
 */

(function() {
    'use strict';

    const UNICAST_WS_PORT = 9876;
    const RECONNECT_INTERVAL = 3000;
    const SEEN_COMMENTS = new Set();

    let ws = null;
    let isConnected = false;
    let observer = null;
    let reconnectTimer = null;

    // Instagram Live yorum selector'ları
    const SELECTORS = {
        // Ana yorum container'ları - Instagram sık değiştiriyor, birden fazla deneyelim
        commentContainer: [
            '[aria-label*="comment" i]',
            '[data-testid*="comment"]',
            'div[class*="Comment"]',
            'ul[class*="comment" i] > li',
            'div[role="list"] > div[role="listitem"]'
        ],
        // Yorum içeriği
        commentText: [
            'span[class*="x1lliihq"]',
            'span[dir="auto"]',
            'span:not([class*="username"])'
        ],
        // Kullanıcı adı
        username: [
            'span[class*="username"]',
            'a[role="link"] span',
            'span[class*="x1lliihq"]:first-child'
        ]
    };

    /**
     * WebSocket bağlantısını başlat
     */
    function connectWebSocket() {
        if (ws && ws.readyState === WebSocket.OPEN) {
            return;
        }

        try {
            ws = new WebSocket(`ws://localhost:${UNICAST_WS_PORT}/instagram`);

            ws.onopen = () => {
                console.log('[UniCast Bridge] WebSocket bağlandı');
                isConnected = true;
                clearTimeout(reconnectTimer);
                
                // Bağlantı bilgisini gönder
                sendMessage({
                    type: 'connected',
                    platform: 'instagram',
                    url: window.location.href,
                    timestamp: Date.now()
                });

                // Badge'i güncelle
                chrome.runtime.sendMessage({ action: 'setConnected', connected: true });
            };

            ws.onclose = () => {
                console.log('[UniCast Bridge] WebSocket kapandı, yeniden bağlanılıyor...');
                isConnected = false;
                chrome.runtime.sendMessage({ action: 'setConnected', connected: false });
                scheduleReconnect();
            };

            ws.onerror = (error) => {
                console.error('[UniCast Bridge] WebSocket hatası:', error);
                isConnected = false;
            };

            ws.onmessage = (event) => {
                try {
                    const data = JSON.parse(event.data);
                    handleServerMessage(data);
                } catch (e) {
                    console.error('[UniCast Bridge] Mesaj parse hatası:', e);
                }
            };

        } catch (error) {
            console.error('[UniCast Bridge] WebSocket bağlantı hatası:', error);
            scheduleReconnect();
        }
    }

    /**
     * Yeniden bağlanma zamanlayıcısı
     */
    function scheduleReconnect() {
        if (reconnectTimer) {
            clearTimeout(reconnectTimer);
        }
        reconnectTimer = setTimeout(connectWebSocket, RECONNECT_INTERVAL);
    }

    /**
     * Sunucuya mesaj gönder
     */
    function sendMessage(data) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify(data));
        }
    }

    /**
     * Sunucudan gelen mesajları işle
     */
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
     * Selector listesinden çalışan ilkini bul
     */
    function findElement(selectors, parent = document) {
        for (const selector of selectors) {
            const element = parent.querySelector(selector);
            if (element) {
                return element;
            }
        }
        return null;
    }

    /**
     * Tüm eşleşen elementleri bul
     */
    function findAllElements(selectors, parent = document) {
        for (const selector of selectors) {
            const elements = parent.querySelectorAll(selector);
            if (elements.length > 0) {
                return Array.from(elements);
            }
        }
        return [];
    }

    /**
     * Yorum elementinden veri çıkar
     */
    function extractCommentData(element) {
        try {
            // Benzersiz ID oluştur
            const id = element.getAttribute('data-comment-id') || 
                       `${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;

            // Daha önce gördüysek atla
            if (SEEN_COMMENTS.has(id)) {
                return null;
            }

            // Kullanıcı adını bul
            let username = 'Anonim';
            const usernameEl = findElement(SELECTORS.username, element);
            if (usernameEl) {
                username = usernameEl.textContent.trim().replace('@', '');
            }

            // Yorum metnini bul
            let text = '';
            const textEl = findElement(SELECTORS.commentText, element);
            if (textEl) {
                text = textEl.textContent.trim();
            }

            // Boş yorumları atla
            if (!text || text === username) {
                return null;
            }

            SEEN_COMMENTS.add(id);

            return {
                id,
                username,
                text,
                timestamp: Date.now(),
                platform: 'instagram'
            };

        } catch (error) {
            console.error('[UniCast Bridge] Yorum parse hatası:', error);
            return null;
        }
    }

    /**
     * Mevcut yorumları tara
     */
    function scanExistingComments() {
        const comments = findAllElements(SELECTORS.commentContainer);
        console.log(`[UniCast Bridge] ${comments.length} mevcut yorum bulundu`);

        for (const element of comments) {
            const data = extractCommentData(element);
            if (data) {
                sendMessage({
                    type: 'comment',
                    data
                });
            }
        }
    }

    /**
     * Yorum container'ını bul
     */
    function findCommentRoot() {
        // Live chat panelini bul
        const possibleRoots = [
            document.querySelector('[aria-label*="Live" i]'),
            document.querySelector('[data-testid*="live"]'),
            document.querySelector('section[class*="live" i]'),
            document.querySelector('div[class*="comment" i]')?.parentElement?.parentElement,
            document.body // Fallback
        ];

        for (const root of possibleRoots) {
            if (root) {
                return root;
            }
        }
        return document.body;
    }

    /**
     * MutationObserver başlat
     */
    function startObserver() {
        if (observer) {
            observer.disconnect();
        }

        const root = findCommentRoot();
        console.log('[UniCast Bridge] Observer başlatılıyor:', root);

        observer = new MutationObserver((mutations) => {
            for (const mutation of mutations) {
                for (const node of mutation.addedNodes) {
                    if (node.nodeType === Node.ELEMENT_NODE) {
                        // Yeni eklenen node bir yorum mu kontrol et
                        const isComment = SELECTORS.commentContainer.some(sel => 
                            node.matches?.(sel) || node.querySelector?.(sel)
                        );

                        if (isComment) {
                            // Direkt yorum elementi
                            let data = extractCommentData(node);
                            if (data) {
                                console.log('[UniCast Bridge] Yeni yorum:', data);
                                sendMessage({ type: 'comment', data });
                            }

                            // İç içe yorum elementleri
                            const innerComments = findAllElements(SELECTORS.commentContainer, node);
                            for (const inner of innerComments) {
                                data = extractCommentData(inner);
                                if (data) {
                                    console.log('[UniCast Bridge] Yeni iç yorum:', data);
                                    sendMessage({ type: 'comment', data });
                                }
                            }
                        }
                    }
                }
            }
        });

        observer.observe(root, {
            childList: true,
            subtree: true
        });

        console.log('[UniCast Bridge] MutationObserver aktif');
    }

    /**
     * Sayfa hazır olduğunda başlat
     */
    function init() {
        console.log('[UniCast Bridge] Instagram Live sayfası tespit edildi');
        console.log('[UniCast Bridge] URL:', window.location.href);

        // WebSocket bağlantısını başlat
        connectWebSocket();

        // Sayfa tam yüklenene kadar bekle
        setTimeout(() => {
            scanExistingComments();
            startObserver();
        }, 2000);

        // Sayfa değişikliklerini izle (SPA navigation)
        let lastUrl = location.href;
        new MutationObserver(() => {
            const url = location.href;
            if (url !== lastUrl) {
                lastUrl = url;
                console.log('[UniCast Bridge] Sayfa değişti:', url);
                
                if (url.includes('/live')) {
                    setTimeout(() => {
                        SEEN_COMMENTS.clear();
                        scanExistingComments();
                        startObserver();
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

})();
