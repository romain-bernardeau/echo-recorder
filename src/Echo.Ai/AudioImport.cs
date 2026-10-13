using System.Text.Json;
using System.Text.Json.Nodes;

namespace Echo.Ai;

/// <summary>
/// Turns an external audio (or video) file — a phone/dictaphone recording, a Teams .mp4,
/// a WhatsApp voice note… — into a regular Echo session folder, so the normal pipeline
/// (transcription → CR → summary) runs on it exactly as on a recorded meeting.
/// The file is decoded by ffmpeg into a single <c>system.wav</c> track (16 kHz mono PCM)
/// with its <c>system.json</c> sidecar: single-track sessions are transcribed as "remote"
/// speakers, so nobody is wrongly labelled "Vous". The source file is never modified.
/// </summary>
public static class AudioImport
{
    /// <summary>Extensions offered in the import dialog and routed to this importer
    /// (everything ffmpeg decodes that people actually have lying around).</summary>
    public static readonly string[] Extensions =
    {
        ".mp3", ".wav", ".m4a", ".aac", ".wma", ".ogg", ".opus", ".flac", ".amr", ".3gp",
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi",
    };

    public static bool IsAudioFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Best guess of when the recording STARTED: the file's last write (≈ end of
    /// recording on a recorder/phone) minus its duration; never in the future.</summary>
    public static DateTime EstimateStart(DateTime lastWriteLocal, double durationSeconds, DateTime nowLocal)
    {
        var t = lastWriteLocal > nowLocal ? nowLocal : lastWriteLocal;
        if (durationSeconds > 0 && double.IsFinite(durationSeconds)) t = t.AddSeconds(-durationSeconds);
        return t;
    }

    /// <summary>Decode <paramref name="sourceFile"/> into the 16 kHz mono PCM WAV <paramref name="outWav"/>.
    /// Returns its duration (s). Throws when ffmpeg cannot read the file or it has no audio.</summary>
    public static double Decode(string sourceFile, string outWav) => Decode(sourceFile, outWav, CancellationToken.None);

    /// <summary>Same as <see cref="Decode(string, string)"/>, cancellable: a cancelled token kills
    /// the ffmpeg decode of a long video and rethrows <see cref="OperationCanceledException"/>.</summary>
    public static double Decode(string sourceFile, string outWav, CancellationToken ct)
    {
        AudioPrep.ConvertToWav16kMono(sourceFile, outWav, ct);
        double dur = AudioPrep.WavDuration(outWav);
        if (dur < 1.0)
        {
            try { File.Delete(outWav); } catch { }
            throw new InvalidDataException("Le fichier ne contient pas de piste audio exploitable.");
        }
        return dur;
    }

    /// <summary>Write the track sidecar (<c>system.json</c>), <c>meeting.json</c> and
    /// <c>session.json</c> of an imported session whose <c>system.wav</c> is already decoded.</summary>
    public static void WriteMetadata(string sessionDir, string sourceFile, string meetingName, DateTime startLocal, double dur)
    {
        var wav = Path.Combine(sessionDir, "system.wav");
        var t0 = new DateTimeOffset(startLocal).ToUnixTimeMilliseconds() / 1000.0;
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var track = new JsonObject
        {
            ["got_audio"] = true,
            ["t0_wall_epoch"] = t0,
            ["duration_s"] = dur,
            ["source"] = "import",
            ["imported_from"] = Path.GetFileName(sourceFile),
        };
        File.WriteAllText(Path.Combine(sessionDir, "system.json"), track.ToJsonString(opts));

        var meeting = new JsonObject
        {
            ["name"] = meetingName,
            ["date_str"] = startLocal.ToString("yyyy-MM-dd HH:mm"),
            ["roster"] = new JsonArray(),
            ["physical"] = false,
            ["started"] = (long)t0,
            ["source"] = "audio_import",
            ["imported_from"] = Path.GetFileName(sourceFile),
        };
        File.WriteAllText(Path.Combine(sessionDir, "meeting.json"), meeting.ToJsonString(opts));

        var session = new JsonObject
        {
            ["out_dir"] = sessionDir,
            ["physical"] = false,
            ["session_wall0_epoch"] = t0,
            ["duration_s"] = dur,
            ["tracks"] = new JsonObject { ["remote"] = new JsonObject { ["wav"] = wav, ["duration_s"] = dur, ["error"] = null } },
        };
        File.WriteAllText(Path.Combine(sessionDir, "session.json"), session.ToJsonString(opts));
    }
}
