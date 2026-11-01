using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CapriDesktop
{
    // Updates that install themselves on Linux and Mac: the release's file, matched against its SHA256SUMS.txt, unpacked beside
    // her, then swapped in. A running program's file can be renamed on both, so she carries on until she starts the new one.
    internal static class Updater
    {
        private const string Site = "https://github.com/tuno473/capri-desktop/releases/download/";

        internal static string Asset(bool mac, System.Runtime.InteropServices.Architecture chip)
        {
            return !mac ? "Capri-linux-x64.tar.gz" : chip == System.Runtime.InteropServices.Architecture.Arm64 ? "Capri-mac-apple-silicon.zip" : "Capri-mac-intel.zip";
        }

        // What the app sits in: Capri.app on Mac, her folder on Linux. Null when it can't be replaced from here.
        internal static string Home(string program, bool mac)
        {
            if (string.IsNullOrEmpty(program)) return null;
            if (!mac) return Path.GetDirectoryName(program);
            Match app = Regex.Match(program, @"^(.*\.app)/Contents/MacOS/[^/]+$");
            return app.Success ? app.Groups[1].Value : null;
        }

        internal static string Sum(string sums, string name)
        {
            foreach (string row in (sums ?? "").Split('\n'))
            {
                Match line = Regex.Match(row.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                if (line.Success && line.Groups[2].Value == name) return line.Groups[1].Value.ToLowerInvariant();
            }
            return null;
        }

        internal static string Hash(string file)
        {
            using (FileStream stream = File.OpenRead(file))
            using (SHA256 sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        // Downloads and installs, and says where the new program is. Throws with the reason, with nothing changed.
        internal static string Install(string tag, string program)
        {
            bool mac = OperatingSystem.IsMacOS();
            string home = Home(program, mac);
            if (home == null) throw new IOException("I can't update myself from here. Her download page has the new one.");
            string name = Asset(mac, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            string work = home.TrimEnd('/') + ".update";
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            try
            {
                string archive = Path.Combine(work, name);
                using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Capri");
                    string sums = client.GetStringAsync(Site + tag + "/SHA256SUMS.txt").GetAwaiter().GetResult();
                    using (Stream from = client.GetStreamAsync(Site + tag + "/" + name).GetAwaiter().GetResult())
                    using (FileStream to = File.Create(archive)) from.CopyTo(to);
                    return Apply(archive, Sum(sums, name), home, work, mac, program);
                }
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); }
                catch (IOException) { }
            }
        }

        internal static string Apply(string archive, string sum, string home, string work, bool mac, string program)
        {
            if (sum == null || Hash(archive) != sum) throw new IOException("The update's download didn't match its checksum, so I left everything as it was.");
            string unpacked = Path.Combine(work, "unpacked");
            Directory.CreateDirectory(unpacked);
            string[] command = mac ? new[] { "ditto", "-x", "-k", archive, unpacked } : new[] { "tar", "-xzf", archive, "-C", unpacked };
            string[] rest = new string[command.Length - 1];
            Array.Copy(command, 1, rest, 0, rest.Length);
            Music.RunFor(command[0], rest, 180000);
            string fresh = Path.Combine(unpacked, mac ? "Capri.app" : "Capri");
            string inside = mac ? Path.Combine(fresh, "Contents", "MacOS", "Capri") : Path.Combine(fresh, "Capri");
            if (!File.Exists(inside)) throw new IOException("The update's download was missing Capri, so I left everything as it was.");
            if (mac)
            {
                string old = home.TrimEnd('/') + ".old";
                if (Directory.Exists(old)) Directory.Delete(old, true);
                Directory.Move(home, old);
                try { Directory.Move(fresh, home); }
                catch (Exception) { Directory.Move(old, home); throw; }
                try { Directory.Delete(old, true); }
                catch (IOException) { }
                return Path.Combine(home, "Contents", "MacOS", "Capri");
            }
            foreach (string file in Directory.GetFiles(fresh))
            {
                string target = Path.Combine(home, Path.GetFileName(file));
                string moved = target + ".old";
                if (File.Exists(moved)) File.Delete(moved);
                if (File.Exists(target)) File.Move(target, moved);
                File.Move(file, target);
                try { if (File.Exists(moved)) File.Delete(moved); }
                catch (IOException) { }
            }
            return Path.Combine(home, "Capri");
        }
    }
}
