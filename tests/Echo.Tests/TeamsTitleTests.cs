using Echo.Teams;

namespace Echo.Tests;

/// <summary>
/// Guards the meeting-subject resolution (<see cref="TeamsMonitor.PickSubject"/>).
///
/// Regression (2026-07-16): in Teams "compact meeting view" the tracked window is named
/// "Affichage compact de reunion", which was saved verbatim as the meeting subject. The
/// fix folds that label (and locale variants) into the generic list so it is rejected, and
/// the real subject is read from another Teams window's title bar ("&lt;Subject&gt; | Microsoft
/// Teams"). These tests pin both halves against the shipped default GenericTitles.
/// </summary>
public class TeamsTitleTests
{
    private static IReadOnlyCollection<string> DefaultFoldedGenerics()
        => new TeamsSelectors().GenericTitles.Select(TeamsMonitor.Fold).ToHashSet(StringComparer.Ordinal);

    [Theory]
    [InlineData("Affichage compact de réunion")]                 // the exact live label (accented)
    [InlineData("Affichage compact de reunion")]                 // unaccented
    [InlineData("Affichage compact de réunion | Microsoft Teams")]
    [InlineData("Compact meeting view")]
    [InlineData("Microsoft Teams")]
    [InlineData("Réunion")]
    // The exact live join/account-decorated title (2026-07-16): the real subject is NOT in it,
    // so nothing must be extracted (no "Meeting join", no tenant, no email).
    [InlineData("Meeting join | Réunion Microsoft Teams | Contoso | jane.doe@contoso.com | Microsoft Teams")]
    [InlineData("Rejoindre la réunion | Microsoft Teams")]
    public void PickSubject_RejectsGenericAndCompactLabels(string title)
        => Assert.Null(TeamsMonitor.PickSubject(title, DefaultFoldedGenerics()));

    [Theory]
    // Account-decorated title WITH a real subject as the leading segment: keep the lead only.
    [InlineData("Revue budget Q3 | Réunion Microsoft Teams | Contoso | jane.doe@contoso.com | Microsoft Teams", "Revue budget Q3")]
    [InlineData("Atlas kickoff | Microsoft Teams | user@x.com", "Atlas kickoff")]
    public void PickSubject_DecoratedTitle_KeepsLeadingSubjectOnly(string title, string expected)
        => Assert.Equal(expected, TeamsMonitor.PickSubject(title, DefaultFoldedGenerics()));

    [Theory]
    [InlineData("Revue budget Q3 | Microsoft Teams", "Revue budget Q3")]
    [InlineData("Fabrikam Atlas Phase 2 - Daily | Microsoft Teams", "Fabrikam Atlas Phase 2 - Daily")]
    [InlineData("Weekly Sync", "Weekly Sync")]
    public void PickSubject_KeepsRealSubject(string title, string expected)
        => Assert.Equal(expected, TeamsMonitor.PickSubject(title, DefaultFoldedGenerics()));

    [Fact]
    public void PickSubject_PrefersRealSegmentOverGenericOne()
        // "<subject> | Microsoft Teams": the generic "Microsoft Teams" segment is skipped.
        => Assert.Equal("Point d'étape Atlas",
            TeamsMonitor.PickSubject("Point d'étape Atlas | Microsoft Teams", DefaultFoldedGenerics()));

    [Theory]
    // Outlook "Meeting request - " chrome in front of the subject.
    [InlineData("Meeting request - Atlas roadmap review | Contoso | jane.doe@contoso.com | Microsoft Teams",
                "Atlas roadmap review")]
    [InlineData("Meeting request - Revue budget | Microsoft Teams", "Revue budget")]
    [InlineData("TR: Atelier baseline | Microsoft Teams", "Atelier baseline")]
    [InlineData("Demande de réunion - Point mensuel | Microsoft Teams", "Point mensuel")]
    // Prefix chrome with a GENERIC remainder must yield nothing, not the generic label.
    [InlineData("Meeting request - Réunion | Microsoft Teams", null)]
    public void PickSubject_StripsOutlookChromePrefixes(string title, string? expected)
    {
        var sel = new TeamsSelectors();
        var folded = sel.GenericTitles.Select(TeamsMonitor.Fold).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected, TeamsMonitor.PickSubject(title, folded, sel.SubjectStripPrefixes));
    }

    [Fact]
    public void CompactViewHints_ShipByDefault_AndMatchTheLiveLabel()
    {
        // The compact tile must be identifiable (window scoring + "enlarge" alert depend on it).
        var folded = new TeamsSelectors().CompactViewTitleHints.Select(TeamsMonitor.Fold).ToList();
        Assert.NotEmpty(folded);
        Assert.Contains(folded, h => TeamsMonitor.Fold("Affichage compact de réunion").Contains(h));
        Assert.Contains(folded, h => TeamsMonitor.Fold("Compact meeting view").Contains(h));
    }
}
