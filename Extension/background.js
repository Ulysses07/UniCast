/**
 * UniCast Chat Bridge - Background Service Worker
 * Extension durumunu ve badge'i yönetir
 */

let isConnected = false;

// Badge renklerini ayarla
function updateBadge(connected) {
    isConnected = connected;
    
    if (connected) {
        chrome.action.setBadgeText({ text: '●' });
        chrome.action.setBadgeBackgroundColor({ color: '#22c55e' }); // Yeşil
    } else {
        chrome.action.setBadgeText({ text: '○' });
        chrome.action.setBadgeBackgroundColor({ color: '#ef4444' }); // Kırmızı
    }
}

// Content script'ten mesaj al
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    switch (message.action) {
        case 'setConnected':
            updateBadge(message.connected);
            break;
        case 'getStatus':
            sendResponse({ connected: isConnected });
            break;
    }
    return true;
});

// Extension yüklendiğinde
chrome.runtime.onInstalled.addListener(() => {
    console.log('[UniCast Bridge] Extension yüklendi');
    updateBadge(false);
});

// Extension başlatıldığında
chrome.runtime.onStartup.addListener(() => {
    updateBadge(false);
});

// Başlangıçta badge'i ayarla
updateBadge(false);
