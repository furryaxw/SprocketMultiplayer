using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SprocketMultiplayer.Core {
    public static class TankDatabase {
        public static List<TankInfo> LoadTanks() {
            List<TankInfo> list = new List<TankInfo>();

            string factionsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "Sprocket", "Factions"
            );

            if (!Directory.Exists(factionsPath)) return list;

            foreach (string factionDir in Directory.GetDirectories(factionsPath))
            {
                string vehicleDir = Path.Combine(factionDir, "Blueprints", "Vehicles");
                if (!Directory.Exists(vehicleDir))
                    continue;

                foreach (string bp in Directory.GetFiles(vehicleDir, "*.blueprint", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(bp);
                    string bpDir = Path.GetDirectoryName(bp) ?? vehicleDir;
                    string png = ResolveProfileImage(bpDir, vehicleDir, name);

                    list.Add(new TankInfo
                    {
                        Name = name,
                        BlueprintPath = bp,
                        ImagePath = png,
                        Hash = ComputeFileHash(bp)
                    });
                }
            }

            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static string ResolveProfileImage(string blueprintDir, string vehicleDir, string name)
        {
            string fileName = name + ".png";
            string[] candidates =
            {
                Path.Combine(blueprintDir, "Profiles", fileName),
                Path.Combine(vehicleDir, "Profiles", fileName)
            };

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        public static string ReadBlueprintText(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            return File.ReadAllText(path, Encoding.UTF8);
        }

        public static string ReadBlueprintBase64(string path)
        {
            string text = ReadBlueprintText(path);
            return text == null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }

        private static string ComputeFileHash(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }
            }
            catch
            {
                return "";
            }
        }
    }

    public class TankInfo
    {
        public string Name;
        public string BlueprintPath;
        public string ImagePath;
        public string Hash;
    }

}
