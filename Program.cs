using Microsoft.Extensions.Options;
using TwitterBot;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<TwitterBotOptions>(ctx.Configuration.GetSection("TwitterBot"));
        services.AddSingleton<RefreshTokenStore>();
        services.AddHttpClient<TwitterApiHelper>();
        services.AddSingleton<TwitterBotService>();
        services.AddHostedService(sp => sp.GetRequiredService<TwitterBotService>());
    })
    .Build();

// One-time OAuth 2.0 setup: dotnet run -- --setup-oauth
if (args.Contains("--setup-oauth"))
{
    var opts = host.Services.GetRequiredService<IOptions<TwitterBotOptions>>().Value;

    if (string.IsNullOrWhiteSpace(opts.OAuth2ClientId) || string.IsNullOrWhiteSpace(opts.OAuth2ClientSecret))
        throw new InvalidOperationException(
            "OAuth2ClientId and OAuth2ClientSecret must be set in user-secrets before running --setup-oauth.\n" +
            "  dotnet user-secrets set \"TwitterBot:OAuth2ClientId\" \"<your-client-id>\"\n" +
            "  dotnet user-secrets set \"TwitterBot:OAuth2ClientSecret\" \"<your-client-secret>\"");

    await OAuth2Setup.RunAsync(opts.OAuth2ClientId, opts.OAuth2ClientSecret);
}
else await host.RunAsync();