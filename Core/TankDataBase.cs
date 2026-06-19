using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SprocketMultiplayer.Core {
    public static class TankDatabase {
        public static List<TankInfo> LoadTanks() {
            List<TankInfo> list = new List<TankInfo>();

            string basePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games/Sprocket/Factions/AllowedVehicles/Blueprints/Vehicles"
            );

            string bpDir = basePath;
            string imgDir = Path.Combine(basePath, "Profiles");

            if (!Directory.Exists(bpDir)) return list;

            foreach (string bp in Directory.GetFiles(bpDir, "*.blueprint", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(bp);
                string png = Path.Combine(imgDir, name + ".png");

                list.Add(new TankInfo
                {
                    Name = name,
                    BlueprintPath = bp,
                    ImagePath = File.Exists(png) ? png : null,
                    Hash = ComputeFileHash(bp)
                });
            }

            return list;
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
