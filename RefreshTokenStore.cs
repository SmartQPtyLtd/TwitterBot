using Microsoft.Extensions.Options;

namespace TwitterBot;

/// <summary>
/// Persists the OAuth 2.0 refresh token to a local file so rotated tokens
/// survive app restarts without manual user-secrets updates.
/// Falls back to the value from configuration (user-secrets / appsettings) 
/// if no file exists yet.
/// </summary>
public class RefreshTokenStore(IOptions<TwitterBotOptions> opts, ILogger<RefreshTokenStore> logger)
{
    private static readonly string TokenFilePath = Path.Combine(
        AppContext.BaseDirectory, ".refresh_token");

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Returns the most recent refresh token — from the persisted file if available,
    /// otherwise from configuration.
    /// </summary>
    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (File.Exists(TokenFilePath))
            {
                var stored = (await File.ReadAllTextAsync(TokenFilePath, ct)).Trim();
                if (!string.IsNullOrEmpty(stored))
                    return stored;
            }

            return opts.Value.OAuth2RefreshToken?.Trim() ?? "";
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Persists a new refresh token to the local file.
    /// </summary>
    public async Task SaveTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(TokenFilePath, refreshToken, ct);
            logger.LogInformation("Refresh token persisted to {path}.", TokenFilePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist refresh token to {path}.", TokenFilePath);
        }
        finally
        {
            _lock.Release();
        }
    }
}