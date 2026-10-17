namespace AlMotaheda.Modules.Platform.Push;

/// <summary>
/// Push notifications through Firebase Cloud Messaging (<c>Push</c> section). Off unless a service-account key file
/// is configured: the key is a secret, so it lives outside the repository (user-secrets or an environment variable
/// holding the file's path), never in appsettings.
/// </summary>
internal sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary>Path of the Firebase service-account JSON (Project settings, Service accounts, Generate new private key).</summary>
    public string? FcmServiceAccountFile { get; set; }

    /// <summary>A switch to keep push off even when a key is present (for example on a test server).</summary>
    public bool Enabled { get; set; } = true;
}
