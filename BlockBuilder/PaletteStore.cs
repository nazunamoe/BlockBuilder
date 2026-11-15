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
            System.IO.Directory.CreateDirectory(Directory);
            string name = string.IsNullOrWhiteSpace(palette.Name) ? "palette" : palette.Name;
            var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            string path = Path.Combine(Directory, safe + ".txt");
            File.WriteAllText(path, palette.Serialize(), new UTF8Encoding(false));
            Reload();
            Mod.log.Info($"palette saved: {path} ({palette.Entries.Count} entries)");
            return path;
        }
    }
}
