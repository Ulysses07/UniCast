/**
 * UniCast Chat Bridge - Popup Script
 */

document.addEventListener('DOMContentLoaded', async () => {
    const statusDot = document.getElementById('statusDot');
    const statusText = document.getElementById('statusText');

    // Background'dan durum al
    chrome.runtime.sendMessage({ action: 'getStatus' }, (response) => {
        if (response && response.connected) {
            statusDot.classList.add('connected');
            statusText.textContent = 'UniCast\'e bağlı';
        } else {
            statusDot.classList.remove('connected');
            statusText.textContent = 'UniCast bağlantısı yok';
        }
    });

    // WebSocket durumunu kontrol et
    try {
        const ws = new WebSocket('ws://localhost:9876/ping');
        ws.onopen = () => {
            statusDot.classList.add('connected');
            statusText.textContent = 'UniCast\'e bağlı';
            ws.close();
        };
        ws.onerror = () => {
            statusDot.classList.remove('connected');
            statusText.textContent = 'UniCast çalışmıyor';
        };
    } catch (e) {
        statusDot.classList.remove('connected');
        statusText.textContent = 'UniCast bağlantısı yok';
    }
});
