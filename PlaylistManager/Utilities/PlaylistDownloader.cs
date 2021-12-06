﻿using BeatSaberPlaylistsLib.Types;
using BeatSaverSharp;
using BeatSaverSharp.Models;
using IPA.Loader;
using PlaylistManager.Types;
using SiraUtil;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zenject;

namespace PlaylistManager.Utilities
{
    public class PlaylistDownloader : IInitializable, IDisposable
    {
        private readonly SiraClient siraClient;
        private readonly BeatSaver beatSaverInstance;
        private readonly SemaphoreSlim downloadSemaphore;
        private static readonly HashSet<string> ownedHashes = new HashSet<string>();
        private DownloadQueueEntry currentDownload;

        private readonly SemaphoreSlim pauseSemaphore;
        private readonly SemaphoreSlim customArchiveSemaphore;
        private bool preferCustomArchiveURL;
        private bool disposed;

        public event Action PopupEvent;
        public event Action QueueUpdatedEvent;

        public static readonly List<object> downloadQueue = new List<object>();
        public PopupContents PendingPopup { get; private set; }

        public PlaylistDownloader([Inject(Id = nameof(PlaylistManager))] PluginMetadata metadata, SiraClient siraClient)
        {
            this.siraClient = siraClient;
            BeatSaverOptions options = new BeatSaverOptions(applicationName: metadata.Name, version: metadata.HVersion.ToString());
            beatSaverInstance = new BeatSaver(options);
            downloadSemaphore = new SemaphoreSlim(1, 1);
            pauseSemaphore = new SemaphoreSlim(0, 1);
            customArchiveSemaphore = new SemaphoreSlim(0, 1);
            PendingPopup = null;
        }

        public void Initialize()
        {
            foreach (DownloadQueueEntry _ in downloadQueue)
            {
                IterateQueue();
            }
        }

        public void Dispose()
        {
            disposed = true;
            if (currentDownload != null && !currentDownload.cancellationTokenSource.IsCancellationRequested)
            {
                currentDownload.cancellationTokenSource.Cancel();
            }
        }

        public void QueuePlaylist(DownloadQueueEntry downloadQueueEntry)
        {
            downloadQueue.Add(downloadQueueEntry);
            downloadQueueEntry.DownloadAbortedEvent += OnDownloadAborted;
            QueueUpdatedEvent?.Invoke();
            IterateQueue();
        }

        private void OnDownloadAborted(DownloadQueueEntry downloadQueueEntry)
        {
            downloadQueueEntry.DownloadAbortedEvent -= OnDownloadAborted;
            downloadQueue.Remove(downloadQueueEntry); 
            QueueUpdatedEvent?.Invoke();
        }

        private async void IterateQueue()
        {
            await downloadSemaphore.WaitAsync();
            if (downloadQueue.Count > 0 && !disposed)
            {
                await DownloadPlaylist(downloadQueue.OfType<DownloadQueueEntry>().FirstOrDefault());
                if (!disposed)
                {
                    downloadQueue.RemoveAt(0);
                }
                QueueUpdatedEvent?.Invoke();
            }
            downloadSemaphore.Release();
        }

        internal void OnQueueClear()
        {
            if (downloadQueue.Count == 0)
            {
                SongCore.Loader.Instance.RefreshSongs(false);
                ownedHashes.Clear();
            }
        }

        internal void PauseDownload()
        {
            if (currentDownload != null && !currentDownload.cancellationTokenSource.IsCancellationRequested)
            {
                currentDownload.cancellationTokenSource.Cancel();
            }
        }

        internal void ResumeDownload()
        {
            if (currentDownload != null && pauseSemaphore.CurrentCount == 0)
            {
                pauseSemaphore.Release();
            }
        }

