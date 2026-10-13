using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Capsule
{
    // Local Windows media sessions: no Spotify API, browser extension, credentials, or network requests.
    public sealed class WindowsMusicSource : IMusicSource
    {
        sealed class Player
        {
            public GlobalSystemMediaTransportControlsSession Session;
            public string Key = Guid.NewGuid().ToString("N"), MetadataKey;
            public ImageSource Artwork;
        }
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        readonly List<Player> players = new List<Player>();
        GlobalSystemMediaTransportControlsSessionManager manager;

        public async Task<MusicSnapshot> ReadAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager"))
                    return new MusicSnapshot { Note = "Music controls need Windows 10 version 1809 or later." };
                return await ReadSupportedAsync().ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        async Task<MusicSnapshot> ReadSupportedAsync()
        {
            if (manager == null) manager = await Finish(GlobalSystemMediaTransportControlsSessionManager.RequestAsync()).ConfigureAwait(false);
            var snapshot = new MusicSnapshot();
            var active = new List<Player>();
            var current = manager.GetCurrentSession();
            foreach (var session in manager.GetSessions())
            {
                try
                {
                    string source = MusicSelection.SourceName(session.SourceAppUserModelId);
                    if (source == null) continue;
                    Player player = players.FirstOrDefault(p => Equals(p.Session, session)) ?? new Player { Session = session };
                    active.Add(player);
                    var properties = await Finish(session.TryGetMediaPropertiesAsync()).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(properties.Title)) continue;
                    var playback = session.GetPlaybackInfo();
                    var timeline = session.GetTimelineProperties();
                    bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    string metadata = properties.Title + "\0" + properties.Artist + "\0" + properties.AlbumTitle;
                    if (metadata != player.MetadataKey)
                    {
                        player.Artwork = null;
                        // Mark before reading: a player with unusable artwork must not keep rereading it every second.
                        player.MetadataKey = metadata;
                        if (properties.Thumbnail != null)
                        {
                            try { player.Artwork = await ReadArtwork(properties.Thumbnail).ConfigureAwait(false); }
                            catch { /* artwork is optional; metadata and controls still work */ }
                        }
                    }
                    double duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
                    double position = (timeline.Position - timeline.StartTime).TotalSeconds;
                    if (playing && timeline.LastUpdatedTime.Year >= 1970)
                    {
                        double elapsed = Math.Max(0, (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds);
                        position += elapsed * (playback.PlaybackRate ?? 1);
                    }
                    var controls = playback.Controls;
                    snapshot.Tracks.Add(new MusicTrack
                    {
                        Key = player.Key, Source = source, Title = properties.Title, Artist = properties.Artist, Album = properties.AlbumTitle,
                        Playing = playing, Artwork = player.Artwork,
                        CanToggle = controls.IsPlayPauseToggleEnabled || (playing ? controls.IsPauseEnabled : controls.IsPlayEnabled),
                        CanPrevious = controls.IsPreviousEnabled, CanNext = controls.IsNextEnabled,
                        DurationSeconds = duration, PositionSeconds = Math.Max(0, duration > 0 ? Math.Min(duration, position) : position),
                    });
                    if (Equals(current, session)) snapshot.SelectedKey = player.Key;
                }
                catch { /* A tab or player can close while its properties are being read. */ }
            }
            players.Clear();
            players.AddRange(active);
            if (snapshot.Tracks.Count == 0) snapshot.Note = "Play a song in Spotify or YouTube Music to see it here. Keep the app or browser tab open.";
            return snapshot;
        }

        public async Task<bool> CommandAsync(string key, MusicCommand command)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Player player = players.FirstOrDefault(p => p.Key == key);
                // Never send global media keys: target exactly the session whose controls were clicked.
                if (player == null || manager == null || !manager.GetSessions().Any(s => Equals(s, player.Session))) return false;
                var session = player.Session;
                var playback = session.GetPlaybackInfo();
                var controls = playback.Controls;
                if (command == MusicCommand.Previous) return controls.IsPreviousEnabled && await Finish(session.TrySkipPreviousAsync()).ConfigureAwait(false);
                if (command == MusicCommand.Next) return controls.IsNextEnabled && await Finish(session.TrySkipNextAsync()).ConfigureAwait(false);
                if (controls.IsPlayPauseToggleEnabled) return await Finish(session.TryTogglePlayPauseAsync()).ConfigureAwait(false);
                if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    return controls.IsPauseEnabled && await Finish(session.TryPauseAsync()).ConfigureAwait(false);
                return controls.IsPlayEnabled && await Finish(session.TryPlayAsync()).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        // A disappearing or hung player must not leave the module or a control button permanently busy.
        static async Task<T> Finish<T>(IAsyncOperation<T> operation)
        {
            try
            {
                // The compiler shipped with Windows reads the split OS WinMD files. Its generic async projection
                // differs from AsTask's SDK projection, so bridge the native completion directly.
                var completion = new TaskCompletionSource<T>();
                operation.Completed = delegate(IAsyncOperation<T> result, AsyncStatus status)
                {
                    try
                    {
                        if (status == AsyncStatus.Completed) completion.TrySetResult(result.GetResults());
                        else if (status == AsyncStatus.Canceled) completion.TrySetCanceled();
                        else completion.TrySetException(result.ErrorCode ?? new InvalidOperationException("Media operation failed."));
                    }
                    catch (Exception e) { completion.TrySetException(e); }
                };
                Task<T> task = completion.Task;
                if (await Task.WhenAny(task, Task.Delay(3000)).ConfigureAwait(false) != task)
                {
                    operation.Cancel();
                    // Observe a late fault after cancellation too.
                    ObserveFault(task);
                    throw new TimeoutException();
                }
                return await task.ConfigureAwait(false);
            }
            finally { operation.Close(); }
        }

        static void ObserveFault(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        static async Task<ImageSource> ReadArtwork(IRandomAccessStreamReference thumbnail)
        {
            using (IRandomAccessStreamWithContentType stream = await Finish(thumbnail.OpenReadAsync()).ConfigureAwait(false))
            {
                if (stream.Size == 0 || stream.Size > 4 * 1024 * 1024) return null;
                using (var reader = new DataReader(stream))
                {
                    uint length = (uint)stream.Size;
                    uint loaded = await Finish(reader.LoadAsync(length)).ConfigureAwait(false);
                    if (loaded != length) return null;
                    var bytes = new byte[length];
                    reader.ReadBytes(bytes);
                    using (var memory = new MemoryStream(bytes))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = 160;
                        bitmap.StreamSource = memory;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
            }
        }
    }
}
