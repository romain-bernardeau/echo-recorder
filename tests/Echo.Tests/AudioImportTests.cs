using System.Text.Json;
using Echo.Ai;

namespace Echo.Tests;

/// <summary>
/// Audio-file import (5.5.0): an external recording becomes a regular session folder whose
/// single <c>system</c> track the normal pipeline transcribes.
/// </summary>
public class AudioImportTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"echo_imp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    [Theory]
    [InlineData(@"C:\x\Réunion client.mp3", true)]
    [InlineData(@"C:\x\note.M4A", true)]
    [InlineData(@"C:\x\Teams recording.mp4", true)]
    [InlineData(@"C:\x\a.wav", true)]
    [InlineData(@"C:\x\transcript.docx", false)]
    [InlineData(@"C:\x\transcript.vtt", false)]
    [InlineData(@"C:\x\noext", false)]
    public void IsAudioFile_RoutesByExtension(string path, bool expected)
        => Assert.Equal(expected, AudioImport.IsAudioFile(path));

    [Fact]
    public void EstimateStart_SubtractsDuration_AndNeverGoesPastNow()
    {
        var now = new DateTime(2026, 9, 25, 12, 0, 0);
        Assert.Equal(new DateTime(2026, 9, 24, 9, 0, 0),
            AudioImport.EstimateStart(new DateTime(2026, 9, 24, 10, 0, 0), 3600, now));
        // A file time in the future (clock skew, copied file) is clamped to now first.
        Assert.Equal(new DateTime(2026, 9, 25, 11, 30, 0),
            AudioImport.EstimateStart(new DateTime(2026, 9, 30, 0, 0, 0), 1800, now));
        Assert.Equal(new DateTime(2026, 9, 24, 10, 0, 0),
            AudioImport.EstimateStart(new DateTime(2026, 9, 24, 10, 0, 0), double.NaN, now));
    }

    [Fact]
    public void WriteMetadata_ProducesASidecarTheTranscriberAccepts()
    {
        var dir = NewDir();
        try
        {
            var start = new DateTime(2026, 9, 24, 9, 0, 0);
            AudioImport.WriteMetadata(dir, @"C:\x\Point hebdo.m4a", "Point hebdo", start, 1234.5);

            var tr = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "system.json"))).RootElement;
            Assert.True(tr.GetProperty("got_audio").GetBoolean());
            Assert.Equal(new DateTimeOffset(start).ToUnixTimeSeconds(), (long)tr.GetProperty("t0_wall_epoch").GetDouble());
            Assert.Equal(1234.5, tr.GetProperty("duration_s").GetDouble());

            var mj = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meeting.json"))).RootElement;
            Assert.Equal("Point hebdo", mj.GetProperty("name").GetString());
            Assert.Equal("2026-09-24 09:00", mj.GetProperty("date_str").GetString());
            Assert.False(mj.GetProperty("physical").GetBoolean());
            Assert.Equal("Point hebdo.m4a", mj.GetProperty("imported_from").GetString());

            // session.json carries the duration shown in the meeting list.
            var sj = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "session.json"))).RootElement;
            Assert.Equal(1234.5, sj.GetProperty("duration_s").GetDouble());
            Assert.False(File.Exists(Path.Combine(dir, "mic.json")));   // no "Vous" track
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Decode_ConvertsToA16kMonoWav_OrRejectsAnUnreadableFile()
    {
        if (!AudioArchive.FfmpegUsable()) return;   // decoding needs ffmpeg (bundled in releases)
        var dir = NewDir();
        try
        {
            // 3 s of 44.1 kHz stereo tone as the "external" recording.
            var src = Path.Combine(dir, "source.wav");
            WriteTone(src, seconds: 3, rate: 44100, channels: 2);
            var outWav = Path.Combine(dir, "system.wav");
            double dur = AudioImport.Decode(src, outWav);
            Assert.InRange(dur, 2.9, 3.1);
            var head = File.ReadAllBytes(outWav);
            Assert.Equal(1, BitConverter.ToInt16(head, 22));          // mono
            Assert.Equal(16000, BitConverter.ToInt32(head, 24));      // 16 kHz
            Assert.True(File.Exists(src));                            // source untouched

            var junk = Path.Combine(dir, "junk.mp3");
            File.WriteAllText(junk, "not audio at all");
            Assert.ThrowsAny<Exception>(() => AudioImport.Decode(junk, Path.Combine(dir, "junk.wav")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void WriteTone(string path, int seconds, int rate, short channels)
    {
        int frames = seconds * rate;
        using var w = new BinaryWriter(File.Create(path));
        int data = frames * channels * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + data); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write(channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(data);
        for (int i = 0; i < frames; i++)
        {
            var v = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / rate));
            for (int c = 0; c < channels; c++) w.Write(v);
        }
    }
}
