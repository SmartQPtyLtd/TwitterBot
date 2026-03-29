using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TwitterBot;

/// <summary>
/// One-time OAuth 2.0 PKCE authorization flow.
/// Run with: dotnet run -- --setup-oauth
/// </summary>
public static class OAuth2Setup
{
    public static async Task RunAsync(string clientId, string clientSecret)
    {
        const string redirectUri = "http://localhost:3000/callback";
        const string scopes = "tweet.read tweet.write users.read offline.access";

        // Generate PKCE verifier and challenge
        var verifier = GenerateCodeVerifier();
        var challenge = ComputeCodeChallenge(verifier);
        var state = Guid.NewGuid().ToString();

        // Build authorization URL
        var authUrl = $"https://twitter.com/i/oauth2/authorize" +
            $"?response_type=code" +
            $"&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scopes)}" +
            $"&state={state}" +
            $"&code_challenge={challenge}" +
            $"&code_challenge_method=S256";

        // Start local HTTP listener
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:3000/");
        listener.Start();

        // Open browser
        Console.WriteLine("Opening browser for authorization...");
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        Console.WriteLine("Waiting for callback (authorize in the browser)...");

        // Wait for callback
        var context = await listener.GetContextAsync();
        var query = context.Request.Url!.Query;

        // Send success page
        var html = "<html><body><h2>Authorization complete! You can close this tab.</h2></body></html>"u8;
        context.Response.ContentType = "text/html";
        await context.Response.OutputStream.WriteAsync(html.ToArray());
        context.Response.Close();
        listener.Stop();

        // Parse code from callback
        var queryParams = System.Web.HttpUtility.ParseQueryString(query);
        var callbackState = queryParams["state"];
        var code = queryParams["code"];

        if (callbackState != state)
        {
            Console.Error.WriteLine($"State mismatch! Expected {state}, got {callbackState}");
            return;
        }

        if (string.IsNullOrEmpty(code))
        {
            Console.Error.WriteLine("No authorization code received.");
            return;
        }

        Console.WriteLine("Got authorization code, exchanging for tokens...");

        // Exchange code for tokens
        using var http = new HttpClient();
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.twitter.com/2/oauth2/token");
        tokenRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        tokenRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier
        });

        var response = await http.SendAsync(tokenRequest);
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Token exchange failed: {responseJson}");
            return;
        }

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var refreshToken = root.GetProperty("refresh_token").GetString();
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        var scope = root.GetProperty("scope").GetString();

        Console.WriteLine();
        Console.WriteLine("=== SUCCESS ===");
        Console.WriteLine($"Scope: {scope}");
        Console.WriteLine($"Access token expires in: {expiresIn}s");
        Console.WriteLine();
        Console.WriteLine("Run these commands to save the credentials:");
        Console.WriteLine();
        Console.WriteLine($"  dotnet user-secrets set \"TwitterBot:OAuth2ClientId\" \"{clientId}\"");
        Console.WriteLine($"  dotnet user-secrets set \"TwitterBot:OAuth2ClientSecret\" \"{clientSecret}\"");
        Console.WriteLine($"  dotnet user-secrets set \"TwitterBot:OAuth2RefreshToken\" \"{refreshToken}\"");
        Console.WriteLine();
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string ComputeCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
