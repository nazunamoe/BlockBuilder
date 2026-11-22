using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BlockBuilder.Core;

namespace BlockBuilder
{
    /// <summary>
    /// Palettes (named placement lists) live as editable text files in
    /// ModsData/BlockBuilder/palettes/*.txt. Nothing is stored in the save game.
    /// </summary>
    public static class PaletteStore
    {
        public static string Directory => Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", nameof(BlockBuilder), "palettes");

        private static List<(string Path, Palette Palette)> s_Cache;

        public static IReadOnlyList<(string Path, Palette Palette)> All
        {
            get
            {
                if (s_Cache == null)
                {
                    Reload();
                }

                return s_Cache;
            }
        }

        /// <summary>Bumped on every reload so settings dropdowns re-read their items.</summary>
        public static int Version { get; private set; }

        public static void Reload()
        {
            Version++;
            var list = new List<(string, Palette)>();
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                foreach (var file in System.IO.Directory.GetFiles(Directory, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    // One unreadable file (locked, no permission) must not hide the lists after it.
                    try
                    {
                        var errors = new List<string>();
                        var palette = Palette.Parse(File.ReadAllText(file, Encoding.UTF8), errors);
                        if (string.IsNullOrWhiteSpace(palette.Name))
                        {
                            palette.Name = Path.GetFileNameWithoutExtension(file);
                        }

                        foreach (var e in errors)
                        {
                            Mod.log.Warn($"palette {Path.GetFileName(file)}: {e}");
                        }

                        list.Add((file, palette));
                    }
                    catch (Exception e)
                    {
                        Mod.log.Warn($"palette {Path.GetFileName(file)} skipped: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Mod.log.Error(e, "palette reload failed");
            }

            s_Cache = list;
            Mod.log.Info($"palettes: {list.Count} loaded from {Directory}");
        }

        public static Palette Get(int index)
        {
            var all = All;
            return index >= 0 && index < all.Count ? all[index].Palette : null;
        }

        public static string Save(Palette palette)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string path = PathFor(palette.Name);
                File.WriteAllText(path, palette.Serialize(), new UTF8Encoding(false));
                Reload();
                Mod.log.Info($"palette saved: {path} ({palette.Entries.Count} entries)");
                return path;
            }
            catch (Exception e)
            {
                Mod.log.Warn($"palette '{palette.Name}' could not be saved: {e.Message}");
                return null;
            }
        }

        private const int kMaxFileStem = 100;

        private static readonly HashSet<string> kReserved = new HashSet<string>
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static string PathFor(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "palette" : name.Trim();
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (safe.Length > kMaxFileStem)
            {
                safe = safe.Substring(0, kMaxFileStem).TrimEnd();
            }

            // Windows treats CON.txt, NUL.txt, COM1.txt ... as devices, not files.
            if (safe.Length == 0 || kReserved.Contains(safe.ToUpperInvariant()))
            {
                safe = "_" + safe;
            }

            return Path.Combine(Directory, safe + ".txt");
        }

        public static int IndexOf(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return -1;
            }

            var all = All;
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(Path.GetFullPath(all[i].Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>A name not used by another list file: "Name", "Name 2", "Name 3" ...</summary>
        public static string UniqueName(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "My list" : name.Trim();
            string candidate = name;
            for (int n = 2; File.Exists(PathFor(candidate)); n++)
            {
                candidate = $"{name} {n}";
            }

            return candidate;
        }

        /// <summary>Writes an edited list back to its own file.</summary>
        public static void Update(int index)
        {
            var all = All;
            if (index < 0 || index >= all.Count)
            {
                return;
            }

            try
            {
                File.WriteAllText(all[index].Path, all[index].Palette.Serialize(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                // The change stays in memory for this session; say so instead of throwing into the UI.
                Mod.log.Warn($"palette {Path.GetFileName(all[index].Path)} could not be written: {e.Message}");
            }

            Version++;
        }

        public static void Delete(int index)
        {
            var all = All;
            if (index < 0 || index >= all.Count)
            {
                return;
            }

            try
            {
                File.Delete(all[index].Path);
                Mod.log.Info($"palette deleted: {all[index].Path}");
            }
            catch (Exception e)
            {
                Mod.log.Warn($"palette {Path.GetFileName(all[index].Path)} could not be deleted: {e.Message}");
            }

            Reload();
        }

        /// <summary>Renames the list and its file; returns the new path.</summary>
        public static string Rename(int index, string newName)
        {
            var all = All;
            if (index < 0 || index >= all.Count || string.IsNullOrWhiteSpace(newName))
            {
                return null;
            }

            var (oldPath, palette) = all[index];
            string target = PathFor(newName);
            if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(oldPath), StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            {
                newName = UniqueName(newName);
                target = PathFor(newName);
            }

            try
            {
                palette.Name = newName.Trim();
                File.WriteAllText(target, palette.Serialize(), new UTF8Encoding(false));
                if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(oldPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(oldPath);
                }
            }
            catch (Exception e)
            {
                Mod.log.Warn($"palette could not be renamed to '{newName}': {e.Message}");
                Reload();
                return null;
            }

            Reload();
            return target;
        }
    }
}
