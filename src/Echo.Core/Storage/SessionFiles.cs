namespace Echo.Core.Storage;

/// <summary>
/// Small facts about a session folder shared by the engine, the list and the sweeper
/// (each used to carry its own copy of these checks).
/// </summary>
public static class SessionFiles
{
    /// <summary>A transcript in any layout (see <see cref="SessionLayout.FindTranscript"/>).</summary>
    public static bool HasTranscript(string dir) => SessionLayout.HasTranscript(dir);

    /// <summary>
    /// Something a transcription can be run on: a mic/system track (.wav or its .mp3 archive)
    /// with more than a header, or the "… - Audio.mp3" listening mixdown. False once the
    /// retention policy purged the audio: "Relancer la transcription" is then pointless.
    /// </summary>
    public static bool HasAudio(string dir)
    {
        try
        {
            foreach (var tag in SessionLayout.Tags)
                foreach (var ext in new[] { ".wav", ".mp3" })
                {
                    var p = SessionLayout.Find(dir, tag + ext);
                    if (File.Exists(p) && new FileInfo(p).Length > 200) return true;
                }
            return Directory.EnumerateFiles(dir, "*- Audio.mp3").Any();
        }
        catch { return false; }
    }

    /// <summary>A compte rendu in either language or a legacy name (see <see cref="SessionLayout.IsCr"/>).</summary>
    public static bool HasCr(string dir) => SessionLayout.HasCr(dir);

    public static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                         StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="dir"/> if free, else "<paramref name="dir"/> (2)", "(3)"…: two sessions
    /// started in the same minute with the same name must never share a folder (the second
    /// one overwrote the WAVs of the first, still being transcribed).
    /// </summary>
    public static string UniqueDir(string dir)
    {
        if (!Directory.Exists(dir) && !File.Exists(dir)) return dir;
        for (int i = 2; ; i++)
        {
            var c = $"{dir} ({i})";
            if (!Directory.Exists(c) && !File.Exists(c)) return c;
        }
    }
}
