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
        public Preferences() { Favorites = new List<string>(); Recipes = new List<Recipe>(); }
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
                    if (titleFilter != null && file.IndexOf(Path.DirectorySeparatorChar + titleFilter + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0) continue;
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
