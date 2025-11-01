using System;

namespace UniCast.Core.Chat
{
    public enum ChatSource { YouTube, TikTok, Instagram, Facebook }

    public sealed record ChatMessage(
        string Id,
        ChatSource Source,
        DateTimeOffset Timestamp,
        string Author,
        string Text,
        string? AvatarUrl = null
    );
}
