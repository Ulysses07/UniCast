/**
 * UniCast Chat Bridge - Background Service Worker v2.1
 * Instagram, TikTok ve Facebook bağlantı durumunu yönetir
 */

let platformStatus = {
    instagram: false,
    tiktok: false,
    facebook: false
};

// Badge'i güncelle - bağlı platform sayısını göster
function updateBadge() {
    let count = 0;
    if (platformStatus.instagram) count++;
    if (platformStatus.tiktok) count++;
    if (platformStatus.facebook) count++;
    
    if (count > 0) {
        chrome.action.setBadgeText({ text: count.toString() });
        chrome.action.setBadgeBackgroundColor({ color: '#22c55e' }); // Yeşil
    } else {
        chrome.action.setBadgeText({ text: '' });
        chrome.action.setBadgeBackgroundColor({ color: '#ef4444' }); // Kırmızı
    }
}

// Content script'ten mesaj al
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    switch (message.action) {
        case 'setConnected':
            const platform = message.platform || 'instagram';
            platformStatus[platform] = message.connected;
            updateBadge();
            console.log(`[UniCast Bridge] ${platform} bağlantı: ${message.connected}`);
            break;
            
        case 'getStatus':
            sendResponse(platformStatus);
            break;
    }
    return true;
});

// Extension yüklendiğinde
chrome.runtime.onInstalled.addListener(() => {
    console.log('[UniCast Bridge] Extension v2.1 yüklendi');
    console.log('[UniCast Bridge] Desteklenen platformlar: Instagram, TikTok, Facebook');
    updateBadge();
});

// Extension başlatıldığında
chrome.runtime.onStartup.addListener(() => {
    platformStatus = { instagram: false, tiktok: false, facebook: false };
    updateBadge();
});

// Başlangıçta badge'i temizle
updateBadge();