        private async Task DownloadPlaylist(DownloadQueueEntry downloadQueueEntry)
        {
            downloadQueueEntry.DownloadAbortedEvent -= OnDownloadAborted;

            currentDownload = downloadQueueEntry;
            List<IPlaylistSong> missingSongs = PlaylistLibUtils.GetMissingSongs(downloadQueueEntry.playlist, ownedHashes);
            downloadQueueEntry.Report(0);

            preferCustomArchiveURL = true;
            bool shownCustomArchiveWarning = false;

            for (int i = 0; i < missingSongs.Count; i++)
            {
                if (preferCustomArchiveURL && missingSongs[i].TryGetCustomData("customArchiveURL", out object outCustomArchiveURL))
                {
                    string customArchiveURL = (string)outCustomArchiveURL;
                    string identifier = PlaylistLibUtils.GetIdentifierForPlaylistSong(missingSongs[i]);
                    if (identifier == "")
                    {
                        continue;
                    }

                    if (!shownCustomArchiveWarning)
                    {
                        shownCustomArchiveWarning = true;
                        PendingPopup = new YesNoPopupContents("This playlist uses mirror download links. Would you like to use them?", yesButtonPressedCallback: () => SetCustomArchivePreference(true),
                             noButtonPressedCallback: () => SetCustomArchivePreference(false), animateParentCanvas: false);
                        PopupEvent?.Invoke();

                        await customArchiveSemaphore.WaitAsync();
                        PendingPopup = null;

                        if (!preferCustomArchiveURL)
                        {
                            i--;
                            continue;
                        }
                    }
                    await BeatmapDownloadByCustomURL(customArchiveURL, identifier, downloadQueueEntry.cancellationTokenSource.Token);
                }
                else if (!string.IsNullOrEmpty(missingSongs[i].Hash))
                {
                    await BeatmapDownloadByHash(missingSongs[i].Hash, downloadQueueEntry.cancellationTokenSource.Token);
                }
                else if (!string.IsNullOrEmpty(missingSongs[i].Key))
                {
                    string hash = await BeatmapDownloadByKey(missingSongs[i].Key.ToLower(), downloadQueueEntry.cancellationTokenSource.Token);
                    if (!string.IsNullOrEmpty(hash))
                    {
                        missingSongs[i].Hash = hash;
                    }
                }

                downloadQueueEntry.Report((i + 1) / ((double)missingSongs.Count));

                if (downloadQueueEntry.Aborted)
                {
                    break;
                }
                else if (disposed)
                {
                    // If downloader is disposed, a soft restart is happening. Reinstantiate the cancellation token and leave.
                    downloadQueueEntry.cancellationTokenSource = new CancellationTokenSource();
                    return;
                }
                else if (downloadQueueEntry.cancellationTokenSource.IsCancellationRequested)
                {
                    // If we directly cancel, it is a pause. So we wait at this semaphore till it is released.
                    await pauseSemaphore.WaitAsync();
                    i--;
                    downloadQueueEntry.cancellationTokenSource = new CancellationTokenSource();
                }
            }

            downloadQueueEntry.parentManager.StorePlaylist(downloadQueueEntry.playlist);
            currentDownload = null;
        }

        private void SetCustomArchivePreference(bool preferCustomArchiveURL)
        {
            this.preferCustomArchiveURL = preferCustomArchiveURL;
            customArchiveSemaphore.Release();
        }

        private async Task BeatSaverBeatmapDownload(Beatmap song, BeatmapVersion songversion, CancellationToken token, IProgress<double> progress = null)
        {
            string customSongsPath = CustomLevelPathHelper.customLevelsDirectoryPath;
            if (!Directory.Exists(customSongsPath))
            {
                Directory.CreateDirectory(customSongsPath);
            }

            if (!ownedHashes.Contains(songversion.Hash.ToUpper()))
            {
                var zip = await songversion.DownloadZIP(token, progress).ConfigureAwait(false);
                await ExtractZipAsync(zip, customSongsPath, songInfo: song).ConfigureAwait(false);
                ownedHashes.Add(songversion.Hash.ToUpper());
            }
        }

        private async Task<string> BeatmapDownloadByKey(string key, CancellationToken token, IProgress<double> progress = null)
        {
            bool songDownloaded = false;
            while (!songDownloaded)
            {
                try
                {
                    var song = await beatSaverInstance.Beatmap(key, token);
                    // A key is not enough to identify a specific version. So just get the latest one.
                    if (SongCore.Loader.GetLevelByHash(song.LatestVersion.Hash) == null)
                    {
                        await BeatSaverBeatmapDownload(song, song.LatestVersion, token, progress);
                    }
                    songDownloaded = true;
                    return song.LatestVersion.Hash;
                }
                catch (Exception e)
                {
                    if (!(e is TaskCanceledException))
                    {
                        Plugin.Log.Info(string.Format("Failed to download Song {0}. Exception: {1}", key, e.ToString()));
                    }
                    songDownloaded = true;
                }
            }
            return "";
        }

