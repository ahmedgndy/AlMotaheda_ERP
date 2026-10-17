namespace AlMotaheda.Modules.Platform.Data;

/// <summary>
/// One phone that receives push notifications for one user: the Firebase Cloud Messaging registration token the app
/// got on that phone. A token belongs to one user at a time: when someone else signs in on the same phone, it moves
/// to them, so a handed-over phone never shows the previous user's notifications.
/// </summary>
/// <remarks>
/// Push only announces what is already stored (<see cref="Notification"/>): the app opens the record through the API,
/// which checks access again. A token Firebase reports as no longer valid gets <see cref="DisabledAt"/> and is not
/// used again until the app registers it anew.
/// </remarks>
internal sealed class DeviceToken
{
    public long Id { get; set; }

    public int UserId { get; set; }

    /// <summary>A <see cref="DevicePlatforms"/> value.</summary>
    public string Platform { get; set; } = "";

    public string Token { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>The last time the app registered it (each sign-in and each token refresh).</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>Set when Firebase answered that the token is unregistered or invalid.</summary>
    public DateTime? DisabledAt { get; set; }
}

/// <summary>The platforms that can register for push. Only Android is configured (Firebase); Apple needs APNs keys.</summary>
internal static class DevicePlatforms
{
    public const string Android = "Android";

    public static readonly string[] All = [Android];
}
