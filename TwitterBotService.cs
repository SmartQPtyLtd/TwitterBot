using Microsoft.Extensions.Options;
using TwitterSharp.Client;
using TwitterSharp.Request;
using TwitterSharp.Request.AdvancedSearch;
using TwitterSharp.Request.Option;
using TwitterSharp.Response.RTweet;
using TwitterSharp.Rule;

namespace TwitterBot;

public class TwitterBotService : BackgroundService
{
    private readonly TwitterApiHelper _apiHelper;
    private readonly TwitterClient _client;
    private readonly ILogger<TwitterBotService> _logger;
    private readonly TwitterBotOptions _opts;
    private readonly SemaphoreSlim _replySemaphore = new(1, 1);
    private readonly Random _rng = new();
    private HashSet<string> _watchedUserIds = new(StringComparer.OrdinalIgnoreCase);

    private const string MentionTag = "mention";
    private const string WatchTag = "watch";
    private const int MaxAttempts = 10;

    public TwitterBotService(IOptions<TwitterBotOptions> opts, ILogger<TwitterBotService> logger, TwitterApiHelper apiHelper)
    {
        _logger = logger;
        _opts = opts.Value;
        _apiHelper = apiHelper;

        if (string.IsNullOrWhiteSpace(_opts.BearerToken))
            throw new InvalidOperationException("TwitterBot:BearerToken is not configured. Set it via user-secrets or appsettings.");

        _client = new TwitterClient(_opts.BearerToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Twitter bot starting.");

        // Build all stream rules up front
        var rules = new List<StreamRequest>();

        // Mention rule
        var mentionExpression = Expression.Mention(_opts.BotHandle);
        rules.Add(new StreamRequest(mentionExpression, MentionTag));

        // Watcher rules — resolve usernames to author expressions
        if (_opts.WatchUsernames is { Length: > 0 })
        {
            var authorExpressions = new List<Expression>();
            foreach (var uname in _opts.WatchUsernames)
            {
                try
                {
                    var user = await _client.GetUserAsync(uname, null);
                    authorExpressions.Add(Expression.Author(user.Username));
                    _watchedUserIds.Add(user.Id);
                    _logger.LogInformation("Watching user @{user} (id: {id})", user.Username, user.Id);
                }
                catch (TwitterException tex) when (tex.Message.Contains("credits") || tex.Message.Contains("Unauthorized"))
                {
                    LogTwitterException(tex, uname);
                    _logger.LogCritical("Non-recoverable error resolving @{uname}. Check your API access tier.", uname);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to resolve username @{uname}, skipping.", uname);
                }
            }

            if (authorExpressions.Count > 0)
            {
                // Combine with OR: from:user1 OR from:user2 ...
                var watchExpression = authorExpressions[0];
                if (authorExpressions.Count > 1)
                    watchExpression = watchExpression.Or([.. authorExpressions.Skip(1)]);

                rules.Add(new StreamRequest(watchExpression, WatchTag));
            }
        }

        if (rules.Count == 0)
        {
            _logger.LogWarning("No stream rules to register. Exiting.");
            return;
        }

        await RunStreamLoop(rules, stoppingToken);
    }

    /// <summary>
    /// Single stream loop that manages all rules, reconnection, and exponential backoff.
    /// TwitterSharp only supports one active stream per client.
    /// </summary>
    private async Task RunStreamLoop(List<StreamRequest> rules, CancellationToken stoppingToken)
    {
        int attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Cancel any lingering stream from a previous iteration
                _client.CancelTweetStream(false);

                // Remove all existing rules
                var existingRules = await _client.GetInfoTweetStreamAsync();
                if (existingRules is { Length: > 0 })
                {
                    var idsToDelete = existingRules.Select(r => r.Id).ToArray();
                    _logger.LogInformation("Removing {count} stale stream rule(s).", idsToDelete.Length);
                    await _client.DeleteTweetStreamAsync(idsToDelete);
                }

                // Register all rules
                _logger.LogInformation("Adding {count} stream rule(s).", rules.Count);
                await _client.AddTweetStreamAsync([.. rules]);

                _logger.LogInformation("Stream connected. Listening for tweets...");

                // Start streaming — blocks until the stream ends or throws
                await _client.NextTweetStreamAsync(tweet =>
                {
                    _ = Task.Run(async () => await DispatchTweetAsync(tweet, stoppingToken), stoppingToken);
                }, new TweetSearchOptions
                {
                    TweetOptions = [TweetOption.Referenced_Tweets, TweetOption.Entities]
                });

                // Stream ended normally — reset attempts
                attempt = 0;
                _logger.LogWarning("Stream ended normally. Reconnecting...");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Cancellation requested, exiting stream loop.");
                break;
            }
            catch (TwitterException tex) when (tex.Message.Contains("credits") || tex.Message.Contains("Unauthorized"))
            {
                LogTwitterException(tex, "stream");
                _logger.LogCritical("Non-recoverable Twitter API error. Check your API access tier and credentials.");
                break;
            }
            catch (TwitterException tex)
            {
                attempt++;
                LogTwitterException(tex, "stream");
                _logger.LogError("Stream error (attempt {attempt}/{max}).", attempt, MaxAttempts);

                if (attempt >= MaxAttempts)
                {
                    _logger.LogCritical("Max retry attempts ({max}) reached. Giving up.", MaxAttempts);
                    break;
                }
            }
            catch (Exception ex)
            {
                attempt++;
                _logger.LogError(ex, "Stream error (attempt {attempt}/{max}).", attempt, MaxAttempts);

                if (attempt >= MaxAttempts)
                {
                    _logger.LogCritical("Max retry attempts ({max}) reached. Giving up.", MaxAttempts);
                    break;
                }
            }

            var backoffSeconds = ComputeBackoffSeconds(attempt, _opts.BaseBackoffSeconds, _opts.MaxBackoffSeconds);
            var jitterMs = _rng.Next(200, 2000);
            var delay = TimeSpan.FromSeconds(backoffSeconds).Add(TimeSpan.FromMilliseconds(jitterMs));
            _logger.LogInformation("Waiting {delay} before reconnecting...", delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _client.CancelTweetStream(true);

        // Clean up stream rules on Twitter's servers so they don't accumulate across restarts
        try
        {
            var remainingRules = await _client.GetInfoTweetStreamAsync();
            if (remainingRules is { Length: > 0 })
            {
                var idsToDelete = remainingRules.Select(r => r.Id).ToArray();
                _logger.LogInformation("Cleaning up {count} stream rule(s) before exit.", idsToDelete.Length);
                await _client.DeleteTweetStreamAsync(idsToDelete);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up stream rules on exit.");
        }

        _logger.LogInformation("Stream loop exited.");
    }

    /// <summary>
    /// Routes an incoming tweet to the correct handler based on its matching rule tag.
    /// Falls back to content-based routing when MatchingRules is not populated.
    /// </summary>
    private async Task DispatchTweetAsync(Tweet tweet, CancellationToken token)
    {
        try
        {
            var tags = tweet.MatchingRules?.Select(r => r.Tag)
                          .Where(t => t != null)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase)
                       ?? [];

            if (tags.Contains(MentionTag))
            {
                await HandleMentionAsync(tweet, token);
            }
            else if (tags.Contains(WatchTag))
            {
                await HandleWatchedTweetAsync(tweet, token);
            }
            else
            {
                // Fallback: MatchingRules not populated — route by content
                var isMention = tweet.Text?.Contains($"@{_opts.BotHandle}", StringComparison.OrdinalIgnoreCase) == true;
                var isWatched = tweet.AuthorId != null
                                && _watchedUserIds.Contains(tweet.AuthorId);

                if (isMention)
                    await HandleMentionAsync(tweet, token);
                else if (isWatched)
                    await HandleWatchedTweetAsync(tweet, token);
                else
                    _logger.LogDebug("Tweet {id} could not be routed. AuthorId: {authorId}",
                        tweet.Id, tweet.AuthorId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching tweet {id}", tweet.Id);
        }
    }

    private static int ComputeBackoffSeconds(int attempt, int baseSeconds, int maxSeconds)
    {
        if (attempt <= 0) return baseSeconds;
        // exponential: base * 2^(attempt-1)
        var pow = Math.Pow(2, attempt - 1);
        var seconds = (int)Math.Min(maxSeconds, Math.Max(baseSeconds, Math.Round(baseSeconds * pow)));
        return seconds;
    }

    private void LogTwitterException(TwitterException tex, string context)
    {
        _logger.LogError(tex, "TwitterException for {context}: {message}", context, tex.Message);
        if (tex.Errors is { Length: > 0 })
        {
            foreach (var err in tex.Errors)
                _logger.LogError("  Twitter error — Title: {title}, Message: {message}, Code: {code}, Details: {details}",
                    err.Title, err.Message, err.Code, err.Details != null ? string.Join("; ", err.Details) : "(none)");
        }
    }

    // Called when the bot is mentioned
    private async Task HandleMentionAsync(Tweet tweet, CancellationToken token)
    {
        _logger.LogInformation("Mention received (tweet {tweetId}): {text}", tweet.Id, tweet.Text);
        await Task.Delay(_rng.Next(_opts.ReplyMinJitterMs, _opts.ReplyMaxJitterMs), token);
        await SafeReplyAsync(tweet.Id, "Thanks for the mention!", token);
    }

    // Called when a watched account tweets
    private async Task HandleWatchedTweetAsync(Tweet tweet, CancellationToken token)
    {
        _logger.LogInformation("Watched account tweeted (tweet {tweetId}): {text}", tweet.Id, tweet.Text);
        await Task.Delay(_rng.Next(_opts.ReplyMinJitterMs, _opts.ReplyMaxJitterMs), token);
        await SafeReplyAsync(tweet.Id, "Automated reply to your tweet!", token);
    }

    // Reply with simple throttling and backoff on HTTP errors
    private async Task SafeReplyAsync(string inReplyToId, string text, CancellationToken token)
    {
        // Ensure only one reply at a time to reduce burstiness
        await _replySemaphore.WaitAsync(token);
        try
        {
            int attempt = 0;
            while (!token.IsCancellationRequested)
            {
                attempt++;
                try
                {
                    var replyId = await _apiHelper.ReplyAsync(inReplyToId, text, token);

                    _logger.LogInformation("Posted reply {id} in response to {inReplyTo}", replyId, inReplyToId);
                    return;
                }
                catch (HttpRequestException hex) when (hex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    // 401 is not transient — don't retry
                    _logger.LogError(hex, "OAuth authentication failed (401). Check ConsumerKey/ConsumerSecret/AccessToken/AccessTokenSecret and app permissions.");
                    return;
                }
                catch (HttpRequestException hex)
                {
                    // Transient errors (429, 500, 503, etc.) — back off and retry
                    _logger.LogWarning(hex, "Twitter API error while replying (attempt {attempt})", attempt);

                    // Compute backoff and jitter
                    var backoffSeconds = ComputeBackoffSeconds(attempt, _opts.BaseBackoffSeconds, _opts.MaxBackoffSeconds);
                    var jitterMs = _rng.Next(200, 1500);
                    var delay = TimeSpan.FromSeconds(backoffSeconds).Add(TimeSpan.FromMilliseconds(jitterMs));

                    _logger.LogInformation("Backing off {delay} before retrying reply", delay);
                    await Task.Delay(delay, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    _logger.LogInformation("Reply cancelled.");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error while replying (attempt {attempt})", attempt);
                    // small delay before retrying unexpected errors
                    await Task.Delay(TimeSpan.FromSeconds(5 + _rng.Next(0, 1000) / 1000), token);
                }
            }
        }
        finally
        {
            _replySemaphore.Release();
        }
    }
}