using System;
using System.Collections.Generic;
using System.IO;
using TLDRevamp.Multiplayer;
using UnityEngine.Networking;

namespace TLDRevamp.Net
{
    public static partial class ModCatalog
    {
        internal static BepInEx.Configuration.ConfigEntry<string> Url;
        internal static Catalog Current;
        internal static string Message = "";
        internal static bool RestartRequired;
        /// The start-up check: fetch the catalog once when the main menu first shows and offer the updates (RevampSettings).
        internal static bool CheckAtStart = true;
        private static bool _background;
        private static readonly Queue<KeyValuePair<Mod, Release>> Updates = new Queue<KeyValuePair<Mod, Release>>();
        private static readonly List<string> Updated = new List<string>(), NotUpdated = new List<string>();
        private static bool _updating;
        private static UnityWebRequest _request;
        private static LimitedDownload _download;
        private static Mod _mod;
        private static Release _release;
        internal static bool Busy => _request != null;
        internal static string Progress => Busy ? (_mod == null ? "Fetching catalog" : "Downloading " + _mod.Name) +
            " (" + ModTransfer.Size(_download.Length) + ", " + (_request.downloadProgress * 100f).ToString("F0") + "%)" : Message;

        internal static string Reason(Mod m, Release v) => ApprovalReason(m, v, ModTransfer.Approved(), ModTransfer.MaxBytes, MpApi.ValidId);
        // Catalog order is newest first; an unapproved version never hides an approved older one.
        internal static Release Offered(Mod m) => Offered(m, ModTransfer.Approved(), ModTransfer.MaxBytes, MpApi.ValidId);

        /// background: the start-up check, which runs with no screen open and says nothing when it fails.
        internal static void Refresh(bool background = false)
        {
            if (Busy) return;
            // The list fetched before (the start-up check, or the last open) stays on screen until the new one is in:
            // clearing it first showed only the installed mods ("Mods 1/1") for the second the download took.
            // A failed fetch keeps it too; the screen says the catalog couldn't be reached.
            string url = Url?.Value ?? DefaultUrl;
            if (!SourceUrl(url) && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile))
            { Current = null; Message = "Catalog URL must use HTTPS (or a file URI for local testing)."; return; }
            Start(url, MaxCatalogBytes, null, null);
            _background = background && Busy;
        }

        /// Downloads the given updates one after another; Message sums them up at the end.
        internal static void UpdateAll(List<KeyValuePair<Mod, Release>> updates)
        {
            if (Busy) return;
            Updates.Clear(); Updated.Clear(); NotUpdated.Clear();
            foreach (var u in updates) Updates.Enqueue(u);
            _updating = true;
            NextUpdate();
        }

        private static void NextUpdate()
        {
            while (Updates.Count > 0 && !Busy)
            {
                var u = Updates.Dequeue();
                Download(u.Key, u.Value);
                if (!Busy) NotUpdated.Add(u.Key.Name + " (" + Message + ")");
            }
            if (Busy) return;
            _updating = false;
            Message = (Updated.Count > 0 ? "Updated " + string.Join(", ", Updated) + ". Restart the game to use " + (Updated.Count == 1 ? "it" : "them") + "." : "") +
                (NotUpdated.Count > 0 ? (Updated.Count > 0 ? " " : "") + "Not updated: " + string.Join("; ", NotUpdated) + "." : "");
        }

        internal static void Download(Mod m, Release v)
        {
            if (Busy) return;
            string reason = Reason(m, v);
            if (reason != null) { Message = reason; return; }
            Start(v.Url, (int)Math.Min(v.Size, ModTransfer.MaxBytes), m, v);
        }

        private static void Start(string url, int limit, Mod m, Release v)
        {
            Message = ""; _mod = m; _release = v;
            try
            {
                _download = new LimitedDownload(limit);
                _request = new UnityWebRequest(url, "GET", _download, null) { timeout = 45, redirectLimit = 8 };
                _request.SendWebRequest();
            }
            catch (Exception e) { Message = "Download could not start: " + e.Message; Dispose(); }
        }

        internal static void Cancel()
        {
            if (!Busy) return;
            _request.Abort(); Dispose(); Message = "Download cancelled.";
            if (_updating) { _updating = false; Updates.Clear(); if (Updated.Count > 0) Message += " Updated " + string.Join(", ", Updated) + ": restart the game to use them."; }
        }

        internal static void Tick()
        {
            if (!Busy) return;
            if (!ModsScreen.IsVisible && !_background) { ModsScreen.Close(); return; }
            if (_download.Exceeded) _request.Abort();
            if (!_request.isDone) return;
            try
            {
                if (_download.Exceeded) throw new InvalidDataException("Download exceeds the size limit.");
                if (_request.result != UnityWebRequest.Result.Success) throw new IOException(_request.error ?? "Request failed");
                var bytes = _download.Bytes;
                if (_mod == null) { Current = Parse(bytes); Message = Current.Mods.Count == 0 ? "No mods in this catalog." : "Select a mod for details."; }
                else
                {
                    var dll = Verify(_mod, _release, bytes, ModTransfer.Approved(), ModTransfer.MaxBytes, MpApi.ValidId);
                    string error = ModTransfer.Install(_mod.Id, _mod.Name, _release.Version, _release.Hash, dll);
                    // Install can report that the DLL was saved but an older copy could not be moved.
                    RestartRequired |= ModTransfer.ManagedFiles(_mod.Id).Count > 0;
                    Message = error ?? "Installed " + _mod.Name + " " + _release.Version + ".";
                    if (_updating) { if (error == null) Updated.Add(_mod.Name + " " + _release.Version); else NotUpdated.Add(_mod.Name + " (" + error + ")"); }
                }
            }
            catch (Exception e)
            {
                if (_mod == null && _background) { Plugin.Log.LogInfo("Mod update check: catalog unavailable: " + e.Message); Message = ""; }
                else Message = (_mod == null ? "Offline or catalog unavailable: " : "Mod refused: ") + e.Message;
                if (_mod != null && _updating) NotUpdated.Add(_mod.Name + " (" + e.Message + ")");
            }
            finally { Dispose(); }
            if (_updating) NextUpdate();
        }

        private static void Dispose()
        {
            _request?.Dispose(); _request = null; _download = null; _mod = null; _release = null; _background = false;
        }

        // Refuse a large Content-Length before buffering, and bound every chunk (including chunked responses).
        private sealed class LimitedDownload : DownloadHandlerScript
        {
            private readonly LimitedBuffer _data;
            internal bool Exceeded => _data.Exceeded;
            internal long Length => _data.Length;
            internal byte[] Bytes => _data.Bytes;
            internal LimitedDownload(int limit) : base(new byte[8192]) { _data = new LimitedBuffer(limit); }
            protected override void ReceiveContentLengthHeader(ulong length) => _data.ContentLength(length);
            protected override bool ReceiveData(byte[] data, int length) => _data.Add(data, length);
            public override void Dispose() { _data.Dispose(); base.Dispose(); }
        }
    }
}
