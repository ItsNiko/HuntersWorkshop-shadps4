// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace HuntersWorkshop
{
    public sealed class Preferences
    {
        public string ShadDirectory { get; set; }
        public string UserDirectory { get; set; }
        public List<string> Favorites { get; set; }
        public List<Recipe> Recipes { get; set; }
        // Remembered between sessions: the amount and reinforcement last chosen on the Items / Favorites page,
        // the amount of the "favorites" kit, per-item amounts of the ready-made kits, and the custom kits.
        public int ItemQuantity { get; set; }
        public int ItemUpgrade { get; set; }
        public int FavoritesKitQuantity { get; set; }
        public Dictionary<string, int> KitQuantities { get; set; }
        public List<CustomKit> CustomKits { get; set; }
        public CustomKit Draft { get; set; }
        public List<KitItem> Recent { get; set; }
        public Preferences()
        {
            Favorites = new List<string>(); Recipes = new List<Recipe>();
            ItemQuantity = 1; FavoritesKitQuantity = 1;
            KitQuantities = new Dictionary<string, int>(); CustomKits = new List<CustomKit>(); Draft = new CustomKit(); Recent = new List<KitItem>();
        }
    }

    public sealed class KitItem
    {
        public int Category { get; set; }
        public int Id { get; set; }
        public int Upgrade { get; set; }
        public int Quantity { get; set; }
    }

    public sealed class CustomKit
    {
        public string Name { get; set; }
        public List<KitItem> Items { get; set; }
        public CustomKit() { Items = new List<KitItem>(); }
    }

    public sealed class Recipe
    {
        public string Name { get; set; }
        public bool Rune { get; set; }
        public int Shape { get; set; }
        public int Source { get; set; }
        public int[] Effects { get; set; }
    }

    public static class Storage
    {
        static readonly object logLock = new object();
        public static string Root
        {
            get
            {
                string test = Environment.GetEnvironmentVariable("HUNTERS_WORKSHOP_DATA");
                if (!String.IsNullOrWhiteSpace(test)) return Path.GetFullPath(test);
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HuntersWorkshop");
            }
        }
        public static string Backups { get { return Path.Combine(Root, "backups"); } }
        public static JavaScriptSerializer Json { get { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }; } }

        public static Preferences Load()
        {
            foreach (string suffix in new[] { "", ".previous" })
            {
                try
                {
                    string path = Path.Combine(Root, "preferences.json") + suffix;
                    if (!File.Exists(path)) continue;
                    var result = Json.Deserialize<Preferences>(File.ReadAllText(path));
                    if (result == null) continue;
                    result.Favorites = result.Favorites ?? new List<string>();
                    result.Recipes = (result.Recipes ?? new List<Recipe>()).Where(r => r != null && r.Effects != null && r.Effects.Length == 6).ToList();
                    result.KitQuantities = result.KitQuantities ?? new Dictionary<string, int>();
                    result.CustomKits = (result.CustomKits ?? new List<CustomKit>()).Where(k => k != null && !String.IsNullOrWhiteSpace(k.Name)).ToList();
                    foreach (CustomKit kit in result.CustomKits) kit.Items = (kit.Items ?? new List<KitItem>()).Where(i => i != null).ToList();
                    result.Draft = result.Draft ?? new CustomKit();
                    result.Draft.Items = (result.Draft.Items ?? new List<KitItem>()).Where(i => i != null).ToList();
                    result.Recent = (result.Recent ?? new List<KitItem>()).Where(i => i != null).Take(30).ToList();
                    if (result.ItemQuantity < 1 || result.ItemQuantity > 999) result.ItemQuantity = 1;
                    if (result.FavoritesKitQuantity < 1 || result.FavoritesKitQuantity > 999) result.FavoritesKitQuantity = 1;
                    return result;
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)) throw;
                    Log("Preferences could not be read: " + ex.Message);
                }
            }
            return new Preferences();
        }

        public static void Save(Preferences preferences)
        {
            Directory.CreateDirectory(Root);
            AtomicWrite(Path.Combine(Root, "preferences.json"), Json.Serialize(preferences));
        }

        public static void AtomicWrite(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string previous = path + ".previous";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(true);
                }
                if (File.Exists(path))
                {
                    // File.Replace is atomic on the normal NTFS application-data destination.
                    File.Replace(temporary, path, previous, true);
                }
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static void Log(string message)
        {
            // A diagnostic failure must never change whether a game command succeeds.
            try
            {
                lock (logLock)
                {
                    Directory.CreateDirectory(Root);
                    string path = Path.Combine(Root, "activity.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    {
                        string previous = path + ".previous";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }
                    File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
                }
            }
            catch (Exception ex) { if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Security.SecurityException)) throw; }
        }

        public static string Hash(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
    }

    public sealed class BackupEntry
    {
        public string Path { get; set; }
        public string SHA256 { get; set; }
        public long Bytes { get; set; }
    }

    public sealed class SnapshotManifest
    {
        public int Schema { get; set; }
        public string Created { get; set; }
        public string Source { get; set; }
        public List<BackupEntry> Files { get; set; }
    }

    public sealed class SnapshotInfo
    {
        public string Folder, Name, Label;
        public DateTime Created;
        public int Files;
        public long Bytes;
        public bool Valid;
        public SnapshotManifest Manifest;
        public string Title { get { return Created.ToString("ddd d MMM yyyy · HH:mm:ss"); } }
    }

    public static class SaveBackup
    {
        public static IEnumerable<string> ListFiles(string user, string titleFilter = null)
        {
            foreach (string group in new[] { "savedata" })
            {
                string root = Path.Combine(user, group);
                if (!Directory.Exists(root)) continue;
                foreach (string file in Walk(root))
                {
                    // shadPS4 stores every game's saves together; keep only this title's.
                    if (titleFilter != null && !titleFilter.Split('|').Any(t => file.IndexOf(Path.DirectorySeparatorChar + t + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    yield return file;
                }
            }
        }

        static IEnumerable<string> Walk(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Save backups do not follow symbolic links or junctions. Select the real user folder.");
            foreach (string file in Directory.GetFiles(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A save file is a link. Select the real save folder.");
                yield return file;
            }
            foreach (string dir in Directory.GetDirectories(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                foreach (string file in Walk(dir)) yield return file;
        }

        static string Full(string path) { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }

        // Every snapshot the Workshop made, newest first. Folders without a readable manifest are not ours and are ignored.
        public static List<SnapshotInfo> Snapshots(string backupsRoot)
        {
            var result = new List<SnapshotInfo>();
            if (String.IsNullOrWhiteSpace(backupsRoot) || !Directory.Exists(backupsRoot)) return result;
            foreach (string dir in Directory.GetDirectories(backupsRoot))
            {
                string name = Path.GetFileName(dir);
                if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new SnapshotInfo { Folder = dir, Name = name };
                try
                {
                    string manifestPath = Path.Combine(dir, "manifest.json");
                    if (!File.Exists(manifestPath)) continue;
                    var manifest = Storage.Json.Deserialize<SnapshotManifest>(File.ReadAllText(manifestPath));
                    if (manifest == null || manifest.Files == null) continue;
                    info.Manifest = manifest;
                    info.Files = manifest.Files.Count;
                    info.Bytes = manifest.Files.Where(f => f != null).Sum(f => f.Bytes);
                    DateTimeOffset created;
                    info.Created = DateTimeOffset.TryParse(manifest.Created, out created) ? created.LocalDateTime : Directory.GetCreationTime(dir);
                    info.Valid = true;
                    string labelPath = Path.Combine(dir, "label.txt");
                    if (File.Exists(labelPath)) info.Label = File.ReadAllText(labelPath).Trim();
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)) throw;
                    continue;
                }
                result.Add(info);
            }
            return result.OrderByDescending(x => x.Created).ThenByDescending(x => x.Name, StringComparer.Ordinal).ToList();
        }

        public static void SetLabel(SnapshotInfo snapshot, string label)
        {
            string path = Path.Combine(snapshot.Folder, "label.txt");
            if (String.IsNullOrWhiteSpace(label)) { if (File.Exists(path)) File.Delete(path); }
            else File.WriteAllText(path, label.Trim(), new UTF8Encoding(false));
            snapshot.Label = String.IsNullOrWhiteSpace(label) ? null : label.Trim();
        }

        public static void Delete(SnapshotInfo snapshot, string backupsRoot)
        {
            string root = Full(backupsRoot), folder = Full(snapshot.Folder);
            if (!folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(folder, "manifest.json")))
                throw new IOException("Only snapshots in the Workshop's backup folder can be deleted here.");
            Directory.Delete(folder, true);
        }

        // Puts a snapshot's save files back. Order matters for safety:
        //  1. every file of the snapshot is checked against its recorded SHA-256, before anything is touched;
        //  2. the current saves are backed up as a new verified snapshot;
        //  3. each file is copied beside its target, hash-checked, then swapped in;
        //  4. every restored file is checked again.
        // Files that exist now but are not in the snapshot are left alone.
        public static string Restore(SnapshotInfo snapshot, string user, string backupsRoot, string titleFilter)
        {
            if (snapshot == null || !snapshot.Valid || snapshot.Manifest == null) throw new IOException("Choose a snapshot to restore.");
            if (String.IsNullOrWhiteSpace(user) || !Directory.Exists(user))
                throw new IOException("The shadPS4 user folder was not found. Choose your shadPS4 folder in Session first.");
            user = Full(user);
            string root = Full(backupsRoot), folder = Full(snapshot.Folder);
            string separator = Path.DirectorySeparatorChar.ToString();
            if (!folder.StartsWith(root + separator, StringComparison.OrdinalIgnoreCase))
                throw new IOException("That snapshot is not inside the Workshop's backup folder.");
            if (snapshot.Manifest.Files.Count == 0) throw new IOException("The snapshot contains no files.");

            string savedata = Path.Combine(user, "savedata") + separator;
            var plan = new List<KeyValuePair<string, BackupEntry>>();
            foreach (BackupEntry entry in snapshot.Manifest.Files)
            {
                if (entry == null || String.IsNullOrWhiteSpace(entry.Path) || Path.IsPathRooted(entry.Path) || String.IsNullOrWhiteSpace(entry.SHA256))
                    throw new IOException("The snapshot's manifest is not valid. Nothing was changed.");
                string source = Path.GetFullPath(Path.Combine(folder, entry.Path));
                string target = Path.GetFullPath(Path.Combine(user, entry.Path));
                if (!source.StartsWith(folder + separator, StringComparison.OrdinalIgnoreCase) || !target.StartsWith(savedata, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The snapshot lists a file outside the save folder. Nothing was changed.");
                if (!File.Exists(source) || new FileInfo(source).Length != entry.Bytes || !String.Equals(Storage.Hash(source), entry.SHA256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The snapshot is damaged (" + entry.Path + "). Nothing was changed.");
                plan.Add(new KeyValuePair<string, BackupEntry>(source, entry));
            }

            string safety = null;
            if (ListFiles(user, titleFilter).Any())
            {
                safety = Create(user, backupsRoot, titleFilter);
                try { File.WriteAllText(Path.Combine(safety, "label.txt"), "Automatic backup before restoring " + snapshot.Name, new UTF8Encoding(false)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            string note = safety == null ? "" : " Your previous saves were backed up first (" + Path.GetFileName(safety) + ").";

            int restored = 0;
            try
            {
                foreach (var step in plan)
                {
                    string target = Path.GetFullPath(Path.Combine(user, step.Value.Path));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("A save file is a link: " + step.Value.Path);
                    string temporary = target + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".restore";
                    try
                    {
                        File.Copy(step.Key, temporary, false);
                        if (!String.Equals(Storage.Hash(temporary), step.Value.SHA256, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("A file changed while it was being copied (" + step.Value.Path + ").");
                        if (File.Exists(target)) File.Replace(temporary, target, null, true); else File.Move(temporary, target);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    restored++;
                }
                foreach (var step in plan)
                {
                    string target = Path.GetFullPath(Path.Combine(user, step.Value.Path));
                    if (!String.Equals(Storage.Hash(target), step.Value.SHA256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("A restored file did not verify (" + step.Value.Path + ").");
                }
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                throw new IOException(ex.Message + " " + restored + " of " + plan.Count + " files were restored before this stopped." + note, ex);
            }
            Storage.Log("Restored " + restored + " save files from " + snapshot.Folder + "." + note);
            return "Restored " + restored + " save file" + (restored == 1 ? "" : "s") + " from " + snapshot.Title + "." + note + " Start shadPS4 and load your hunter.";
        }

        public static string Create(string user, string destinationRoot, string titleFilter = null)
        {
            if (String.IsNullOrWhiteSpace(user) || !Directory.Exists(user))
                throw new IOException("Choose your shadPS4 folder in Session, or connect to the running game first.");
            user = Path.GetFullPath(user).TrimEnd(Path.DirectorySeparatorChar);
            destinationRoot = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (destinationRoot.Equals(user, StringComparison.OrdinalIgnoreCase) ||
                destinationRoot.StartsWith(user + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The backup destination must be outside the game's user folder.");

            string[] files = ListFiles(user, titleFilter).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) throw new IOException("No saves were found. Check the shadPS4 folder in Session, or load and save a character first.");
            Directory.CreateDirectory(destinationRoot);
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string staging = Path.Combine(destinationRoot, name + ".partial");
            string destination = Path.Combine(destinationRoot, name);
            Directory.CreateDirectory(staging);
            try
            {
                var entries = new List<BackupEntry>();
                foreach (string file in files)
                {
                    string relative = file.Substring(user.Length + 1);
                    string target = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                        output.Flush(true);
                    }
                    entries.Add(new BackupEntry { Path = relative, SHA256 = Storage.Hash(target), Bytes = new FileInfo(target).Length });
                }
                // Recheck the entire set after every copy, so changes to an earlier file or the
                // character list invalidate the snapshot instead of publishing an incomplete one.
                if (!files.SequenceEqual(ListFiles(user, titleFilter).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)))
                    throw new IOException("The save file list changed during backup. Wait for saving to finish and try again.");
                foreach (var entry in entries)
                {
                    string source = Path.Combine(user, entry.Path);
                    if (new FileInfo(source).Length != entry.Bytes || Storage.Hash(source) != entry.SHA256)
                        throw new IOException("A save changed during backup. Wait for the save indicator to clear and try again.");
                }
                Storage.AtomicWrite(Path.Combine(staging, "manifest.json"), Storage.Json.Serialize(new
                {
                    Schema = 1,
                    Created = DateTimeOffset.Now.ToString("o"),
                    Source = user,
                    Files = entries
                }));
                // Indexers, virus scanners and sync clients can briefly hold the directory.
                // Retry only publishing our own complete snapshot; never change the source.
                for (int attempt = 0; ; attempt++)
                {
                    try { Directory.Move(staging, destination); break; }
                    catch (IOException)
                    {
                        if (attempt >= 9 || !Directory.Exists(staging) || Directory.Exists(destination)) throw;
                        System.Threading.Thread.Sleep(100);
                    }
                }
                Storage.Log("Verified " + entries.Count + " save files. Backup: " + destination);
                return destination;
            }
            finally
            {
                // Delete only this exact, newly created staging directory under this root.
                string resolved = Path.GetFullPath(staging);
                if (resolved.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    resolved.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                {
                    try { Directory.Delete(resolved, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
