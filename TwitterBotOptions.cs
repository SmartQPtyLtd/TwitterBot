namespace TwitterBot;

public class TwitterBotOptions
{
    // App-only auth (Bearer Token) for TwitterSharp reads/streams
    public string BearerToken { get; set; } = null!;

    // OAuth 2.0 PKCE credentials for user-context writes (posting tweets)
    public string OAuth2ClientId { get; set; } = null!;
    public string OAuth2ClientSecret { get; set; } = null!;
    public string OAuth2RefreshToken { get; set; } = null!;

    public string BotHandle { get; set; } = null!;
    public string[] WatchUsernames { get; set; } = [];
    public int MaxBackoffSeconds { get; set; } = 300;
    public int BaseBackoffSeconds { get; set; } = 5;
    public int ReplyMinJitterMs { get; set; } = 500;
    public int ReplyMaxJitterMs { get; set; } = 3000;
}
