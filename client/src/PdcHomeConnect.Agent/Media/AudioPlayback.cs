using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace PdcHomeConnect.Agent.Media;

/// <summary>Plays audio (TTS announcements, sounds) and speaks text with the Windows voices.</summary>
public static class AudioPlayback
{
    private static readonly object Gate = new();
    private static readonly HashSet<MediaPlayer> Active = [];

    /// <summary>Starts playback and returns immediately. The player is kept alive until it ends.</summary>
    public static void Play(IRandomAccessStream stream, string contentType, MediaPlayerAudioCategory category = MediaPlayerAudioCategory.Media)
    {
        Start(MediaSource.CreateFromStream(stream, contentType), category);
    }

    public static void Play(Uri uri, MediaPlayerAudioCategory category = MediaPlayerAudioCategory.Media)
    {
        Start(MediaSource.CreateFromUri(uri), category);
    }

    public static void Play(byte[] data, string contentType, MediaPlayerAudioCategory category = MediaPlayerAudioCategory.Media)
    {
        Play(new MemoryStream(data).AsRandomAccessStream(), contentType, category);
    }

    private static void Start(MediaSource source, MediaPlayerAudioCategory category)
    {
        var player = new MediaPlayer { AudioCategory = category, Volume = 1.0 };
        // Do not show up as a media session (would appear as "now playing" in our own media player).
        player.CommandManager.IsEnabled = false;
        player.Source = source;
        player.MediaEnded += (_, _) => Release(player);
        player.MediaFailed += (_, _) => Release(player);
        lock (Gate)
        {
            if (Active.Count >= 8)
            {
                var oldest = Active.First();
                Active.Remove(oldest);
                oldest.Dispose();
            }

            Active.Add(player);
        }

        player.Play();
        // Safety net: never keep a player forever.
        _ = Task.Delay(TimeSpan.FromMinutes(15)).ContinueWith(_ => Release(player), TaskScheduler.Default);
    }

    private static void Release(MediaPlayer player)
    {
        lock (Gate)
        {
            if (!Active.Remove(player))
            {
                return;
            }
        }

        try
        {
            player.Dispose();
        }
        catch
        {
            // Already gone.
        }
    }

    /// <summary>Speaks text with a Windows voice (optionally matching a language like "de-DE" or "en").</summary>
    public static async Task SpeakAsync(string text, string? language)
    {
        using var synthesizer = new SpeechSynthesizer();
        if (!string.IsNullOrWhiteSpace(language))
        {
            var voices = SpeechSynthesizer.AllVoices;
            var voice = voices.FirstOrDefault(v => v.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
                ?? voices.FirstOrDefault(v => v.Language.StartsWith(language.Split('-')[0] + "-", StringComparison.OrdinalIgnoreCase));
            if (voice is not null)
            {
                synthesizer.Voice = voice;
            }
        }

        var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
        Play(stream, stream.ContentType, MediaPlayerAudioCategory.Speech);
    }

    /// <summary>Installed voice languages (for the tray status dialog / docs).</summary>
    public static IReadOnlyList<string> VoiceLanguages() =>
        SpeechSynthesizer.AllVoices.Select(v => v.Language).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
