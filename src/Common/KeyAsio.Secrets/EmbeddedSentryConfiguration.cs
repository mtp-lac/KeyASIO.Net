using System.Globalization;

public class EmbeddedSentryConfiguration : IDisposable
{
    private readonly IDisposable _sentrySdk;

    public EmbeddedSentryConfiguration(Action<SentryOptions>? configureOptions = null)
    {
        _sentrySdk = SentrySdk.Init(options =>
        {
            Configure(options);
            configureOptions?.Invoke(options);
        });
    }

    public static void Configure(SentryOptions options)
    {
        options.Dsn = IsValidDsn(Secrets.Dsn) ? Secrets.Dsn : string.Empty;
#if !RELEASE
        options.Debug = true;
        options.Environment = "debug";
#else
        options.Debug = false;
        options.Environment = "production";
#endif
        options.TracesSampleRate = 1;
        options.SendDefaultPii = true;
        options.AttachStacktrace = true;
    }

    private static bool IsValidDsn(string? dsn)
    {
        if (!Uri.TryCreate(dsn, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return false;
        }

        var projectId = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return int.TryParse(projectId, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    public void Dispose()
    {
        _sentrySdk.Dispose();
    }
}
