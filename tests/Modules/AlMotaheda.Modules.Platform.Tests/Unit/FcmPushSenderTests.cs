using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlMotaheda.Modules.Platform.Push;

namespace AlMotaheda.Modules.Platform.Tests.Unit;

/// <summary>The Firebase message and the Google sign-in token, without calling Google.</summary>
public sealed class FcmPushSenderTests
{
    private static readonly PushMessage Returned = new(42, "RecordReturned", "تمت إعادة المصروف أو التوريد للتعديل",
        "Your expense or supply was returned for changes", "ProjectExpense", "123", 7);

    [Fact]
    public void The_message_shows_the_stored_text_and_carries_only_what_opens_the_record()
    {
        var json = JsonSerializer.SerializeToElement(FcmPushSender.Body("device-token-1", Returned)).GetProperty("message");

        Assert.Equal("device-token-1", json.GetProperty("token").GetString());
        Assert.Equal(Returned.TitleAr, json.GetProperty("notification").GetProperty("title").GetString());
        Assert.Equal(Returned.TitleEn, json.GetProperty("notification").GetProperty("body").GetString());
        var data = json.GetProperty("data");
        Assert.Equal(["notificationId", "type", "entityType", "entityKey", "projectId"], data.EnumerateObject().Select(field => field.Name));
        Assert.Equal("42", data.GetProperty("notificationId").GetString());
        Assert.Equal("7", data.GetProperty("projectId").GetString());
        // FCM data values must be strings; a company-wide notification has no project.
        var noProject = JsonSerializer.SerializeToElement(FcmPushSender.Body("t", Returned with { ProjectId = null }));
        Assert.Equal("", noProject.GetProperty("message").GetProperty("data").GetProperty("projectId").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{\"error\":{\"status\":\"NOT_FOUND\"}}", true)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"The registration token is not a valid FCM registration token\"}}", true)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"details\":[{\"errorCode\":\"UNREGISTERED\"}]}}", true)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Invalid JSON payload\"}}", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", false)]
    [InlineData(HttpStatusCode.Unauthorized, "", false)]
    public void Only_answers_about_the_token_itself_disable_a_device(HttpStatusCode status, string body, bool dead) =>
        Assert.Equal(dead, FcmPushSender.IsDeadToken(status, body));

    [Fact]
    public void The_google_sign_in_token_is_an_RS256_JWT_for_the_messaging_scope_valid_one_hour()
    {
        using var rsa = RSA.Create(2048);
        var account = new FcmPushSender.ServiceAccount("my-project", "pusher@my-project.iam.gserviceaccount.com", rsa.ExportPkcs8PrivateKeyPem());
        var now = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

        var jwt = FcmPushSender.Assertion(account, now);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        static byte[] Decode(string part) => Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/').PadRight((part.Length + 3) / 4 * 4, '='));
        var header = JsonDocument.Parse(Decode(parts[0])).RootElement;
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        var claims = JsonDocument.Parse(Decode(parts[1])).RootElement;
        Assert.Equal(account.ClientEmail, claims.GetProperty("iss").GetString());
        Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", claims.GetProperty("scope").GetString());
        Assert.Equal("https://oauth2.googleapis.com/token", claims.GetProperty("aud").GetString());
        Assert.Equal(3600, claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}
