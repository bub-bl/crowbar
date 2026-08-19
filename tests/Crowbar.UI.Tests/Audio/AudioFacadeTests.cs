using Crowbar.Engine.Audio;
using Crowbar.FileSystems;

namespace Crowbar.Audio.Tests;

/// <summary>
/// Headless tests for the static <see cref="Audio"/> facade: asynchronous
/// path playback and the nullable listener.
/// </summary>
public class AudioFacadeTests
{
    [Fact]
    public async Task PlayAsync_LoadsAndPlaysClip()
    {
        var path = $"facade-test-{Guid.NewGuid():N}.wav";
        using var system = new AudioSystem();
        Crowbar.Engine.Audio.Audio.Bind(system);
        try
        {
            var mono = AudioTestData.Tone(440f, 0.1f);
            FileSystem.Project.WriteAllBytes(path, AudioTestData.BuildWav(mono, 1));

            var handle = await Crowbar.Engine.Audio.Audio.PlayAsync(path);
            Assert.True(handle.IsValid);

            var buffer = new float[AudioSystem.BlockSize * AudioSystem.Channels];
            system.RenderBlock(buffer);
            Assert.Contains(buffer, sample => MathF.Abs(sample) > 1e-4f);
        }
        finally
        {
            Crowbar.Engine.Audio.Audio.Unbind();
            AudioClip.Invalidate(path);
            if (FileSystem.Project.FileExists(path))
                FileSystem.Project.DeleteFile(path);
        }
    }

    [Fact]
    public void Listener_IsNullWhenUnbound()
    {
        Crowbar.Engine.Audio.Audio.Unbind();
        Assert.Null(Crowbar.Engine.Audio.Audio.Listener);
    }
}