        private async Task BeatmapDownloadByHash(string hash, CancellationToken token, IProgress<double> progress = null)
        {
            bool songDownloaded = false;
            while (!songDownloaded)
            {
                try
                {
                    var song = await beatSaverInstance.BeatmapByHash(hash, token);
                    if (song == null)
                    {
                        Plugin.Log.Info(string.Format("Failed to download Song {0}. Unable to find a beatmap for that hash.", hash));
                        return;
                    }

                    BeatmapVersion matchingVersion = null;
                    foreach (BeatmapVersion version in song.Versions)
                    {
                        if (hash.ToLowerInvariant() == version.Hash.ToLowerInvariant())
                        {
                            matchingVersion = version;
                        }
                    }

                    // Just download exact hash matches for now. Updating to a newer version of a song based on a hash should require some user interaction or option setting.
                    if (matchingVersion != null)
                    {
                        await BeatSaverBeatmapDownload(song, matchingVersion, token, progress);
                    }
                    else
                    {
                        Plugin.Log.Info(string.Format("Failed to download Song {0}. Unable to find a matching version for that hash.", hash));
                    }
                    songDownloaded = true;
                }
                catch (Exception e)
                {
                    if (!(e is TaskCanceledException))
                    {
                        Plugin.Log.Info(string.Format("Failed to download Song {0}. Exception: {1}", hash, e.ToString()));
                    }
                    songDownloaded = true;
                }
            }
        }

        private async Task BeatmapDownloadByCustomURL(string url, string songName, CancellationToken token)
        {
            try
            {
                string customSongsPath = CustomLevelPathHelper.customLevelsDirectoryPath;
                if (!Directory.Exists(customSongsPath))
                {
                    Directory.CreateDirectory(customSongsPath);
                }
                WebResponse webResponse = await siraClient.GetAsync(url, token);
                if (webResponse.IsSuccessStatusCode)
                {
                    byte[] zip = webResponse.ContentToBytes();
                    await ExtractZipAsync(zip, customSongsPath, songName: songName).ConfigureAwait(false);
                }
                else
                {
                    Plugin.Log.Info(string.Format("Failed to download Song {0}", url));
                }
            }
            catch (Exception e)
            {
                if (!(e is TaskCanceledException))
                    Plugin.Log.Info(string.Format("Failed to download Song {0}", url));
            }
        }

        private async Task ExtractZipAsync(byte[] zip, string customSongsPath, bool overwrite = false, string songName = null, Beatmap songInfo = null)
        {
            Stream zipStream = new MemoryStream(zip);
            try
            {
                ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                string basePath = "";
                if (songInfo != null)
                {
                    basePath = songInfo.ID + " (" + songInfo.Metadata.SongName + " - " + songInfo.Metadata.LevelAuthorName + ")";
                }
                else
                {
                    basePath = songName;
                }
                basePath = string.Join("", basePath.Split(Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).ToArray()));
                string path = Path.Combine(customSongsPath, basePath);

                if (!overwrite && Directory.Exists(path))
                {
                    int pathNum = 1;
                    while (Directory.Exists(path + $" ({pathNum})")) ++pathNum;
                    path += $" ({pathNum})";
                }
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
                await Task.Run(() =>
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (!string.IsNullOrWhiteSpace(entry.Name))
                        {
                            var entryPath = Path.Combine(path, entry.Name); // Name instead of FullName for better security and because song zips don't have nested directories anyway
                            if (overwrite || !File.Exists(entryPath)) // Either we're overwriting or there's no existing file
                                entry.ExtractToFile(entryPath, overwrite);
                        }
                    }
                }).ConfigureAwait(false);
                archive.Dispose();
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"Unable to extract ZIP! Exception: {e}");
                return;
            }
            zipStream.Close();
        }
    }
}
