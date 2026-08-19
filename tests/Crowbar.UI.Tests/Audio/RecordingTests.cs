using Crowbar.Engine.Audio;
using Crowbar.FileSystems;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Tests headless de l'écriture WAV et du tap d'enregistrement : on écrit, on
/// relit avec <see cref="WavDecoder"/> et on compare le PCM. Le mode micro
/// (périphérique de capture) est volontairement hors de ces tests sans device.
/// </summary>
public class RecordingTests
{
    [Fact]
    public void WavWriter_RoundTripsThroughWavDecoder()
    {
        var mono = AudioTestData.Tone(440f, 0.1f);
        var stereo = new float[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            stereo[i * 2] = mono[i];
            stereo[i * 2 + 1] = mono[i];
        }

        using var stream = new MemoryStream();
        var writer = new WavWriter(stream, 48000, 2);
        writer.WriteInterleaved(stereo);
        writer.Close();

        using var decoder = new WavDecoder(stream.ToArray());
        Assert.Equal(mono.Length, decoder.TotalFrames);

        var output = new float[stereo.Length];
        Assert.Equal(mono.Length, decoder.Read(output));

        // Tolérance PCM 16 bits (quantification ≈ 1/32767).
        const float tolerance = 1f / 32767f;
        for (var i = 0; i < stereo.Length; i++)
            Assert.InRange(output[i], stereo[i] - tolerance, stereo[i] + tolerance);
    }

    [Fact]
    public void OutputRecorder_TapsMasterAfterMix()
    {
        using var system = new AudioSystem();
        var clip = AudioClip.Create("dc", 48000, AudioTestData.Constant(0.5f, AudioSystem.BlockSize), 1);
        system.Play(clip);

        var path = $"Audio/Recordings/test-{Guid.NewGuid():N}.wav";
        try
        {
            system.Recorder.StartOutput(path);
            var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];
            system.RenderBlock(buffer);
            system.Recorder.Stop();

            var bytes = FileSystem.Project.ReadAllBytes(path);
            using var decoder = new WavDecoder(bytes);

            Assert.Equal(AudioSystem.BlockSize, decoder.TotalFrames);
            var output = new float[AudioSystem.BlockSize * AudioSystem.Channels];
            decoder.Read(output);
            Assert.Contains(output, sample => MathF.Abs(sample) > 0.01f);
        }
        finally
        {
            if (FileSystem.Project.FileExists(path))
                FileSystem.Project.DeleteFile(path);
        }
    }
}
