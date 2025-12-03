using System;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UniCast.Core.Chat.Ingestors
{
    /// <summary>
    /// Twitch IRC Chat Ingestor.
    /// Twitch chat'e IRC protokolü üzerinden bağlanır.
    /// OAuth token OPSIYONEL - anonim okuma desteklenir.
    /// 
    /// DÜZELTME v17.3: OnAuthenticationFailed event eklendi
    /// </summary>
    public sealed class TwitchChatIngestor : BaseChatIngestor
    {
        private const string TwitchIrcServer = "irc.chat.twitch.tv";
        private const int TwitchIrcPort = 6667;
        private const int TwitchIrcSslPort = 6697;

        private TcpClient? _tcpClient;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private bool _isAuthenticated;

        // IRC mesaj pattern'i
        // Format: @tags :user!user@user.tmi.twitch.tv PRIVMSG #channel :message
        private static readonly Regex MessageRegex = new(
            @"^(?:@(?<tags>[^\s]+)\s)?:(?<user>[^!]+)![^\s]+\s+PRIVMSG\s+#(?<channel>[^\s]+)\s+:(?<message>.+)$",
            RegexOptions.Compiled);

        // Badge pattern'i
        private static readonly Regex BadgeRegex = new(
            @"badges=([^;]*)",
            RegexOptions.Compiled);

        public override ChatPlatform Platform => ChatPlatform.Twitch;

        /// <summary>
        /// DÜZELTME v17.3: OAuth token geçersiz veya süresi dolduğunda tetiklenir.
        /// UI'da kullanıcıya bildirim göstermek için kullanılabilir.
        /// </summary>
        public event EventHandler<AuthenticationFailedEventArgs>? OnAuthenticationFailed;

        /// <summary>
        /// Twitch OAuth Token (opsiyonel).
        /// Anonim okuma için boş bırakılabilir.
        /// Format: oauth:xxxxxxxxxxxxxx
        /// </summary>
        public string? OAuthToken { get; set; }

        /// <summary>
        /// Bot kullanıcı adı.
        /// Anonim okuma için "justinfan" + rastgele sayı kullanılır.
        /// </summary>
        public string? BotUsername { get; set; }

        /// <summary>
        /// SSL kullanılsın mı?
        /// </summary>
        public bool UseSsl { get; set; } = false;

        /// <summary>
        /// Yeni Twitch chat ingestor oluşturur.
        /// </summary>
        /// <param name="channelName">Twitch kanal adı (# olmadan)</param>
        public TwitchChatIngestor(string channelName) : base(channelName.ToLowerInvariant().TrimStart('#'))
        {
        }

        protected override async Task ConnectAsync(CancellationToken ct)
        {
            Log.Information("[Twitch] #{Channel} kanalına bağlanılıyor...", _identifier);

            try
            {
                // TCP bağlantısı
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(TwitchIrcServer, UseSsl ? TwitchIrcSslPort : TwitchIrcPort, ct);

                var stream = _tcpClient.GetStream();
                _reader = new StreamReader(stream);
                _writer = new StreamWriter(stream) { AutoFlush = true };

                // Kimlik doğrulama
                if (!string.IsNullOrEmpty(OAuthToken) && !string.IsNullOrEmpty(BotUsername))
                {
                    // OAuth ile giriş
                    await _writer.WriteLineAsync($"PASS {OAuthToken}");
                    await _writer.WriteLineAsync($"NICK {BotUsername.ToLowerInvariant()}");
                    _isAuthenticated = true;
                    Log.Debug("[Twitch] OAuth ile giriş yapılıyor: {Username}", BotUsername);
                }
                else
                {
                    // Anonim giriş (sadece okuma)
                    var anonUser = $"justinfan{Random.Shared.Next(10000, 99999)}";
                    await _writer.WriteLineAsync("PASS SCHMOOPIIE");
                    await _writer.WriteLineAsync($"NICK {anonUser}");
                    _isAuthenticated = false;
                    Log.Information("[Twitch] Anonim giriş yapılıyor: {Username}", anonUser);
                }

                // Capability request - zengin mesaj bilgileri için
                await _writer.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands");

                // Kanala katıl
                await _writer.WriteLineAsync($"JOIN #{_identifier}");

                Log.Information("[Twitch] #{Channel} kanalına bağlandı", _identifier);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Twitch] Bağlantı hatası: {Message}", ex.Message);
                throw;
            }
        }

        protected override async Task DisconnectAsync()
        {
            try
            {
                if (_writer != null)
                {
                    await _writer.WriteLineAsync($"PART #{_identifier}");
                    await _writer.WriteLineAsync("QUIT");
                }
            }
            catch (Exception ex)
            {
                // DÜZELTME v26: Boş catch'e loglama eklendi
                System.Diagnostics.Debug.WriteLine($"[TwitchChatIngestor.DisconnectAsync] Bağlantı kesme hatası: {ex.Message}");
            }

            _reader?.Dispose();
            _writer?.Dispose();
            _tcpClient?.Dispose();

            _reader = null;
            _writer = null;
            _tcpClient = null;

            Log.Debug("[Twitch] Bağlantı kapatıldı");
        }

        protected override async Task RunMessageLoopAsync(CancellationToken ct)
        {
            if (_reader == null || _writer == null)
            {
                Log.Warning("[Twitch] Reader/Writer null, bağlantı kopmuş olabilir");
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var line = await _reader.ReadLineAsync(ct);

                    if (string.IsNullOrEmpty(line))
                    {
                        await Task.Delay(100, ct);
                        continue;
                    }

                    // PING/PONG - bağlantıyı canlı tut
                    if (line.StartsWith("PING"))
                    {
                        await _writer.WriteLineAsync(line.Replace("PING", "PONG"));
                        continue;
                    }

                    // DÜZELTME v17.3: NOTICE - Authentication hataları ve token expiry
                    if (line.Contains("NOTICE") &&
                        (line.Contains("Login authentication failed") ||
                         line.Contains("Login unsuccessful") ||
                         line.Contains("Invalid NICK")))
                    {
                        Log.Error("[Twitch] OAuth token geçersiz veya süresi dolmuş! Ayarlardan yeni token alın.");

                        OnAuthenticationFailed?.Invoke(this, new AuthenticationFailedEventArgs
                        {
                            Reason = "OAuth token geçersiz veya süresi dolmuş",
                            RequiresReauth = true,
                            Platform = "Twitch"
                        });

                        break; // Bağlantıyı kapat
                    }

                    // PRIVMSG - chat mesajı
                    var match = MessageRegex.Match(line);
                    if (match.Success)
                    {
                        var message = ParseMessage(match);
                        if (message != null)
                        {
                            PublishMessage(message);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException ex)
                {
                    Log.Warning(ex, "[Twitch] Okuma hatası, yeniden bağlanılacak");

                    try
                    {
                        await ReconnectAsync(ct);
                    }
                    catch (Exception reconnectEx)
                    {
                        // DÜZELTME v27: Reconnect exception logging eklendi
                        Log.Error(reconnectEx, "[Twitch] Reconnect başarısız, ingestor durduruluyor");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Twitch] Beklenmeyen hata");
                    await Task.Delay(1000, ct);
                }
            }
        }

        private ChatMessage? ParseMessage(Match match)
        {
            try
            {
                var tags = match.Groups["tags"].Value;
                var user = match.Groups["user"].Value;
                var messageText = match.Groups["message"].Value.Trim();

                // Display name
                var displayName = ExtractTag(tags, "display-name");
                if (string.IsNullOrEmpty(displayName))
                    displayName = user;

                // Badges
                var badges = ExtractTag(tags, "badges") ?? "";
                var isMod = badges.Contains("moderator") || badges.Contains("broadcaster");
                var isSub = badges.Contains("subscriber") || badges.Contains("founder");
                var isVip = badges.Contains("vip");
                var isBroadcaster = badges.Contains("broadcaster");

                // Emotes
                var emotes = ExtractTag(tags, "emotes");

                // Message type
                var msgType = ChatMessageType.Normal;
                var bits = ExtractTag(tags, "bits");
                if (!string.IsNullOrEmpty(bits))
                {
                    msgType = ChatMessageType.Superchat;
                }

                var message = new ChatMessage
                {
                    Platform = ChatPlatform.Twitch,
                    Username = user,
                    DisplayName = displayName,
                    Message = messageText,
                    IsModerator = isMod,
                    IsSubscriber = isSub,
                    IsOwner = isBroadcaster,
                    IsVerified = isVip,
                    Type = msgType,
                    DonationAmount = bits,
                    DonationCurrency = !string.IsNullOrEmpty(bits) ? "bits" : null,
                    Timestamp = DateTime.UtcNow
                };

                // Metadata
                if (!string.IsNullOrEmpty(emotes))
                    message.Metadata["emotes"] = emotes;

                var color = ExtractTag(tags, "color");
                if (!string.IsNullOrEmpty(color))
                    message.Metadata["color"] = color;

                var msgId = ExtractTag(tags, "id");
                if (!string.IsNullOrEmpty(msgId))
                    message.Metadata["msg-id"] = msgId;

                return message;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Twitch] Mesaj parse hatası");
                return null;
            }
        }

        private static string? ExtractTag(string tags, string tagName)
        {
            if (string.IsNullOrEmpty(tags))
                return null;

            var pattern = $@"{tagName}=([^;]*)";
            var match = Regex.Match(tags, pattern);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Chat'e mesaj gönderir (OAuth gerektirir).
        /// </summary>
        public async Task SendMessageAsync(string message)
        {
            if (!_isAuthenticated)
            {
                Log.Warning("[Twitch] Mesaj göndermek için OAuth ile giriş yapmalısınız");
                return;
            }

            if (_writer == null)
            {
                Log.Warning("[Twitch] Bağlantı yok, mesaj gönderilemedi");
                return;
            }

            await _writer.WriteLineAsync($"PRIVMSG #{_identifier} :{message}");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader?.Dispose();
                _writer?.Dispose();
                _tcpClient?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// DÜZELTME v17.3: Authentication hatası event argümanları.
    /// OAuth token expire olduğunda veya geçersiz olduğunda kullanılır.
    /// </summary>
    public sealed class AuthenticationFailedEventArgs : EventArgs
    {
        /// <summary>
        /// Hata nedeni (kullanıcıya gösterilebilir).
        /// </summary>
        public string Reason { get; init; } = "";

        /// <summary>
        /// Kullanıcının yeniden kimlik doğrulaması yapması gerekiyor mu?
        /// </summary>
        public bool RequiresReauth { get; init; }

        /// <summary>
        /// Hangi platformda hata oluştu (Twitch, YouTube, vb.)
        /// </summary>
        public string Platform { get; init; } = "";

        /// <summary>
        /// Hata oluştuğu zaman.
        /// </summary>
        public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    }
}