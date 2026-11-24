using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WowVisionInstaller
{
    /// <summary>
    /// One downloadable zip inside a managed entry. A github package tracks the
    /// project's own /releases/latest; a CurseForge package (addons without
    /// GitHub releases) tracks the project's newest release file there
    /// (<see cref="CurseForge"/>). Either falls back to the baked-in
    /// (version, url, sha256) pin when its source cannot be read, the same way
    /// the main addon's build-time pin does.
    /// </summary>
    public class ManagedPackage
    {
        /// <summary>Manifest key, e.g. "CurseQuestie". Same key as the macOS installer.</summary>
        public string Key;

        /// <summary>
        /// WoW product tokens this package is available for. Most zips are
        /// multi-flavor (one download serves several clients); the exceptions are
        /// per-flavor release assets like Pawn's -BurningCrusade/-Classic/-Forever/
        /// -Pandaria zips and DBM's per-era raid packages. Every list was checked
        /// against the real zips (2026-10-19): a client is listed only when the zip
        /// carries a TOC that client loads. A zip that drops a client later is
        /// still caught at install time (<see cref="WowLocator.FolderServesClient"/>).
        /// Null = every supported client.
        /// </summary>
        public string[] Products;

        /// <summary>Top-level zip folders that must exist and land in AddOns.</summary>
        public string[] RequiredRoots;

        public bool AppliesTo(string product) =>
            Products == null || Array.IndexOf(Products, product) >= 0;

        /// <summary>
        /// Additional top-level folders are accepted and installed when they start
        /// with this prefix (e.g. "DBM-" — a new DBM sub-folder in a future release
        /// must not fail validation). Null = only RequiredRoots are allowed.
        /// </summary>
        public string RootPrefix;

        /// <summary>"owner/repo" whose /releases/latest is tracked, or null.</summary>
        public string GitHubRepo;

        /// <summary>CurseForge project id whose newest release file is tracked, or 0.</summary>
        public int CurseProjectId;

        /// <summary>
        /// Regex the CurseForge file name must match, group 1 = the version label
        /// (e.g. "^GTFO-(\d[\d.]*)\.zip$" gives "6.12.2"; digits
        /// and dots only, so a renamed or alpha-labelled zip never passes). The pin's label is written
        /// the same way, so a live file equal to the pin counts as the same version.
        /// </summary>
        public string CurseFilePattern;

        /// <summary>
        /// Asset filename with the release tag substituted, e.g. "BugSack-{tag}.zip".
        /// "{name}" is the tag without <see cref="TagPrefix"/>.
        /// </summary>
        public string AssetTemplate;

        /// <summary>
        /// Set for a repo whose release tags carry a path prefix, e.g. Questie's
        /// "bundle/v12.1.0+v1.0.6" (asset "Questie-v12.1.0+v1.0.6.zip"). Only such
        /// a package accepts tags with "/" and "+"; null = plain tags.
        /// </summary>
        public string TagPrefix;

        /// <summary>
        /// Folders of an older package of the same addon that clash with this
        /// one (both claim the same globals). Removed by exact name once this
        /// package is installed and current; symlinks are never touched.
        /// </summary>
        public string[] ObsoleteRoots;

        /// <summary>Pinned/fallback version label, download URL, and its SHA-256.</summary>
        public string PinVersion;
        public string PinUrl;
        public string PinSha256;

        /// <summary>
        /// Top-level folders of the zip that are known and deliberately NOT
        /// installed. They are no reason to refuse the package and they are
        /// never copied. A copy the player put into AddOns themselves is left
        /// alone.
        /// </summary>
        public string[] SkipRoots = new string[0];
    }

    /// <summary>One checkbox on the components page: a named bundle of packages.</summary>
    public class ManagedEntry
    {
        /// <summary>Preference key, e.g. "ManageQuestie". Same key as the macOS installer.</summary>
        public string PrefKey;
        public string DisplayName;
        public bool DefaultEnabled = true;
        public ManagedPackage[] Packages;
    }

    /// <summary>Where one package's download resolved to for this run.</summary>
    public class ResolvedManagedPackage
    {
        public ManagedPackage Package;
        public string Version;   // recorded in the manifest; release tag for github packages
        public string Url;
        public string Sha256;    // null when the source is live (github release or CurseForge file)
        public long ExpectedLength;   // the size CurseForge lists for a live file; 0 = not checked

        /// <summary>
        /// "CurseForge" or "GitHub" when this package follows that source but the
        /// lookup of its newest release failed this run (blocked, offline, too
        /// slow), so the baked-in pin is used instead. Null when the lookup worked
        /// or the package follows no source.
        /// </summary>
        public string LookupFailedSource;
    }

    /// <summary>
    /// The well-known extra addons the installer can install and keep current
    /// beside WowVision — the Sku installer's list (5.5), taken over unchanged and
    /// opened up to Mists Classic and Retail where the addon exists there. Most
    /// zips are multi-flavor and serve several clients from one download; each
    /// package names the clients it is available for (see
    /// <see cref="ManagedPackage.Products"/>). GitHub is used for every addon that
    /// publishes releases there; the addons without GitHub releases (AtlasLoot,
    /// Details, GTFO, BugGrabber) follow their newest CurseForge release file.
    ///
    /// Availability, checked against the real zips on 2026-10-19:
    ///   Questie: every client but Retail. AtlasLoot Continued: Forever, TBC, Era.
    ///   Details, DBM core and dungeons, GTFO, BugSack + BugGrabber: all five.
    ///   Pawn and DBM raids: one package per client (Mists = Pandaria / DBM-MoP,
    ///   Retail = the plain Pawn zip; Retail's raids ship in the DBM core zip).
    /// </summary>
    public static class ManagedAddons
    {
        private static readonly string[] AllButRetail =
            { Config.ForeverFlavor, Config.AnniversaryFlavor, Config.EraFlavor, Config.MistsFlavor };
        private static readonly string[] ClassicTrio =
            { Config.ForeverFlavor, Config.AnniversaryFlavor, Config.EraFlavor };

        public static readonly List<ManagedEntry> Entries = new List<ManagedEntry>
        {
            new ManagedEntry
            {
                PrefKey = "ManageQuestie", DisplayName = "Questie",
                Packages = new[]
                {
                    new ManagedPackage
                    {
                        // Questie 12 (2026-09-23) is one multi-flavor bundle for
                        // every client, WoW Forever included: tags read
                        // "bundle/v12.x+v1.y", the asset "Questie-v12.x+v1.y.zip",
                        // and the quest data moved to a second required folder,
                        // QuestieDB. Until installer 5.5 the plain-tag pattern
                        // rejected those tags and kept every client on 11.37.1.
                        //
                        // The bundle of 2026-11 (seen in v12.13.0+v1.1.5) carries a
                        // third addon, QuestieTrace: "Data collection addon for
                        // Questie". It records gameplay data on the player's
                        // machine after asking once, in a dialog of its own that
                        // is not one of the game's popups. Questie lists it as an
                        // optional dependency and runs without it. It is left out:
                        // nobody chose it by ticking Questie, and its question
                        // would meet a blind player as a dialog nothing reads.
                        // Before this the unknown folder made the installer refuse
                        // the whole bundle, so Questie was not installed or
                        // updated at all.
                        Key = "CurseQuestie", RequiredRoots = new[] { "Questie", "QuestieDB" },
                        SkipRoots = new[] { "QuestieTrace" },
                        Products = AllButRetail,   // Questie has no retail build
                        GitHubRepo = "Questie/Questie", TagPrefix = "bundle/", AssetTemplate = "Questie-{name}.zip",
                        PinVersion = "bundle/v12.13.0+v1.1.5",
                        PinUrl = "https://github.com/Questie/Questie/releases/download/bundle%2Fv12.13.0%2Bv1.1.5/Questie-v12.13.0%2Bv1.1.5.zip",
                        PinSha256 = "bd47e4ade92cb352d3cbcf1267cc85343c01a2bc6bcc495f0fa992633a32d995",
                    },
                },
            },
            new ManagedEntry
            {
                // The AtlasLoot CurseForge project (1270310) left the
                // AtlasLootClassic line on 2026-09-17: every file since is
                // "AtlasLootClassic-Continued", one zip with TBC, Vanilla and
                // Camelot TOCs for all three clients. Its folders were renamed on
                // the way (AtlasLoot_* for a few days, AtlasLootContinued_* now),
                // and any older set left beside it collides (both claim the global
                // AtlasLoot), so both older sets are removed after the install.
                // No Mists or Retail TOCs in it.
                PrefKey = "ManageAtlasLoot", DisplayName = "AtlasLoot Continued",
                Packages = new[]
                {
                    new ManagedPackage
                    {
                        Key = "CurseAtlasLootContinued", Products = ClassicTrio,
                        RequiredRoots = new[] { "AtlasLootContinued", "AtlasLootContinued_Data" },
                        RootPrefix = "AtlasLootContinued",
                        ObsoleteRoots = new[]
                        {
                            "AtlasLootClassic",
                            "AtlasLootClassic_BiS",
                            "AtlasLootClassic_Collections",
                            "AtlasLootClassic_Crafting",
                            "AtlasLootClassic_Data",
                            "AtlasLootClassic_DungeonsAndRaids",
                            "AtlasLootClassic_Factions",
                            "AtlasLootClassic_Options",
                            "AtlasLootClassic_PvP",
                            "AtlasLoot",
                            "AtlasLoot_BiS",
                            "AtlasLoot_Collections",
                            "AtlasLoot_Crafting",
                            "AtlasLoot_Data",
                            "AtlasLoot_DungeonsAndRaids",
                            "AtlasLoot_Factions",
                            "AtlasLoot_Favourites",
                            "AtlasLoot_Options",
                            "AtlasLoot_PvP",
                        },
                        CurseProjectId = 1270310,
                        // Since 2026-11-17 each release is published three times,
                        // as AtlasLootContinued-ERA_N, -Anniversary_N and
                        // -Forever_N with a number of its own. Compared on
                        // 2026-11-23 (12991, 12992, 12993): the same 495 files,
                        // differing only in the Version line of the TOCs, the
                        // same folders as before plus AtlasLootContinued_Upgrades.
                        // So one package still serves all three clients, and the
                        // newest of the three files is taken. The older name stays
                        // accepted.
                        CurseFilePattern = @"^AtlasLoot(?:Classic-Continued|Continued-(?:ERA|Anniversary|Forever))_(\d+)\.zip$",
                        PinVersion = "12997",
                        PinUrl = "https://edge.forgecdn.net/files/9785/961/AtlasLootContinued-Forever_12997.zip",
                        PinSha256 = "e01132b595cd0d9f37cb67ef4da594821590f7823403940cc594d00f1133e1cc",
                    },
                },
            },
            new ManagedEntry
            {
                // Details releases are multi-flavor since mid-2026 (one zip carries
                // Details_TBC, _Classic, _Camelot, _Mists and the retail TOC), so
                // one pin serves all five clients.
                PrefKey = "ManageDetails", DisplayName = "Details Damage Meter",
                Packages = new[]
                {
                    new ManagedPackage
                    {
                        Key = "CurseDetails",
                        RequiredRoots = new[] { "Details", "Details_DataStorage" },
                        RootPrefix = "Details",
                        CurseProjectId = 61284,
                        CurseFilePattern = @"^Details-Details\.(\d[\d.]*)\.zip$",
                        PinVersion = "20261109.15410.172",
                        PinUrl = "https://edge.forgecdn.net/files/9521/145/Details-Details.20261109.15410.172.zip",
                        PinSha256 = "5002bdd5c3e92dd59bffa3ebdfc867f980b09ed629c51dab056fb5442f92ad41",
                    },
                },
            },
            new ManagedEntry
            {
                // Off by default: gear-weighting advice is a personal taste, not a
                // baseline accessibility need like the others. Pawn publishes one
                // release asset PER flavor under a "Pawn-x.y.z" tag, hence the
                // "{tag}-<flavor>.zip" templates and one package per client. The
                // plain "{tag}.zip" is the retail build.
                PrefKey = "ManagePawn", DisplayName = "Pawn", DefaultEnabled = false,
                Packages = new[]
                {
                    new ManagedPackage
                    {
                        Key = "CursePawnTBC", RequiredRoots = new[] { "Pawn" },
                        Products = new[] { Config.AnniversaryFlavor },
                        GitHubRepo = "VgerMods/Pawn", AssetTemplate = "{tag}-BurningCrusade.zip",
                        PinVersion = "Pawn-2.14.4",
                        PinUrl = "https://github.com/VgerMods/Pawn/releases/download/Pawn-2.14.4/Pawn-2.14.4-BurningCrusade.zip",
                        PinSha256 = "a637fc06eeb970bf62e975e5ff9dd8e95139a9f24fd6a612d73f202628957b49",
                    },
                    new ManagedPackage
                    {
                        Key = "CursePawnVanilla", RequiredRoots = new[] { "Pawn" },
                        Products = new[] { Config.EraFlavor },
                        GitHubRepo = "VgerMods/Pawn", AssetTemplate = "{tag}-Classic.zip",
                        PinVersion = "Pawn-2.14.4",
                        PinUrl = "https://github.com/VgerMods/Pawn/releases/download/Pawn-2.14.4/Pawn-2.14.4-Classic.zip",
                        PinSha256 = "e1cb0ce7157e8ff48aae07afc1dd36c6327d081c0e31e575b4ad7ee2013250d5",
                    },
                    new ManagedPackage
                    {
                        // First published with 2.13.17. Not the plain Pawn zip:
                        // that one is the retail build.
                        Key = "CursePawnForever", RequiredRoots = new[] { "Pawn" },
                        Products = new[] { Config.ForeverFlavor },
                        GitHubRepo = "VgerMods/Pawn", AssetTemplate = "{tag}-Forever.zip",
                        PinVersion = "Pawn-2.14.4",
                        PinUrl = "https://github.com/VgerMods/Pawn/releases/download/Pawn-2.14.4/Pawn-2.14.4-Forever.zip",
                        PinSha256 = "9e043fe917de294f32b57f809e9c6dfb7bf6985f9f878f3ffbff27f984e5ee32",
                    },
                    new ManagedPackage
                    {
                        Key = "CursePawnMists", RequiredRoots = new[] { "Pawn" },
                        Products = new[] { Config.MistsFlavor },
                        GitHubRepo = "VgerMods/Pawn", AssetTemplate = "{tag}-Pandaria.zip",
                        PinVersion = "Pawn-2.14.4",
                        PinUrl = "https://github.com/VgerMods/Pawn/releases/download/Pawn-2.14.4/Pawn-2.14.4-Pandaria.zip",
                        PinSha256 = "df92388a0c6513c39512605611be47f39ceacc9e84f63c0eb25829be60125853",
                    },
                    new ManagedPackage
                    {
                        Key = "CursePawnRetail", RequiredRoots = new[] { "Pawn" },
                        Products = new[] { Config.RetailFlavor },
                        GitHubRepo = "VgerMods/Pawn", AssetTemplate = "{tag}.zip",
                        PinVersion = "Pawn-2.14.4",
                        PinUrl = "https://github.com/VgerMods/Pawn/releases/download/Pawn-2.14.4/Pawn-2.14.4.zip",
                        PinSha256 = "bfb924cde412b718204b82298ffe618b793779741e43ba78df8bcef1d6e84e2e",
                    },
                },
            },
            new ManagedEntry
            {
                // ONE entry, several release packages: core alone has no TBC
                // bosses. Core and dungeons serve every client; the raid mods come
                // per era (Retail's current raids are inside the core zip).
                PrefKey = "ManageDBM", DisplayName = "Deadly Boss Mods",
                Packages = new[]
                {
                    new ManagedPackage
                    {
                        Key = "DBMCore",
                        RequiredRoots = new[] { "DBM-Core", "DBM-GUI", "DBM-StatusBarTimers" },
                        RootPrefix = "DBM-",
                        GitHubRepo = "DeadlyBossMods/DeadlyBossMods", AssetTemplate = "DBM-Core-{tag}.zip",
                        PinVersion = "12.1.23",
                        PinUrl = "https://github.com/DeadlyBossMods/DeadlyBossMods/releases/download/12.1.23/DBM-Core-12.1.23.zip",
                        PinSha256 = "7b78bad217c9067bcc962c107758ad4ee3972a8654e17fccae8041a436503326",
                    },
                    new ManagedPackage
                    {
                        // TBC raid mods; the release declares no Era flavor.
                        Key = "DBMRaidsBC", RequiredRoots = new[] { "DBM-Raids-BC" }, RootPrefix = "DBM-",
                        Products = new[] { Config.AnniversaryFlavor },
                        GitHubRepo = "DeadlyBossMods/DBM-BurningCrusade", AssetTemplate = "DBM-Raids-BC-{tag}.zip",
                        PinVersion = "r22",
                        PinUrl = "https://github.com/DeadlyBossMods/DBM-BurningCrusade/releases/download/r22/DBM-Raids-BC-r22.zip",
                        PinSha256 = "70827fde1bd5ab6f63e7635590c52ae2866052ad9aff5f1095b8bb9c5a8979d3",
                    },
                    new ManagedPackage
                    {
                        // Vanilla/SoD raid mods - the Era counterpart of Raids-BC.
                        // WoW Forever's raids are the vanilla ones (its TOC lists 16001).
                        Key = "DBMRaidsVanilla", RequiredRoots = new[] { "DBM-Raids-Vanilla" }, RootPrefix = "DBM-",
                        Products = new[] { Config.EraFlavor, Config.ForeverFlavor },
                        GitHubRepo = "DeadlyBossMods/DBM-Vanilla", AssetTemplate = "DBM-Vanilla_SoD-{tag}.zip",
                        PinVersion = "r835",
                        PinUrl = "https://github.com/DeadlyBossMods/DBM-Vanilla/releases/download/r835/DBM-Vanilla_SoD-r835.zip",
                        PinSha256 = "a20cbdd7f7ebfbad09bb549750deb8d5fe7fe826ac49b3c60318238febb38358",
                    },
                    new ManagedPackage
                    {
                        // Mists raid mods (DBM-Raids-MoP plus DBM-Pandaria and
                        // DBM-Scenario-MoP in the same zip).
                        Key = "DBMRaidsMoP", RequiredRoots = new[] { "DBM-Raids-MoP" }, RootPrefix = "DBM-",
                        Products = new[] { Config.MistsFlavor },
                        GitHubRepo = "DeadlyBossMods/DBM-MoP", AssetTemplate = "DBM-Raids-MoP-{tag}.zip",
                        PinVersion = "r192",
                        PinUrl = "https://github.com/DeadlyBossMods/DBM-MoP/releases/download/r192/DBM-Raids-MoP-r192.zip",
                        PinSha256 = "954db157e6b96a3e18fc8b7873ce2d4acf8e75b1cf1cbd799dba37daaa5128bd",
                    },
                    new ManagedPackage
                    {
                        // One zip carries the dungeon folders of every era.
                        Key = "DBMDungeons", RequiredRoots = new[] { "DBM-Party-BC", "DBM-Party-Vanilla" }, RootPrefix = "DBM-",
                        GitHubRepo = "DeadlyBossMods/DBM-Dungeons", AssetTemplate = "DBM-Dungeons-{tag}.zip",
                        PinVersion = "r272",
                        PinUrl = "https://github.com/DeadlyBossMods/DBM-Dungeons/releases/download/r272/DBM-Dungeons-r272.zip",
                        PinSha256 = "a9470ebd94b01ccacea2fd0c3354425bc819c941746ea20550c574003bb17bd3",
                    },
                },
            },
            new ManagedEntry
            {
                PrefKey = "ManageGTFO", DisplayName = "GTFO",
                Packages = new[]
                {
                    new ManagedPackage
                    {
                        Key = "CurseGTFO", RequiredRoots = new[] { "GTFO" },
                        // Live from CurseForge: GTFO's spell lists follow each
                        // new boss, and it is a blind player's warning of standing
                        // in a mechanic. 6.11+ ships GTFO_Camelot.toc; older pins
                        // would load the retail GTFO_Mainline.toc on WoW Forever.
                        CurseProjectId = 20697,
                        CurseFilePattern = @"^GTFO-(\d[\d.]*)\.zip$",
                        PinVersion = "6.12.4",
                        PinUrl = "https://edge.forgecdn.net/files/9695/357/GTFO-6.12.4.zip",
                        PinSha256 = "d23b6097944d3dfa5d6958df5a3c6182e2c628657b19654c50278baaf504c14e",
                    },
                },
            },
            new ManagedEntry
            {
                // The pair is one decision: BugSack without BugGrabber does nothing.
                PrefKey = "ManageBugSack", DisplayName = "BugSack + BugGrabber",
                Packages = new[]
                {
                    new ManagedPackage
                    {
                        Key = "BugSack", RequiredRoots = new[] { "BugSack" },
                        GitHubRepo = "funkydude/BugSack", AssetTemplate = "BugSack-{tag}.zip",
                        PinVersion = "v12.1.9",
                        PinUrl = "https://github.com/funkydude/BugSack/releases/download/v12.1.9/BugSack-v12.1.9.zip",
                        PinSha256 = "2b3a96d352b5fc351dfc62628ea1c75fe86c837cc59967455e1ac482a1b92ac1",
                    },
                    new ManagedPackage
                    {
                        Key = "CurseBugGrabber", RequiredRoots = new[] { "!BugGrabber" },
                        CurseProjectId = 13344,
                        CurseFilePattern = @"^!BugGrabber-v(\d[\d.]*)\.zip$",
                        PinVersion = "12.1.1",
                        PinUrl = "https://edge.forgecdn.net/files/9277/326/%21BugGrabber-v12.1.1.zip",
                        PinSha256 = "1e53e65c72f525a45c8951abe06415df5b1fb52ec21e30970c1416a2fbd4a250",
                    },
                },
            },
        };

        // ── Resolution ─────────────────────────────────────────────────────────

        private static readonly Dictionary<string, ResolvedManagedPackage> _resolved =
            new Dictionary<string, ResolvedManagedPackage>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Resolve every github- or CurseForge-tracked package to its current
        /// release, in parallel, bounded by <paramref name="timeout"/>. Whatever
        /// does not resolve in time stays on its pin — same graceful degradation
        /// as the main addon's version redirect. Called once at startup.
        /// </summary>
        public static void ResolveAll(GitHubClient github, TimeSpan timeout)
        {
            SeedPins();

            var tasks = new List<Task>();
            foreach (var entry in Entries)
            {
                foreach (var pkg in entry.Packages)
                {
                    var p = pkg;
                    if (p.CurseProjectId > 0)
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            var look = await CurseForge.LookUpLatestAsync(github, p.CurseProjectId,
                                                                          p.CurseFilePattern, timeout);
                            ApplyCurseLookup(p, look);
                            if (look.File != null)
                                Logger.Info($"{p.Key}: newest CurseForge file {look.File.FileName} (project {p.CurseProjectId}).");
                        }));
                        continue;
                    }
                    if (string.IsNullOrEmpty(p.GitHubRepo)) continue;
                    tasks.Add(Task.Run(async () =>
                    {
                        string tag = await github.ResolveLatestTagAsync(p.GitHubRepo, p.TagPrefix);
                        if (string.IsNullOrEmpty(tag)) return;
                        string name = p.TagPrefix != null && tag.StartsWith(p.TagPrefix, StringComparison.Ordinal)
                            ? tag.Substring(p.TagPrefix.Length) : tag;
                        string asset = p.AssetTemplate.Replace("{tag}", tag).Replace("{name}", name);
                        var r = new ResolvedManagedPackage
                        {
                            Package = p, Version = tag, Sha256 = null,
                            Url = $"https://github.com/{p.GitHubRepo}/releases/download/" +
                                  $"{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset)}",
                        };
                        SetResolved(r);
                        Logger.Info($"{p.Key}: latest release {tag} on {p.GitHubRepo}.");
                    }));
                }
            }

            try { Task.WaitAll(tasks.ToArray(), timeout); }
            catch (Exception ex) { Logger.Warning($"Managed addon resolution: {ex.Message}"); }

            lock (_resolved)
                foreach (var r in _resolved.Values)
                    if (r.LookupFailedSource != null)
                        Logger.Warning($"{r.Package.Key}: newest release not found on {r.LookupFailedSource}; " +
                                       $"using the built-in version {r.Version}.");
        }

        /// <summary>
        /// Every package on its pin, each one with a live source marked as not
        /// looked up yet; a lookup that succeeds replaces its package's entry.
        /// </summary>
        internal static void SeedPins()
        {
            lock (_resolved)
                foreach (var entry in Entries)
                    foreach (var pkg in entry.Packages)
                        _resolved[pkg.Key] = new ResolvedManagedPackage
                        {
                            Package = pkg, Version = pkg.PinVersion, Url = pkg.PinUrl, Sha256 = pkg.PinSha256,
                            LookupFailedSource = LiveSource(pkg),
                        };
        }

        /// <summary>
        /// Records what CurseForge said about one package. No answer leaves the
        /// package marked as a failed lookup (the player is told). An answer
        /// without a file under a name we know keeps the pin WITHOUT that mark:
        /// the source did answer, and telling every player on every run about a
        /// renamed zip helps nobody. The log has it.
        /// </summary>
        internal static void ApplyCurseLookup(ManagedPackage p, CurseForge.CurseLookup look)
        {
            if (look == null || !look.Answered) return;
            var f = look.File;
            if (f == null)
            {
                SetResolved(new ResolvedManagedPackage
                {
                    Package = p, Version = p.PinVersion, Url = p.PinUrl, Sha256 = p.PinSha256,
                });
                return;
            }
            // The newest file IS the pin: keep the pin's checksum.
            bool isPin = string.Equals(f.Version, p.PinVersion, StringComparison.OrdinalIgnoreCase);
            SetResolved(new ResolvedManagedPackage
            {
                Package = p, Version = f.Version, ExpectedLength = f.Length,
                Url = isPin ? p.PinUrl : CurseForge.DownloadUrl(f.Id, f.FileName),
                Sha256 = isPin ? p.PinSha256 : null,
            });
        }

        /// <summary>Records where one package resolved to this run.</summary>
        internal static void SetResolved(ResolvedManagedPackage r)
        {
            lock (_resolved) _resolved[r.Package.Key] = r;
        }

        /// <summary>Where a package looks up its newest release, or null for a pin-only package.</summary>
        internal static string LiveSource(ManagedPackage pkg) =>
            pkg.CurseProjectId > 0 ? "CurseForge" :
            !string.IsNullOrEmpty(pkg.GitHubRepo) ? "GitHub" : null;

        /// <summary>
        /// One sentence for the player when the newest release of a wanted extra
        /// addon could not be looked up and its built-in version is used instead,
        /// e.g. "Could not check online for newer versions of: Details (built-in
        /// 20261109.15410.172). CurseForge did not answer. …"; null when every
        /// lookup worked. Only entries that are switched on and exist for one of
        /// <paramref name="products"/> count. Said whether or not anything is
        /// installed this run: a player kept on an old pin hears it every time.
        /// </summary>
        public static string LookupFailureNotice(IDictionary<string, bool> enabled, IEnumerable<string> products)
        {
            var productList = products.Distinct().ToList();
            var names = new List<string>();
            var sources = new List<string>();
            foreach (var entry in Entries)
            {
                if (!enabled.TryGetValue(entry.PrefKey, out bool on) || !on) continue;
                var failed = productList.SelectMany(p => PackagesFor(entry, p)).Distinct()
                                        .Select(Resolved).Where(r => r.LookupFailedSource != null).ToList();
                if (failed.Count == 0) continue;
                names.Add($"{entry.DisplayName} ({Loc.Format("managed.builtIn", DisplayVersion(failed[0].Version))})");
                foreach (var r in failed)
                    if (!sources.Contains(r.LookupFailedSource)) sources.Add(r.LookupFailedSource);
            }
            if (names.Count == 0) return null;
            return Loc.Format("managed.lookupFailed", string.Join(", ", names), string.Join(", ", sources));
        }

        public static ResolvedManagedPackage Resolved(ManagedPackage pkg)
        {
            lock (_resolved)
            {
                return _resolved.TryGetValue(pkg.Key, out var r) ? r : new ResolvedManagedPackage
                {
                    Package = pkg, Version = pkg.PinVersion, Url = pkg.PinUrl, Sha256 = pkg.PinSha256,
                };
            }
        }

        // ── State queries ──────────────────────────────────────────────────────

        /// <summary>
        /// A version/tag as shown to the user: leading non-digits stripped, the
        /// same rule the macOS installer applies ("v11.37.1" → "11.37.1",
        /// "Pawn-2.13.15" → "2.13.15", "r261" → "261").
        /// </summary>
        public static string DisplayVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return "";
            int i = 0;
            while (i < version.Length && !char.IsDigit(version[i])) i++;
            return version.Substring(i);
        }

        /// <summary>Every supported client takes extra addons (each gets what exists for it).</summary>
        public static bool AppliesTo(InstallTarget target) =>
            target != null && target.ClientFound && WowLocator.IsSupportedProduct(target.Product);

        /// <summary>The entry's packages that apply to one client.</summary>
        public static IEnumerable<ManagedPackage> PackagesFor(ManagedEntry entry, string product) =>
            entry.Packages.Where(p => p.AppliesTo(product));

        /// <summary>
        /// True for a manifest key that belongs to an extra-addon package
        /// ("CurseQuestie", "BugSack", …) rather than to one of WowVision's folders.
        /// </summary>
        public static bool IsManagedKey(string key) =>
            Entries.Any(e => e.Packages.Any(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)));

        /// <summary>True when the entry has anything at all for this client.</summary>
        public static bool AvailableFor(ManagedEntry entry, string product) =>
            PackagesFor(entry, product).Any();

        /// <summary>
        /// Manifest value recorded when a package's zip turned out to carry no TOC
        /// for the client (a release that dropped it). Keeps the next run from
        /// downloading the same zip again; a newer release is tried afresh.
        ///
        /// The "@2" is the rule's generation. Markers written before installer
        /// 1.8 read "unavailable:&lt;version&gt;" and came from a check that did
        /// not know WoW Forever's _Forever TOCs: they wrongly parked Questie on
        /// that client. They no longer match, so such a package is tried again
        /// once. Raise the number whenever the check learns something that could
        /// turn an old "unavailable" into "available".
        /// </summary>
        internal static string UnavailableMarker(string version) => "unavailable@2:" + version;

        /// <summary>True when this release of the package was found unusable for this client.</summary>
        internal static bool KnownUnavailable(InstallManifest manifest, ManagedPackage pkg) =>
            string.Equals(manifest.GetTag(pkg.Key), UnavailableMarker(Resolved(pkg).Version),
                          StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Compares two versions of ONE package by the numbers in them, left to
        /// right: "6.12.4" is newer than "6.12.3", "r837" than "r835",
        /// "bundle/v12.13.0+v1.1.5" than "bundle/v12.11.1+v1.1.3". Positive when
        /// <paramref name="a"/> is the newer one, 0 when they are the same or
        /// carry no numbers to tell by.
        /// </summary>
        internal static int CompareVersions(string a, string b)
        {
            var x = VersionNumbers(a);
            var y = VersionNumbers(b);
            for (int i = 0; i < Math.Min(x.Count, y.Count); i++)
                if (x[i] != y[i]) return x[i] > y[i] ? 1 : -1;
            return x.Count.CompareTo(y.Count);
        }

        private static List<decimal> VersionNumbers(string version)
        {
            var numbers = new List<decimal>();
            var digits = new System.Text.StringBuilder();
            foreach (char c in (version ?? "") + " ")
            {
                if (c >= '0' && c <= '9') { digits.Append(c); continue; }
                if (digits.Length == 0) continue;
                numbers.Add(decimal.TryParse(digits.ToString(), out decimal n) ? n : 0);
                digits.Clear();
            }
            return numbers;
        }

        /// <summary>
        /// True when the package's newest release could not be looked up this
        /// run and what is installed is NEWER than the built-in version the
        /// installer fell back to. The installed one is then kept: without this
        /// a single refused or timed-out lookup put the older built-in version
        /// over it, and the next working lookup put the newer one back. A
        /// built-in version that is newer than the installed one is still
        /// installed: that is how a player CurseForge refuses (HTTP 403 for
        /// every program, reported 2026-11-24) gets updates at all.
        /// A lookup that WORKED is always followed, also back to an older release.
        /// </summary>
        internal static bool KeepsNewerInstall(string addonsFolder, InstallManifest manifest, ManagedPackage pkg)
        {
            var resolved = Resolved(pkg);
            if (resolved.LookupFailedSource == null) return false;
            string current = manifest.GetTag(pkg.Key);
            if (string.IsNullOrEmpty(current) ||
                current.StartsWith("unavailable", StringComparison.OrdinalIgnoreCase)) return false;
            if (pkg.RequiredRoots.Any(r => !Directory.Exists(Path.Combine(addonsFolder, r)))) return false;
            return CompareVersions(current, resolved.Version) > 0;
        }

        /// <summary>True when every required folder of every applicable package exists on disk.</summary>
        public static bool EntryInstalled(string addonsFolder, string product, ManagedEntry entry) =>
            PackagesFor(entry, product).Any() &&
            PackagesFor(entry, product).All(p => p.RequiredRoots.All(
                r => Directory.Exists(Path.Combine(addonsFolder, r))));

        /// <summary>
        /// True when the entry needs a download for this client this run: an
        /// applicable package's recorded version differs from the resolved one, or
        /// a required folder is missing.
        /// </summary>
        public static bool EntryNeedsWork(string addonsFolder, string product,
                                          InstallManifest manifest, ManagedEntry entry)
        {
            foreach (var pkg in PackagesFor(entry, product))
            {
                if (KnownUnavailable(manifest, pkg)) continue;
                var resolved = Resolved(pkg);
                string current = manifest.GetTag(pkg.Key);
                if (!string.Equals(current, resolved.Version, StringComparison.OrdinalIgnoreCase) &&
                    !KeepsNewerInstall(addonsFolder, manifest, pkg))
                    return true;
                if (pkg.RequiredRoots.Any(r => !Directory.Exists(Path.Combine(addonsFolder, r))))
                    return true;
                if (ObsoleteRootsPresent(addonsFolder, pkg).Any())
                    return true;
            }
            return false;
        }

        /// <summary>The package's obsolete folders that exist and are real folders (not symlinks).</summary>
        internal static IEnumerable<string> ObsoleteRootsPresent(string addonsFolder, ManagedPackage pkg) =>
            (pkg.ObsoleteRoots ?? new string[0]).Where(r =>
                Directory.Exists(Path.Combine(addonsFolder, r)) && !AddonInstaller.IsSymlinked(addonsFolder, r));

        /// <summary>
        /// Display names for the opening screen: (updates, freshInstalls) among the
        /// enabled entries of one managed client.
        /// </summary>
        public static void PendingWork(InstallTarget target, IDictionary<string, bool> enabled,
                                       out List<string> updates, out List<string> freshInstalls)
        {
            updates = new List<string>();
            freshInstalls = new List<string>();
            if (!AppliesTo(target)) return;

            var manifest = InstallManifest.Load(target.AddOnsPath);
            foreach (var entry in Entries)
            {
                if (!enabled.TryGetValue(entry.PrefKey, out bool on) || !on) continue;
                if (!EntryNeedsWork(target.AddOnsPath, target.Product, manifest, entry)) continue;
                if (EntryInstalled(target.AddOnsPath, target.Product, entry)) updates.Add(entry.DisplayName);
                else freshInstalls.Add(entry.DisplayName);
            }
        }
    }

    /// <summary>
    /// Which managed entries the user wants, persisted across runs in
    /// %LOCALAPPDATA%\WowVisionUpdater\WowVisionManagedAddons.txt ("ManageQuestie=1"
    /// lines). One-click updates read the same store, so an unchecked entry stays
    /// unchecked without a wizard visit.
    /// </summary>
    public static class ManagedPrefs
    {
        private static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Shortcut.UpdaterDirName, "WowVisionManagedAddons.txt");

        /// <summary>The effective choice per PrefKey: stored value, else the entry default.</summary>
        public static Dictionary<string, bool> Load()
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in ManagedAddons.Entries)
                result[e.PrefKey] = e.DefaultEnabled;
            try
            {
                if (File.Exists(StorePath))
                {
                    foreach (var line in File.ReadAllLines(StorePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        if (result.ContainsKey(key))
                            result[key] = line.Substring(eq + 1).Trim() == "1";
                    }
                }
            }
            catch (Exception ex) { Logger.Warning($"Managed prefs unreadable, using defaults: {ex.Message}"); }
            return result;
        }

        public static void Save(IDictionary<string, bool> enabled)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
                var lines = new List<string>();
                foreach (var e in ManagedAddons.Entries)
                    if (enabled.TryGetValue(e.PrefKey, out bool on))
                        lines.Add($"{e.PrefKey}={(on ? "1" : "0")}");
                File.WriteAllLines(StorePath, lines);
            }
            catch (Exception ex) { Logger.Warning($"Could not save managed prefs: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Downloads and installs one managed package: SHA-256 check for pinned
    /// sources, zip-content validation (only expected/prefixed top-level folders,
    /// each carrying a TOC, at least one written for this client), then the same
    /// crash-safe folder swap WowVision gets. The manifest records the installed
    /// version per package key.
    /// </summary>
    public static class ManagedAddonInstaller
    {
        private static string TempRoot => AddonInstaller.TempRoot;

        /// <summary>
        /// Installs every package of one entry that applies to the given client.
        /// Returns true when all packages ended installed and current; false when
        /// any failed (logged, the rest still tried — one broken CDN pin must not
        /// cost the other addons).
        /// </summary>
        public static async Task<bool> InstallEntryAsync(
            string addonsFolder, string product, ManagedEntry entry, GitHubClient github,
            InstallManifest manifest, Action<InstallProgress> report, bool force,
            CancellationToken cancel = default(CancellationToken))
        {
            bool ok = true;
            foreach (var pkg in ManagedAddons.PackagesFor(entry, product))
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    if (!await InstallPackageAsync(addonsFolder, product, entry, pkg, github, manifest, report, force, cancel))
                        ok = false;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ok = false;
                    Logger.Error($"{pkg.Key}: install failed", ex);
                    report(new InstallProgress { Addon = entry.DisplayName, Phase = "skip",
                        Percent = -1, Message = $"{entry.DisplayName}: {ex.Message}" });
                }
            }
            return ok;
        }

        private static async Task<bool> InstallPackageAsync(
            string addonsFolder, string product, ManagedEntry entry, ManagedPackage pkg, GitHubClient github,
            InstallManifest manifest, Action<InstallProgress> report, bool force,
            CancellationToken cancel)
        {
            var resolved = ManagedAddons.Resolved(pkg);
            string current = manifest.GetTag(pkg.Key);
            bool rootsPresent = pkg.RequiredRoots.All(r => Directory.Exists(Path.Combine(addonsFolder, r)));

            if (!force && ManagedAddons.KnownUnavailable(manifest, pkg))
            {
                report(new InstallProgress { Addon = entry.DisplayName, Phase = "skip",
                    Percent = 100, Message = Loc.Format("managed.notForClient", entry.DisplayName, WowLocator.ProductLabel(product)) });
                return true;
            }

            bool keepsNewer = !force && ManagedAddons.KeepsNewerInstall(addonsFolder, manifest, pkg);
            if (keepsNewer)
                Logger.Info($"{pkg.Key}: installed {current} is newer than the built-in {resolved.Version}; kept.");
            if (!force && rootsPresent &&
                (keepsNewer || string.Equals(current, resolved.Version, StringComparison.OrdinalIgnoreCase)))
            {
                report(new InstallProgress { Addon = entry.DisplayName, Phase = "skip",
                    Percent = 100, Message = Loc.Format("ai.upToDate", entry.DisplayName, keepsNewer ? current : resolved.Version) });
                return RemoveObsoleteRoots(addonsFolder, entry, pkg, report);
            }

            // Never write through a developer's symlink/junction.
            foreach (var root in pkg.RequiredRoots)
            {
                if (AddonInstaller.IsSymlinked(addonsFolder, root))
                {
                    Logger.Warning($"{pkg.Key}: {root} is a symlink/junction — left untouched.");
                    report(new InstallProgress { Addon = entry.DisplayName, Phase = "skip",
                        Percent = 100, Message = Loc.Format("ai.symlink", entry.DisplayName) });
                    return true;
                }
            }

            Directory.CreateDirectory(TempRoot);
            string tempZip = Path.Combine(TempRoot, $"managed_{pkg.Key}.zip");
            string staging = Path.Combine(TempRoot, $"managed_{pkg.Key}_{Guid.NewGuid():N}");
            try
            {
                Logger.Info($"{pkg.Key}: downloading {resolved.Url} ({resolved.Version})");
                int lastPct = -2;
                await github.DownloadFileAsync(resolved.Url, tempZip, (done, total) =>
                {
                    int pct = total > 0 ? (int)(done * 100 / total) : -1;
                    if (pct == lastPct) return;
                    lastPct = pct;
                    string mb = total > 0 ? $"{done / 1048576} / {total / 1048576} MB" : $"{done / 1048576} MB";
                    report(new InstallProgress { Addon = entry.DisplayName + " (" + pkg.Key + ")",
                        Phase = "download", Percent = pct,
                        Message = Loc.Format("ai.downloading", entry.DisplayName, mb) });
                }, cancel);
                cancel.ThrowIfCancellationRequested();

                // Integrity: pinned sources carry a baked-in hash. Live github
                // downloads are the canonical release URL over TLS — the same
                // trust model every other download in this installer uses. Live
                // CurseForge files have no published hash; they must have the
                // size CurseForge lists for them.
                if (!string.IsNullOrEmpty(resolved.Sha256) && !VerifySha256(tempZip, resolved.Sha256))
                {
                    Logger.Warning($"{pkg.Key}: SHA-256 mismatch — refusing the download.");
                    report(new InstallProgress { Addon = entry.DisplayName, Phase = "skip",
                        Percent = -1, Message = Loc.Format("managed.badHash", entry.DisplayName) });
                    return false;
                }
                long gotLength = new FileInfo(tempZip).Length;
                if (resolved.ExpectedLength > 0 && gotLength != resolved.ExpectedLength)
                {
                    Logger.Warning($"{pkg.Key}: {gotLength} bytes where CurseForge lists {resolved.ExpectedLength} — refusing the download.");
                    report(new InstallProgress { Addon = entry.DisplayName, Phase = "skip",
                        Percent = -1, Message = Loc.Format("managed.badHash", entry.DisplayName) });
                    return false;
                }

                report(new InstallProgress { Addon = entry.DisplayName + " (" + pkg.Key + ")",
                    Phase = "extract", Percent = -1,
                    Message = Loc.Format("ai.extracting", entry.DisplayName) });
                Directory.CreateDirectory(staging);
                ZipFile.ExtractToDirectory(tempZip, staging);

                // Validate the zip's shape before touching AddOns: every top-level
                // folder must be expected (or match the prefix) and carry a TOC;
                // every required folder must actually be in the zip.
                var roots = Directory.GetDirectories(staging).Select(Path.GetFileName).ToList();
                // Folders the package is known to carry and that are left out on
                // purpose: not checked, not copied.
                foreach (var skipped in roots.Where(r => pkg.SkipRoots.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList())
                {
                    roots.Remove(skipped);
                    Logger.Info($"{pkg.Key}: folder '{skipped}' is left out on purpose; not installed.");
                }
                if (Directory.GetFiles(staging).Length > 0)
                {
                    Logger.Warning($"{pkg.Key}: loose files at the zip root — refusing.");
                    return false;
                }
                foreach (var root in roots)
                {
                    bool allowed = pkg.RequiredRoots.Contains(root, StringComparer.OrdinalIgnoreCase) ||
                                   (!string.IsNullOrEmpty(pkg.RootPrefix) &&
                                    root.StartsWith(pkg.RootPrefix, StringComparison.OrdinalIgnoreCase));
                    if (!allowed)
                    {
                        Logger.Warning($"{pkg.Key}: unexpected top-level folder '{root}' — refusing.");
                        return false;
                    }
                    if (Directory.GetFiles(Path.Combine(staging, root), "*.toc").Length == 0)
                    {
                        Logger.Warning($"{pkg.Key}: '{root}' has no TOC file — refusing.");
                        return false;
                    }
                }
                foreach (var required in pkg.RequiredRoots)
                {
                    if (!roots.Contains(required, StringComparer.OrdinalIgnoreCase))
                    {
                        Logger.Warning($"{pkg.Key}: required folder '{required}' missing from the zip — refusing.");
                        return false;
                    }
                }

                // The check per game version: at least one of the package's folders
                // must carry a TOC this client loads. Without it the addon would sit
                // in AddOns and never load — or worse, load a build meant for
                // another client. Not a failure: the addon simply does not exist for
                // this game version, and the next run does not download it again.
                if (!pkg.RequiredRoots.Any(r => WowLocator.FolderServesClient(staging, r, product)))
                {
                    Logger.Info($"{pkg.Key} {resolved.Version}: no TOC for {product}; not installed there.");
                    manifest.SetTag(pkg.Key, ManagedAddons.UnavailableMarker(resolved.Version));
                    report(new InstallProgress { Addon = entry.DisplayName + " (" + pkg.Key + ")", Phase = "skip",
                        Percent = 100, Message = Loc.Format("managed.notForClient", entry.DisplayName, WowLocator.ProductLabel(product)) });
                    return true;
                }

                report(new InstallProgress { Addon = entry.DisplayName + " (" + pkg.Key + ")",
                    Phase = "install", Percent = -1,
                    Message = Loc.Format("ai.installing", entry.DisplayName) });

                int locked = 0;
                foreach (var root in roots)
                {
                    string source = Path.Combine(staging, root);
                    string target = Path.Combine(addonsFolder, root);
                    if (!WowLocator.IsGameRunning() && Directory.Exists(target))
                        AddonInstaller.CleanReplace(source, target);
                    else
                        locked += AddonInstaller.OverwriteInPlace(source, target);
                }

                if (locked > 0)
                {
                    // Incomplete — leave the old tag so the next run retries.
                    report(new InstallProgress { Addon = entry.DisplayName, Phase = "done",
                        Percent = 100, Message = Loc.Format("ai.locked", entry.DisplayName, locked) });
                    Logger.Warning($"{pkg.Key}: {locked} locked file(s); version NOT recorded.");
                    return false;
                }

                manifest.SetTag(pkg.Key, resolved.Version);
                report(new InstallProgress { Addon = entry.DisplayName + " (" + pkg.Key + ")",
                    Phase = "done", Percent = 100,
                    Message = Loc.Format("ai.installed", entry.DisplayName, resolved.Version) });
                Logger.Info($"{pkg.Key}: installed {resolved.Version}.");
                return RemoveObsoleteRoots(addonsFolder, entry, pkg, report);
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }

        /// <summary>
        /// Deletes the package's obsolete folders (exact names, never a symlink),
        /// only ever after the new package is in place. Returns false when one
        /// could not be removed (e.g. locked); the next run retries.
        /// </summary>
        private static bool RemoveObsoleteRoots(string addonsFolder, ManagedEntry entry,
                                                ManagedPackage pkg, Action<InstallProgress> report)
        {
            bool ok = true;
            foreach (var root in ManagedAddons.ObsoleteRootsPresent(addonsFolder, pkg).ToList())
            {
                try
                {
                    Directory.Delete(Path.Combine(addonsFolder, root), true);
                    Logger.Info($"{pkg.Key}: removed obsolete folder {root}.");
                    report(new InstallProgress { Addon = entry.DisplayName + " (" + pkg.Key + ")",
                        Phase = "cleanup:" + root, Percent = 100,
                        Message = Loc.Format("managed.obsoleteRemoved", entry.DisplayName, root) });
                }
                catch (Exception ex)
                {
                    ok = false;
                    Logger.Warning($"{pkg.Key}: could not remove obsolete folder {root}: {ex.Message}");
                }
            }
            return ok;
        }

        private static bool VerifySha256(string file, string expected)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
            {
                string actual = BitConverter.ToString(sha.ComputeHash(stream))
                                            .Replace("-", "").ToLowerInvariant();
                return string.Equals(actual, expected.Trim().ToLowerInvariant(), StringComparison.Ordinal);
            }
        }
    }
}
