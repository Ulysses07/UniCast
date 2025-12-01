using System;

namespace UniCast.Core.Chat
{
    /// <summary>
    /// Chat kaynağı (eski uyumluluk için)
    /// </summary>
    public enum ChatSource
    {
        YouTube = 1,
        TikTok = 3,
        Instagram = 4,
        Facebook = 5
    }

    /// <summary>
    /// Chat sabitleri
    /// </summary>
    public static class ChatConstants
    {
        public const int MaxUiMessages = 100;
        public const int MaxBufferSize = 500;
        public const int BatchIntervalMs = 250;
        public const int MaxOverlayMessages = 50;
    }
}