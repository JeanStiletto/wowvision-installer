using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WowVisionInstaller
{
    /// <summary>
    /// The newest release file of a CurseForge project, for the extra addons that
    /// publish nowhere else (GTFO, Details, BugGrabber, AtlasLoot Continued).
    /// GTFO above all must be current: an outdated one misses the newest boss
    /// mechanics, and a blind player has no other warning of standing in them.
    ///
    /// Read from the file listing the curseforge.com website itself uses
    /// (/api/v1/mods/{project}/files): no API key, one small request per addon.
    /// It is NOT CurseForge's documented API and may change or be blocked, so
    /// every failure leaves the package on its baked-in pin, which still installs.
    ///
    /// The zip comes from the CurseForge file server by its id and name,
    /// edge.forgecdn.net/files/{id / 1000}/{id % 1000}/{name} with no padding
    /// (file 8619054 is files/8619/54/), the host the pins use as well. The
    /// listing carries no checksum; the download is held to the listed size
    /// instead, on top of the zip-shape checks every extra addon gets.
    /// </summary>
    public static class CurseForge
    {
        /// <summary>One file of a project's listing.</summary>
        public class CurseFile
        {
            public long Id;
            public string FileName;
            public long Length;
            /// <summary>The version label: group 1 of the package's file-name pattern.</summary>
            public string Version;
        }

        /// <summary>What one look at a project's listing gave.</summary>
        public class CurseLookup
        {
            /// <summary>The newest release file under a name we know, or null.</summary>
            public CurseFile File;

            /// <summary>
            /// True when a listing came back. False: the source did not answer
            /// (offline, blocked, an error page). Answered with no File means
            /// the project has no release under a name we know. It renamed its
            /// zip, which is for us to look at (the log says so), not something
            /// a player can act on: AtlasLoot did this on 2026-11-17.
            /// </summary>
            public bool Answered;
        }

        public static string ListingUrl(int projectId) =>
            $"https://www.curseforge.com/api/v1/mods/{projectId}/files" +
            "?pageIndex=0&pageSize=20&sort=dateCreated&sortDescending=true&removeAlphas=true";

        public static string DownloadUrl(long fileId, string fileName) =>
            $"https://edge.forgecdn.net/files/{fileId / 1000}/{fileId % 1000}/{Uri.EscapeDataString(fileName)}";

        /// <summary>
        /// The newest file of a listing that is a full release (no alpha, beta or
        /// early access) and whose name matches <paramref name="fileNamePattern"/>,
        /// or null. A file under another name is skipped rather than trusted: when
        /// an addon renamed its zip before (AtlasLoot), its folders changed too,
        /// which needs a look at the package before it is installed. Newest = the
        /// highest file id, so the listing's order does not matter.
        /// </summary>
        public static CurseFile PickLatest(string json, string fileNamePattern)
        {
            if (string.IsNullOrEmpty(fileNamePattern)) return null;
            var data = ListingData(json);
            if (data == null) return null;

            var pattern = new Regex(fileNamePattern, RegexOptions.CultureInvariant);
            CurseFile best = null;
            foreach (var item in data)
            {
                if (!(item is IDictionary<string, object> f)) continue;
                long id = AsLong(f, "id");
                long length = AsLong(f, "fileLength");
                string name = f.TryGetValue("fileName", out var n) ? n as string : null;
                if (id <= 0 || length <= 0 || string.IsNullOrEmpty(name)) continue;
                if (AsLong(f, "releaseType") != 1) continue;   // 1 release, 2 beta, 3 alpha
                if (f.TryGetValue("isEarlyAccessContent", out var ea) && ea is bool early && early) continue;
                var m = pattern.Match(name);
                if (!m.Success || m.Groups.Count < 2 || m.Groups[1].Value.Length == 0) continue;
                if (best == null || id > best.Id)
                    best = new CurseFile { Id = id, FileName = name, Length = length, Version = m.Groups[1].Value };
            }
            return best;
        }

        /// <summary>The files of a listing, or null when the text is not a listing (nothing, an error page).</summary>
        private static IEnumerable ListingData(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            object parsed;
            try { parsed = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(json); }
            catch (Exception ex)
            {
                Logger.Warning($"CurseForge listing is not JSON: {ex.Message}");
                return null;
            }
            if (!(parsed is IDictionary<string, object> root) ||
                !root.TryGetValue("data", out var dataObj) || !(dataObj is IEnumerable data))
                return null;
            return data;
        }

        /// <summary>What a listing's text says: whether it is a listing, and its newest release file.</summary>
        public static CurseLookup Read(string json, string fileNamePattern) =>
            new CurseLookup { Answered = ListingData(json) != null, File = PickLatest(json, fileNamePattern) };

        /// <summary>One look at a project's listing online (a missing file is logged).</summary>
        public static async Task<CurseLookup> LookUpLatestAsync(GitHubClient http, int projectId,
                                                                string fileNamePattern, TimeSpan timeout)
        {
            string json = await http.DownloadTextAsync(ListingUrl(projectId), timeout);
            var look = Read(json, fileNamePattern);
            if (look.File == null && look.Answered)
                Logger.Warning($"CurseForge project {projectId}: no release file named like {fileNamePattern}. " +
                               "The project may have renamed its zip; the built-in version stays in use.");
            return look;
        }

        /// <summary>The newest release file of a project online, or null (logged).</summary>
        public static async Task<CurseFile> ResolveLatestAsync(GitHubClient http, int projectId,
                                                               string fileNamePattern, TimeSpan timeout) =>
            (await LookUpLatestAsync(http, projectId, fileNamePattern, timeout)).File;

        private static long AsLong(IDictionary<string, object> f, string key)
        {
            if (!f.TryGetValue(key, out var v) || v == null) return 0;
            try { return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
    }
}
