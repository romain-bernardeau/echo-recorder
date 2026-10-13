using System.Diagnostics;

namespace Echo.Core.Models;

/// <summary>
/// A single monotonic anchor shared by every track and the speaker watcher, so
/// audio timelines and speakers.jsonl align. Mirrors the Python session clock
/// {wall0, perf0_ns} captured once at Recorder.start.
/// </summary>
public readonly record struct CaptureClock(long PerfTimestamp, DateTime WallUtc)
{
    public static CaptureClock StartNow() => new(Stopwatch.GetTimestamp(), DateTime.UtcNow);

    /// <summary>Seconds elapsed since the clock anchor, from a high-resolution timestamp.</summary>
    public double ElapsedSeconds(long perfTimestamp) =>
        (perfTimestamp - PerfTimestamp) / (double)Stopwatch.Frequency;

    /// <summary>Recording-relative seconds for the current instant.</summary>
    public double NowSeconds() => ElapsedSeconds(Stopwatch.GetTimestamp());

    /// <summary>Convert a wall-clock epoch (seconds) into recording-relative seconds.</summary>
    public double WallEpochToRelative(double epochSeconds)
    {
        var wall0 = new DateTimeOffset(WallUtc).ToUnixTimeMilliseconds() / 1000.0;
        return epochSeconds - wall0;
    }
}

/// <summary>Result of one recorded track (mic or system).</summary>
public sealed record TrackResult(string Label, string WavPath, double DurationSeconds, string? Error = null);

/// <summary>One speaker-attributed span of the final transcript.</summary>
public sealed record TranscriptTurn(double StartS, double EndS, string Speaker, string Text);

/// <summary>One line of speakers.jsonl: active speaker names at a wall-clock instant.</summary>
public sealed record SpeakerTick(double T, IReadOnlyList<string> Speakers);

/// <summary>Meeting name + roster snapshot + whether a call is actually active.</summary>
public sealed record MeetingInfo(string Name, IReadOnlyList<string> Roster, bool InCall);

/// <summary>List-row for the recents/all views.</summary>
public sealed record SessionInfo(string Path, string Name, string Sub, bool Done);

/// <summary>Result of an update check.</summary>
public sealed record UpdateInfo(bool Available, string? Version, string? Url, string? Notes, string? Sha256);
