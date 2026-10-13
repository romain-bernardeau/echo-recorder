using NAudio.CoreAudioApi;

namespace Echo.Audio;

/// <summary>
/// Volume/mute guard for endpoint loopback (port of audio_guard.ensure_endpoint_audible).
/// Before a non-physical endpoint capture, un-mutes the render endpoint and floors
/// its volume to 1% so the loopback mix isn't 100% silent. Never throws — capture
/// proceeds regardless (best-effort, respects the user by never raising volume loud).
/// </summary>
public static class AudioGuard
{
    /// <summary>1% floor applied when the endpoint is near-muted (user-respect choice).</summary>
    public const float VolumeFloor = 0.01f;

    public sealed record GuardResult(bool? MutedBefore, float? VolumeBefore,
        IReadOnlyList<string> Actions, string? Error);

    public static GuardResult EnsureEndpointAudible()
    {
        var actions = new List<string>();
        try
        {
            using var enumr = new MMDeviceEnumerator();
            var dev = enumr.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var vol = dev.AudioEndpointVolume;

            bool muted = vol.Mute;
            float scalar = vol.MasterVolumeLevelScalar;

            if (muted)
            {
                try { vol.Mute = false; actions.Add("demute"); }
                catch { /* ignore */ }
            }
            if (scalar < VolumeFloor)
            {
                try
                {
                    vol.MasterVolumeLevelScalar = VolumeFloor;
                    actions.Add($"volume {scalar:0.000}->{VolumeFloor:0.000}");
                }
                catch { /* ignore */ }
            }
            return new GuardResult(muted, scalar, actions, null);
        }
        catch (Exception ex)
        {
            // Endpoint unavailable / COM failure: never block capture.
            return new GuardResult(null, null, actions, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Friendly name of the default render endpoint (diagnostics), or null.</summary>
    public static string? DefaultRenderName()
    {
        try
        {
            using var enumr = new MMDeviceEnumerator();
            return enumr.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
        }
        catch
        {
            return null;
        }
    }
}
