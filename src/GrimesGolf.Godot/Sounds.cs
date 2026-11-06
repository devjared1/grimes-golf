using System.Collections.Concurrent;
using Godot;
using GrimesGolf.Core.Audio;

namespace GrimesGolf.Game;

/// <summary>Plays the sound effects. They're made once in the background at start-up, so nothing stalls a shot the first time it's heard.</summary>
public partial class Sounds : Node
{
    private const int Voices = 6;
    private readonly List<AudioStreamPlayer> _players = new();
    private readonly ConcurrentDictionary<SoundEffect, byte[]> _made = new();
    private readonly Dictionary<SoundEffect, AudioStreamWav> _streams = new();

    public override void _Ready()
    {
        for (int i = 0; i < Voices; i++)
        {
            var p = new AudioStreamPlayer();
            AddChild(p);
            _players.Add(p);
        }
        Task.Run(() =>
        {
            foreach (var e in Enum.GetValues<SoundEffect>()) _made[e] = SoundSynth.ToBytes(SoundSynth.Make(e));
        });
    }

    /// <param name="volumeDb">Relative to full level; a sound not made yet is skipped rather than waited for.</param>
    public void Play(SoundEffect effect, float volumeDb = 0)
    {
        if (!_streams.TryGetValue(effect, out var stream))
        {
            if (!_made.TryGetValue(effect, out var data)) return;
            _streams[effect] = stream = new AudioStreamWav
            {
                Data = data, Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = SoundSynth.SampleRate, Stereo = false,
            };
        }
        // A free voice, or failing that the one that started longest ago.
        var player = _players.FirstOrDefault(p => !p.Playing) ?? _players.MaxBy(p => p.GetPlaybackPosition())!;
        player.Stream = stream;
        player.VolumeDb = volumeDb;
        player.Play();
    }
}
