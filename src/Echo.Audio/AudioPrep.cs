namespace Echo.Audio;

/// <summary>
/// ffmpeg resolution for the app (audio clips). The transcription-side preparation
/// (resample, silence trim, mixdown) lives in Echo.Ai.AudioPrep; this former copy of it
/// was dead code and is reduced to the one member still used (Echo.App Bridge.AudioClip).
/// </summary>
public static class AudioPrep
{
    private static volatile string? _ffmpegCache;

    /// <summary>
    /// ffmpeg.exe, resolved in the same order as Echo.Ai.AudioPrep.ResolveFfmpeg: the
    /// bundled &lt;appdir&gt;\bin\ffmpeg.exe first, then the PATH. Null if neither exists.
    /// Only a FOUND path is cached (and re-checked): a miss is never memorised, so an
    /// ffmpeg installed while the app runs is picked up on the next call.
    /// </summary>
    public static string? ResolveFfmpeg()
    {
        var cached = _ffmpegCache;
        if (cached is not null && File.Exists(cached)) return cached;
        _ffmpegCache = null;

        try
        {
            string local = Path.Combine(AppContext.BaseDirectory, "bin", "ffmpeg.exe");
            if (File.Exists(local)) return _ffmpegCache = local;
        }
        catch { /* fall back to PATH */ }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate)) return _ffmpegCache = candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
