using Penghou.Luban.Language;

namespace Penghou.Hufu.Luban;

/// <summary>A closed, host-selected semantic authorization profile for compiled Luban documents.</summary>
public sealed class HufuLanguageAuthorityProfile
{
    private HufuLanguageAuthorityProfile(LanguageVersions versions, bool allowDiff)
    { Versions = versions; AllowDiff = allowDiff; }

    /// <summary>The existing known-root read, find and search profile.</summary>
    public static HufuLanguageAuthorityProfile ReadV1 { get; } = new(new LanguageVersions(), false);

    /// <summary>V2 known-root reads, find/search and fixed-input read-only diff. No merge or mutation.</summary>
    public static HufuLanguageAuthorityProfile ReadAndDiffV2 { get; } = new(new LanguageVersions(
        LanguageProfile.TextChangeLanguageVersion, LanguageProfile.TextChangeIrVersion,
        LanguageProfile.TextChangeCatalogueVersion, LanguageProfile.TextChangeProviderProfile), true);

    /// <summary>The exact language, IR, catalogue and provider versions required by this profile.</summary>
    public LanguageVersions Versions { get; }
    internal bool AllowDiff { get; }
}
