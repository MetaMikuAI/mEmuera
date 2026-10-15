namespace mEmuera.Contracts;

public sealed record SaveCompatibility(string Profile, string Fingerprint = "", long GameVersion = 0,
    long MinimumVersion = -1, bool VersionDefined = false)
{
    public bool AcceptsVersion(long version)
        => !VersionDefined && version != 1000 || MinimumVersion <= version || GameVersion == version;

    public bool CanRead(SaveSnapshot snapshot)
    {
        if (snapshot.SchemaVersion is not (1 or 2) || snapshot.FlowVersion != 1) return false;
        bool sameProfile = snapshot.CompatibilityProfile == Profile;
        if (!sameProfile && (snapshot.SchemaVersion != 2 || string.IsNullOrEmpty(Fingerprint)
            || snapshot.CompatibilityFingerprint != Fingerprint)) return false;
        // 旧档可能缺少独立版本字段；完整 profile 仍证明原有版本与配置相同。
        if (sameProfile && string.IsNullOrEmpty(snapshot.CompatibilityFingerprint)) return true;
        if (sameProfile && snapshot.CompatibilityFingerprint != Fingerprint) return false;
        return AcceptsVersion(snapshot.GameVersion);
    }
}
