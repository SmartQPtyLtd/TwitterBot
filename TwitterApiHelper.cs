using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TwitterBot;

/// <summary>
/// Posts tweets using OAuth 2.0 Authorization Code with PKCE (user-context).
/// Manages access token lifecycle via refresh tokens automatically.
/// </summary>
public class TwitterApiHelper(HttpClient http, IOptions<TwitterBotOptions> opts, RefreshTokenStore tokenStore, ILogger<TwitterApiHelper> logger)
{
    private readonly TwitterBotOptions _opts = opts.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    public async Task<string?> ReplyAsync(string inReplyToId, string text, CancellationToken token)
    {
        var accessToken = await GetAccessTokenAsync(token);

        const string url = "https://api.twitter.com/2/tweets";
        var body = JsonSerializer.Serialize(new
        {
            text,
            reply = new { in_reply_to_tweet_id = inReplyToId }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await http.SendAsync(request, token);
        var responseJson = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Twitter API {(int)response.StatusCode}: {responseJson}",
                inner: null,
                statusCode: response.StatusCode);
        }

        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.GetProperty("data").GetProperty("id").GetString();
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken token)
    {
        // Return cached token if still valid (with 60s buffer)
        if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddSeconds(-60))
            return _accessToken;

        await _tokenLock.WaitAsync(token);
        try
        {
            // Double-check after acquiring lock
            if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddSeconds(-60))
                return _accessToken;

            var clientId = _opts.OAuth2ClientId?.Trim() ?? "";
            var clientSecret = _opts.OAuth2ClientSecret?.Trim() ?? "";
            var refreshToken = await tokenStore.GetTokenAsync(token);

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(refreshToken))
            {
                throw new InvalidOperationException(
                    "OAuth 2.0 credentials (OAuth2ClientId, OAuth2ClientSecret, OAuth2RefreshToken) are not configured. " +
                    "Run the authorization setup first, then set them via user-secrets.");
            }

            logger.LogInformation("Refreshing OAuth 2.0 access token...");

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.twitter.com/2/oauth2/token");

            // Basic auth: Base64(client_id:client_secret)
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            });

            var response = await http.SendAsync(request, token);
            var responseJson = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Failed to refresh OAuth 2.0 token: {(int)response.StatusCode} {responseJson}. " +
                    "You may need to re-authorize. Run the setup again.");
            }

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            _accessToken = root.GetProperty("access_token").GetString()!;
            var expiresIn = root.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            // Twitter rotates refresh tokens — persist the new one automatically
            if (root.TryGetProperty("refresh_token", out var newRefreshToken))
            {
                var newToken = newRefreshToken.GetString();
                if (!string.IsNullOrEmpty(newToken) && newToken != refreshToken)
                {
                    await tokenStore.SaveTokenAsync(newToken, token);
                }
            }

            logger.LogInformation("OAuth 2.0 access token refreshed. Expires in {seconds}s.", expiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
