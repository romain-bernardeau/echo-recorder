using Echo.Core.Storage;

namespace Echo.Tests;

/// <summary>
/// 5.9.0 safety review: the recordings root anchors every path guard and clean-up, so it must be
/// a folder of its own; meeting names coming from Teams must always give a valid folder name.
/// </summary>
public class StorageSafetyTests
{
    [Fact]
    public void RecordingsRoot_RefusesDriveRootsNetworkAndProfileFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.True(StoragePaths.IsUnsafeRecordingsRoot(@"C:\"));
        Assert.True(StoragePaths.IsUnsafeRecordingsRoot(@"D:"));
        Assert.True(StoragePaths.IsUnsafeRecordingsRoot(@"\\server\share\Echo"));
        Assert.True(StoragePaths.IsUnsafeRecordingsRoot(profile));
        Assert.True(StoragePaths.IsUnsafeRecordingsRoot(profile + "\\"));
        Assert.True(StoragePaths.IsUnsafeRecordingsRoot(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        Assert.True(StoragePaths.IsUnsafeRecordingsRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.True(StoragePaths.IsUnsafeRecordingsRoot(""));
    }

    [Fact]
    public void RecordingsRoot_AcceptsADedicatedFolder()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.False(StoragePaths.IsUnsafeRecordingsRoot(Path.Combine(docs, "Echo")));
        Assert.False(StoragePaths.IsUnsafeRecordingsRoot(@"D:\Reunions"));
    }

    [Theory]
    [InlineData("Point\u0007 hebdo\t", "Point hebdo")]
    [InlineData("Revue.", "Revue")]
    [InlineData("a/b\\c:d", "abcd")]
    public void SafeName_DropsControlCharsAndTrailingDots(string raw, string expected)
        => Assert.Equal(expected, StoragePaths.SafeName(raw));
}
