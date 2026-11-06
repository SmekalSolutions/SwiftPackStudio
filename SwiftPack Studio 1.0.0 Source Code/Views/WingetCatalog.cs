using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SwiftPack.Views
{
    /// <summary>
    /// Local copy of the winget community catalog, so looking up a package's latest version costs
    /// nothing.
    ///
    /// WHY THIS EXISTS
    /// The winget fetch used to call api.github.com once per app, purely to list the version folders
    /// under manifests/&lt;x&gt;/&lt;Publisher&gt;/&lt;Package&gt; and read the newest one. GitHub's
    /// unauthenticated API allows 60 requests per hour per IP, so ~15 apps used a quarter of the
    /// hourly budget on a single refresh, and everything 403'd after four refreshes. At 1000 apps it
    /// would be unusable on the first page load, and a Personal Access Token does not save it - the
    /// 5,000/hour ceiling is per TOKEN, so one token shipped in the product would be shared by every
    /// customer.
    ///
    /// This is what winget itself does: download the pre-indexed source once, then answer every
    /// lookup locally.
    ///
    ///     https://cdn.winget.microsoft.com/cache/source2.msix    (an MSIX = a ZIP)
    ///        -> Public/index.db                                  (a SQLite database)
    ///        -> SELECT latest_version ... WHERE id = ...
    ///
    /// Both constants are taken from winget-cli's own source, not from memory:
    ///   SourceList.cpp                   s_Source_WingetCommunityDefault_Arg = "https://cdn.winget.microsoft.com/cache"
    ///   PreIndexedPackageSourceFactory.cpp  V2_PackageFileName = "source2.msix",  IndexFilePath = "Public\index.db"
    ///
    /// COST: one download per day, whatever the app count. 1000 apps = 1 request, not 1000.
    /// raw.githubusercontent.com is still used for the installer manifest of the handful of apps whose
    /// version actually changed - that is a CDN with no API quota.
    ///
    /// REQUIRES the Microsoft.Data.Sqlite NuGet package (MIT, first-party):
    ///     &lt;PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.11" /&gt;
    /// </summary>
    internal static class WingetCatalog
    {
        // From winget-cli SourceList.cpp / PreIndexedPackageSourceFactory.cpp.
        private const string SourceMsixUrl   = "https://cdn.winget.microsoft.com/cache/source2.msix";
        private const string IndexEntryName  = "Public/index.db";   // ZIP entries use '/'

        /// <summary>How stale the catalog may get before it is re-downloaded.</summary>
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static bool _unavailable;   // sticky: do not retry a failed download every lookup

        /// <summary>%LOCALAPPDATA%\SwiftPack\winget\index.db</summary>
        internal static string IndexPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SwiftPack", "winget", "index.db");

        /// <summary>True when a usable, fresh-enough index is on disk.</summary>
        private static bool IsFresh()
        {
            try
            {
                var fi = new FileInfo(IndexPath);
                return fi.Exists && fi.Length > 0 && DateTime.UtcNow - fi.LastWriteTimeUtc < MaxAge;
            }
            catch { return false; }
        }

        /// <summary>
        /// Downloads the catalog if it is missing or stale. Safe to call from many places: the
        /// semaphore means only one download ever runs, and _unavailable stops a failed download being
        /// retried on every single lookup.
        /// Returns false when no usable index exists - the caller then falls back to the GitHub path.
        /// </summary>
        /// <param name="force">
        /// Re-download even if the cached index is still fresh. Used by the Refresh button: without
        /// it, Refresh re-read the SAME local index and handed back the SAME version, so for winget
        /// apps "Refresh" did not actually refresh anything for up to 12 hours.
        /// </param>
        internal static async Task<bool> EnsureAsync(HttpClient http, Action<string>? log = null, bool force = false)
        {
            if (!force && IsFresh()) return true;
            if (!force && _unavailable && File.Exists(IndexPath)) return true;   // stale but usable

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!force && IsFresh()) return true;   // another caller just did it

                string dir = Path.GetDirectoryName(IndexPath)!;
                Directory.CreateDirectory(dir);
                string tmpMsix = Path.Combine(dir, "source2.msix.tmp");
                string tmpDb   = IndexPath + ".tmp";

                log?.Invoke("[WingetCatalog] downloading " + SourceMsixUrl);

                // Stream to disk: the index is tens of MB, so do not buffer it in memory.
                using (var resp = await http.GetAsync(SourceMsixUrl, HttpCompletionOption.ResponseHeadersRead)
                                            .ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var dst = File.Create(tmpMsix);
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                }

                // MSIX is an OPC package, i.e. an ordinary ZIP - ZipArchive reads it directly.
                using (var zip = ZipFile.OpenRead(tmpMsix))
                {
                    var entry = zip.Entries.FirstOrDefault(e =>
                        e.FullName.Replace('\\', '/').Equals(IndexEntryName, StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                        throw new FileNotFoundException($"{IndexEntryName} not found inside source2.msix");

                    using var es = entry.Open();
                    using var fs = File.Create(tmpDb);
                    await es.CopyToAsync(fs).ConfigureAwait(false);
                }

                // Swapped in one move, and reported on its own.
                //
                // This was "delete the old file, then move the new one over", inside the outer catch.
                // When the delete failed because a reader still held index.db open, the download and
                // the extract - both of which had just worked - were reported as
                // "download failed, falling back to the GitHub API", the two temp files were left on
                // disk, and index.db kept its old timestamp. Which meant it was never fresh, so the
                // next call downloaded 3 MB again, failed the same way, and the catalog stayed frozen
                // at whatever it held when the process first read it. The visible symptom is an
                // index.db.tmp NEWER than index.db, with a source2.msix.tmp beside it.
                //
                // Move with overwrite does it in one step, and a failure here says what it was.
                try
                {
                    File.Move(tmpDb, IndexPath, overwrite: true);
                }
                catch (Exception exSwap)
                {
                    log?.Invoke($"[WingetCatalog] downloaded, but could not replace the index: {exSwap.Message}");
                    throw;
                }
                finally
                {
                    // Neither temp file is worth keeping either way: the next attempt re-downloads.
                    // Left behind, they were ~11 MB of dead weight that also hid the real failure.
                    try { File.Delete(tmpMsix); } catch { }
                    try { if (File.Exists(tmpDb)) File.Delete(tmpDb); } catch { }
                }

                _unavailable = false;
                log?.Invoke($"[WingetCatalog] index ready ({new FileInfo(IndexPath).Length / 1024 / 1024} MB)");
                return true;
            }
            catch (Exception ex)
            {
                _unavailable = true;
                log?.Invoke($"[WingetCatalog] download failed, falling back to the GitHub API: {ex.Message}");
                return File.Exists(IndexPath);   // a stale index still beats hammering GitHub
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Latest version of a package, straight from the local index. Null when the package is not in
        /// the catalog or the index cannot be read.
        ///
        /// Handles BOTH index schemas, because the schema the CDN currently serves cannot be assumed:
        ///
        ///   schema 2.x - a single denormalised table, with the answer already computed:
        ///       packages(id, name, moniker, latest_version, arp_min_version, arp_max_version, hash)
        ///
        ///   schema 1.x - normalised, so the versions have to be joined and compared:
        ///       ids(id)  versions(version)  manifest(id -> ids.rowid, version -> versions.rowid, ...)
        ///
        /// Detected from sqlite_master rather than from a version number, so it keeps working if
        /// Microsoft ships another revision.
        /// </summary>
        internal static string? TryGetLatestVersion(string packageId, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(packageId) || !File.Exists(IndexPath)) return null;

            try
            {
                // Read-only: the index is a plain file we never write to.
                //
                // Pooling OFF, and it matters. Microsoft.Data.Sqlite pools connections by default
                // since 6.0, so disposing this one hands the HANDLE back to the pool instead of
                // closing it, and index.db stays open for the life of the process. The next refresh
                // then cannot replace the file it is trying to update - see EnsureAsync.
                using var cn = new Microsoft.Data.Sqlite.SqliteConnection(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    {
                        DataSource = IndexPath,
                        Mode       = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                        Pooling    = false,
                    }.ToString());
                cn.Open();

                bool Has(string table)
                {
                    using var c = cn.CreateCommand();
                    c.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1";
                    c.Parameters.AddWithValue("$n", table);
                    return c.ExecuteScalar() != null;
                }

                // ── schema 2.x ────────────────────────────────────────────────────────────────
                if (Has("packages"))
                {
                    using var cmd = cn.CreateCommand();
                    cmd.CommandText = "SELECT latest_version FROM packages WHERE id = $id COLLATE NOCASE LIMIT 1";
                    cmd.Parameters.AddWithValue("$id", packageId);
                    return cmd.ExecuteScalar() as string;
                }

                // ── schema 1.x ────────────────────────────────────────────────────────────────
                if (Has("manifest") && Has("ids") && Has("versions"))
                {
                    var all = new List<string>();
                    using (var cmd = cn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT v.version FROM manifest m " +
                            "JOIN ids i      ON m.id      = i.rowid " +
                            "JOIN versions v ON m.version = v.rowid " +
                            "WHERE i.id = $id COLLATE NOCASE";
                        cmd.Parameters.AddWithValue("$id", packageId);
                        using var rd = cmd.ExecuteReader();
                        while (rd.Read())
                            if (rd.GetValue(0) is string s && s.Length > 0) all.Add(s);
                    }
                    // The 1.x index lists every version, so pick the highest. "Latest" is a marker
                    // some manifests use and always wins.
                    return all
                        .OrderByDescending(v => v.Equals("Latest", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(VersionSortKey, StringComparer.Ordinal)
                        .FirstOrDefault();
                }

                log?.Invoke("[WingetCatalog] unrecognised index schema - no packages/manifest table");
                return null;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[WingetCatalog] lookup failed for {packageId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>A catalog entry: what the picker shows.</summary>
        internal sealed class WingetPackage
        {
            public string Id      { get; set; } = "";
            public string Name    { get; set; } = "";
            public string Version { get; set; } = "";
            public override string ToString() => Id;
        }

        /// <summary>
        /// Packages in the local catalog matching <paramref name="query"/>, best first. An EMPTY query
        /// lists the catalog (capped at <paramref name="max"/>), so the picker opens populated rather
        /// than blank.
        ///
        /// Matches on BOTH the id and the display name - searching "compare" should find
        /// Smekal.FileCompare.64-bit even though the word order differs.
        ///
        /// Costs nothing: the whole winget repo is already on disk, so this is a local SQLite query -
        /// no GitHub, no network, no rate limit.
        ///
        /// Ordered so a prefix match wins, then the shortest id - typing "filecompare" puts
        /// Smekal.FileCompare above Smekal.FileCompare.Custom.
        /// </summary>
        internal static List<WingetPackage> SearchPackages(string query, int max = 20000)
        {
            var hits = new List<WingetPackage>();
            string q = (query ?? "").Trim();
            if (!File.Exists(IndexPath)) return hits;

            try
            {
                using var cn = new Microsoft.Data.Sqlite.SqliteConnection(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    {
                        DataSource = IndexPath,
                        Mode       = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                        Pooling    = false,   // see the note on the other connection
                    }.ToString());
                cn.Open();

                bool Has(string table)
                {
                    using var c = cn.CreateCommand();
                    c.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1";
                    c.Parameters.AddWithValue("$n", table);
                    return c.ExecuteScalar() != null;
                }

                using var cmd = cn.CreateCommand();

                if (Has("packages"))
                {
                    // schema 2.x - name and latest_version sit right there beside the id.
                    //
                    // Sorted BY NAME, not by id. Ordering by id put "0-don.clippy", "0state.scafld"
                    // and "0x192.UniversalAndroidDebloater" at the top of the list, because ids
                    // starting with a digit sort before letters - useless for browsing.
                    //
                    // When searching, relevance still wins first (a prefix match on the name, then on
                    // the id), and only then alphabetical.
                    cmd.CommandText = q.Length == 0
                        ? "SELECT id, name, latest_version FROM packages " +
                          "ORDER BY name COLLATE NOCASE ASC, id COLLATE NOCASE ASC LIMIT $max"
                        : "SELECT id, name, latest_version FROM packages " +
                          "WHERE id LIKE $q OR name LIKE $q " +
                          "ORDER BY (name LIKE $pfx) DESC, (id LIKE $pfx) DESC, " +
                          "name COLLATE NOCASE ASC, id COLLATE NOCASE ASC LIMIT $max";
                }
                else if (Has("ids"))
                {
                    // schema 1.x - only the id is cheaply reachable; name/version stay blank.
                    cmd.CommandText = q.Length == 0
                        ? "SELECT id, '', '' FROM ids ORDER BY id COLLATE NOCASE ASC LIMIT $max"
                        : "SELECT id, '', '' FROM ids WHERE id LIKE $q " +
                          "ORDER BY (id LIKE $pfx) DESC, id COLLATE NOCASE ASC LIMIT $max";
                }
                else return hits;

                if (q.Length > 0)
                {
                    cmd.Parameters.AddWithValue("$q",   "%" + q + "%");
                    cmd.Parameters.AddWithValue("$pfx", q + "%");
                }
                cmd.Parameters.AddWithValue("$max", max);

                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    string id = rd.IsDBNull(0) ? "" : rd.GetString(0);
                    if (id.Length == 0) continue;
                    hits.Add(new WingetPackage
                    {
                        Id      = id,
                        Name    = rd.IsDBNull(1) ? "" : rd.GetString(1),
                        Version = rd.IsDBNull(2) ? "" : rd.GetString(2),
                    });
                }
            }
            catch { /* no catalog yet, or unreadable - the picker just comes up empty */ }

            return hits;
        }

        /// <summary>Ids only. Kept for callers that just want the identifier.</summary>
        internal static List<string> SearchIds(string query, int max = 15)
            => SearchPackages(query, max).Select(p => p.Id).ToList();

        /// <summary>
        /// Zero-padded key so versions sort numerically as strings: "2.9" must not beat "2.61.1".
        /// Same shape as the VerSortKey the GitHub path already used, kept identical on purpose.
        /// </summary>
        private static string VersionSortKey(string v) => string.Join(".",
            v.Split('.', '-', '+').Select(p =>
            {
                string digits = new string(p.TakeWhile(char.IsDigit).ToArray());
                return (digits.Length > 0 ? digits : "0").PadLeft(12, '0');
            }));
    }
}
