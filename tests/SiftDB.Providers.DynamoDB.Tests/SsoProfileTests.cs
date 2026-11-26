using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.DynamoDB.Tests;

/// <summary>
/// Tests that point the AWS SDK at profile files of their own (AWS_CONFIG_FILE), so they never run at the same time.
/// </summary>
[CollectionDefinition("AWS profile files", DisableParallelization = true)]
public class AwsProfileFiles;

/// <summary>
/// An SSO profile through the provider loaded as a plugin (F18): the SDK loads AWSSDK.SSO and AWSSDK.SSOOIDC by name
/// when it meets one, and they must resolve from providers\dynamodb\. Without a sign-in (`aws sso login`) the
/// connection is refused, but for that reason, not because an assembly could not be found. No AWS account needed.
/// </summary>
[Collection("AWS profile files")]
public sealed class SsoProfileTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("siftdb-aws-");
    private readonly string? _config = Environment.GetEnvironmentVariable("AWS_CONFIG_FILE");
    private readonly string? _credentials = Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AWS_CONFIG_FILE", _config);
        Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE", _credentials);
        try { _dir.Delete(true); } catch (IOException) { }
    }

    [Fact]
    public async Task An_sso_profile_without_a_sign_in_is_refused_for_that_reason()
    {
        var config = Path.Combine(_dir.FullName, "config");
        File.WriteAllText(config, """
            [profile siftdb-sso]
            sso_session = siftdb
            sso_account_id = 111122223333
            sso_role_name = ReadOnly
            region = eu-west-2

            [sso-session siftdb]
            sso_start_url = https://siftdb-test.awsapps.com/start
            sso_region = eu-west-2
            """);
        Environment.SetEnvironmentVariable("AWS_CONFIG_FILE", config);
        Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE", Path.Combine(_dir.FullName, "credentials"));

        var p = Assert.Single(ProviderLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "providers")).Providers);
        // Nothing listens on this port: a sign-in that somehow worked would fail there, never at AWS.
        var cs = p.BuildConnectionString(new Dictionary<string, string>
            { ["auth"] = "profile", ["profile"] = "siftdb-sso", ["region"] = "eu-west-2", ["endpoint"] = "http://127.0.0.1:9" }, null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => p.OpenAsync(cs, ConnectionOptions.Default, timeout.Token));
        var all = string.Join(" | ", Chain(ex).Select(e => $"{e.GetType().Name}: {e.Message}"));
        Assert.DoesNotContain("Could not load file or assembly", all);
        Assert.DoesNotContain("AWSSDK.SSO", all.Replace("AWSSDK.SSO.", "")); // never "missing AWSSDK.SSO"
        // And it says what to do, naming the profile.
        Assert.Contains("aws sso login --profile siftdb-sso", ex.Message);
    }

    private static IEnumerable<Exception> Chain(Exception? e)
    {
        for (; e is not null; e = e.InnerException) yield return e;
    }
}
