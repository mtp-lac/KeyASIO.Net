using Sentry;

namespace KeyAsio.UnitTests;

public sealed class EmbeddedSentryConfigurationTests
{
    [Fact]
    public void InvalidEmbeddedDsn_DisablesSentryWithoutThrowing()
    {
        var options = new SentryOptions();

        EmbeddedSentryConfiguration.Configure(options);

        Assert.Equal(string.Empty, options.Dsn);
    }
}
