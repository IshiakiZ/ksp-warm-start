using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Expansions;
using UnityEngine;

namespace WarmStart
{
    /// <summary>
    /// The expansions' files, kept unpacked.
    ///
    /// Making History and Breaking Ground each keep their models, scenes and launch sites in "asset bundles":
    /// makinghistory_assets (133 MB), makinghistory_scene (34 MB) and serenity_assets (288 MB). Each is packed
    /// as ONE block of LZMA, which packs small and cannot be read in part: to use anything in it, all of it
    /// has to be unpacked. The game does that at every start: it reads each file whole into memory, takes its
    /// checksum, and has Unity unpack it there (1.2 GB comes out). Timed on the Mac this was made on: 5.6
    /// seconds for Making History and 11.5 for Breaking Ground, a third of the whole start.
    ///
    /// Unity can also keep a bundle in blocks of LZ4, which are read straight off the disk, a block at a time,
    /// as each thing in the bundle is wanted: opening such a bundle takes no time to speak of. And Unity will
    /// make that kind out of the other (AssetBundle.RecompressAssetBundleAsync). So a copy of each of the
    /// three is made that way, once, and kept; and at the very start of every later run the game is handed
    /// the copies, already open.
    ///
    /// Handing them over needs none of the game's code changed. Its loader keeps a list of the bundles it has
    /// loaded (Expansions.BundleLoader.loadedBundles, with each one's checksum) and skips any that is already
    /// on the list: "already loaded - skipping...", its log says. The list is open to mods. So the copies are
    /// put on it before the game gets that far, each with the checksum of the game's OWN file, taken when the
    /// copy was made. The game then checks that checksum against the publisher's signature exactly as it
    /// always does, so an expansion whose files are not the genuine ones is turned away as before.
    ///
    /// A copy is used only while the game's own file is the size and has the date it had when the copy was
    /// made; anything else and the game loads its own file as ever, and a new copy is made.
    /// </summary>
    static class Bundles
    {
        sealed class Kept
        {
            public string Of;              // the game's own file, from the game's folder
            public string Copy;            // the copy's file name, in the mod's folder
            public long Size, Stamp, CopySize;
            public string Sum;             // the checksum of the game's own file (MD5, as the game takes it), in hex
        }

        static readonly List<Kept> kept = new List<Kept>();
        static readonly List<string> wanted = new List<string>();          // the game's files there is no good copy of
        static bool read;
        static int redated;
        public static string Report = "";
        /// <summary>Whether there are any expansions' files here at all.</summary>
        public static bool Any { get; private set; }

        static string Index => Warm.Folder + "bundles.cfg";
        static string Root => KSPUtil.ApplicationRootPath;

        static void Read()
        {
            if (read) return;
            read = true;
            kept.Clear();
            if (!File.Exists(Index)) return;
            ConfigNode file = ConfigNode.Load(Index);
            if (file == null) return;
            foreach (ConfigNode n in file.GetNodes("BUNDLE"))
            {
                var k = new Kept { Of = n.GetValue("of"), Copy = n.GetValue("copy"), Sum = n.GetValue("md5") };
                if (string.IsNullOrEmpty(k.Of) || string.IsNullOrEmpty(k.Copy) || string.IsNullOrEmpty(k.Sum) || k.Sum.Length != 32) continue;
                if (!long.TryParse(n.GetValue("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out k.Size)) continue;
                if (!long.TryParse(n.GetValue("stamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out k.Stamp)) continue;
                if (!long.TryParse(n.GetValue("copy_size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out k.CopySize)) continue;
                // (a copy made by another version of Unity is not one this game can be trusted to read)
                if (n.GetValue("unity") != Application.unityVersion) continue;
                kept.Add(k);
            }
        }

        static void Write()
        {
            var file = new ConfigNode();
            foreach (Kept k in kept)
            {
                ConfigNode n = file.AddNode("BUNDLE");
                n.AddValue("of", k.Of); n.AddValue("copy", k.Copy);
                n.AddValue("size", k.Size.ToString(CultureInfo.InvariantCulture)); n.AddValue("stamp", k.Stamp.ToString(CultureInfo.InvariantCulture));
                n.AddValue("copy_size", k.CopySize.ToString(CultureInfo.InvariantCulture)); n.AddValue("md5", k.Sum);
                n.AddValue("unity", Application.unityVersion);
            }
            Directory.CreateDirectory(Warm.Folder);
            file.Save(Index);
        }

        /// <summary>The copy of this file of the game's, if there is one and the game's file is still what it was copied from.</summary>
        static Kept Good(string original)
        {
            string of = Relative(original);
            Kept k = kept.Find(x => x.Of == of);
            if (k == null) return null;
            var its = new FileInfo(original);
            if (!its.Exists || its.Length != k.Size) return null;
            long stamp = its.LastWriteTimeUtc.Ticks;
            if (stamp != k.Stamp)
            {
                // (the same size at another date: what is in the file decides, by the checksum taken of it when the copy was made)
                string sum;
                try { sum = BitConverter.ToString(Warm.Sum(original)).Replace("-", "").ToLowerInvariant(); }
                catch (Exception) { return null; }
                if (sum != k.Sum) return null;
                k.Stamp = stamp; redated++;
            }
            var copy = new FileInfo(Warm.Folder + k.Copy);
            return copy.Exists && copy.Length == k.CopySize ? k : null;
        }

        static string Relative(string path) => path.Replace('\\', '/').Substring(Root.Replace('\\', '/').Length);

        static byte[] Bytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b;
        }

        /// <summary>Every bundle of every expansion that is installed: the game's own files.</summary>
        static List<string> Theirs()
        {
            var found = new List<string>();
            string all = KSPExpansionsUtils.ExpansionsGameDataPath;
            if (!Directory.Exists(all)) return found;
            foreach (string master in Directory.GetFiles(all, "*" + ExpansionsLoader.expansionsMasterExtension, SearchOption.AllDirectories))
            {
                string bundles = Path.Combine(Path.GetDirectoryName(master), "AssetBundles");
                if (!Directory.Exists(bundles)) continue;
                foreach (string file in Directory.GetFiles(bundles))
                {
                    // (a bundle, by its first seven bytes; anything else in that folder is not ours to touch)
                    try
                    {
                        using (FileStream s = File.OpenRead(file))
                        {
                            var head = new byte[7];
                            if (s.Read(head, 0, 7) == 7 && System.Text.Encoding.ASCII.GetString(head) == "UnityFS") found.Add(file.Replace('\\', '/'));
                        }
                    }
                    catch (Exception) { }
                }
            }
            return found;
        }

        /// <summary>
        /// At the very start: open every good copy and put it on the game's list of bundles it has loaded, as the game itself
        /// would have (the same names, the same list of what is in each, the checksum of the game's own file). True if any was.
        /// </summary>
        public static bool Offer()
        {
            Read();
            wanted.Clear();
            int handed = 0; long bytes = 0;
            float began = Time.realtimeSinceStartup;
            foreach (string original in Theirs())
            {
                Any = true;
                string name = Path.GetFileName(original);
                if (BundleLoader.IsBundleLoaded(name)) continue;
                Kept k = Good(original);
                if (k == null) { wanted.Add(original); continue; }
                AssetBundle bundle = AssetBundle.LoadFromFile(Warm.Folder + k.Copy);
                if (bundle == null || bundle.name != name)
                {
                    // (not a copy the game can use after all: it is forgotten, and made again later)
                    if (bundle != null) bundle.Unload(true);
                    kept.Remove(k); wanted.Add(original);
                    Warm.Log("the copy of " + name + " could not be opened: the game will load its own file, and the copy will be made again");
                    continue;
                }
                foreach (string asset in bundle.GetAllAssetNames())
                    if (!BundleLoader.loadedAssets.ContainsKey(asset)) BundleLoader.loadedAssets.Add(asset, new BundleLoader.ABAssetInfo(asset, name, false));
                foreach (string scene in bundle.GetAllScenePaths())
                    if (!BundleLoader.loadedAssets.ContainsKey(scene)) BundleLoader.loadedAssets.Add(scene, new BundleLoader.ABAssetInfo(scene, name, true));
                // (the folder as the game's own loader names it, for whatever reads it afterwards)
                string folder = KSPUtil.ApplicationFileProtocol + Path.GetDirectoryName(original).Replace('\\', '/') + "/";
                BundleLoader.loadedBundles.Add(name, new BundleLoader.ABInfo(bundle, folder + name, Bytes(k.Sum)));
                handed++; bytes += k.Size;
            }
            if (redated > 0)
            {
                try { Write(); } catch (Exception) { }
                Warm.Log(redated + " of the expansions' files have another date than when they were copied, and the same contents: the copies stand");
                redated = 0;
            }
            Report = handed > 0 ? handed + " of the expansions' files (" + (bytes / 1e6).ToString("F0") + " MB as the game keeps them) were handed to the game ready unpacked."
                   : wanted.Count > 0 ? "No copies of the expansions' files are kept yet." : "No expansions are installed: nothing to keep.";
            if (handed > 0) Warm.Log(handed + " expansion bundles handed over ready unpacked, in " + ((Time.realtimeSinceStartup - began) * 1000f).ToString("F0") + " ms" + (wanted.Count > 0 ? "; " + wanted.Count + " still to be copied" : ""));
            return handed > 0;
        }

        /// <summary>
        /// At the main menu: make the copies that are missing, one at a time, in the background. Each is the game's own file
        /// read once for its checksum (on another thread) and once by Unity, which unpacks it and packs it again in blocks.
        /// </summary>
        public static IEnumerator Make(Action<string> says)
        {
            if (wanted.Count == 0) yield break;
            yield return new WaitForSecondsRealtime(2f);                   // (let the menu settle first)
            Directory.CreateDirectory(Warm.Folder);
            var todo = new List<string>(wanted);
            int made = 0;
            float began = Time.realtimeSinceStartup;
            for (int i = 0; i < todo.Count; i++)
            {
                string original = todo[i], name = Path.GetFileName(original);
                string label = "Warm Start is keeping a copy of the expansions' files, once (" + (i + 1) + " of " + todo.Count + ", " + name + "): the next start will be faster.";
                says(label);
                var its = new FileInfo(original);
                if (!its.Exists) continue;
                long size = its.Length, stamp = its.LastWriteTimeUtc.Ticks;

                // ---- the checksum the game takes of its own file
                byte[] sum = null; bool done = false;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { using (MD5 md5 = MD5.Create()) using (FileStream s = File.OpenRead(original)) sum = md5.ComputeHash(s); }
                    catch (Exception) { sum = null; }
                    done = true;
                });
                while (!done) yield return null;
                if (sum == null) { Warm.Log(name + " could not be read: no copy made"); continue; }

                // ---- the copy
                string copy = name + ".unpacked", making = Warm.Folder + copy + ".making";
                try { if (File.Exists(making)) File.Delete(making); } catch (Exception) { }
                AssetBundleRecompressOperation work = AssetBundle.RecompressAssetBundleAsync(original, making, BuildCompression.LZ4Runtime, 0, UnityEngine.ThreadPriority.Low);
                while (!work.isDone)
                {
                    says(label + "  " + Mathf.RoundToInt(100f * work.progress) + "%");
                    yield return null;
                }
                if (!work.success || !File.Exists(making))
                {
                    Warm.Log("no copy of " + name + " could be made: " + work.humanReadableResult + " (" + work.result + ")");
                    try { if (File.Exists(making)) File.Delete(making); } catch (Exception) { }
                    continue;
                }
                // (the game's file must be what it was when this began, or the copy is of something else)
                its.Refresh();
                if (!its.Exists || its.Length != size || its.LastWriteTimeUtc.Ticks != stamp) { try { File.Delete(making); } catch (Exception) { } continue; }
                try
                {
                    if (File.Exists(Warm.Folder + copy)) File.Delete(Warm.Folder + copy);
                    File.Move(making, Warm.Folder + copy);
                }
                catch (Exception ex) { Warm.Log("the copy of " + name + " could not be put in place (" + ex.Message + ")"); continue; }
                string of = Relative(original);
                kept.RemoveAll(x => x.Of == of);
                kept.Add(new Kept { Of = of, Copy = copy, Size = size, Stamp = stamp, CopySize = new FileInfo(Warm.Folder + copy).Length, Sum = BitConverter.ToString(sum).Replace("-", "").ToLowerInvariant() });
                Write();
                wanted.Remove(original);
                made++;
            }
            says("");
            long disk = 0;
            foreach (Kept k in kept) disk += k.CopySize;
            Warm.Log(made + " of " + todo.Count + " copies made in " + (Time.realtimeSinceStartup - began).ToString("F0") + " s; " + (disk / 1e6).ToString("F0") + " MB kept on the disk");
            Report = made > 0 ? "Copies of " + made + " of the expansions' files were made this time (" + (disk / 1e6).ToString("F0") + " MB on the disk): they will be handed to the game from the next start on."
                   : "The copies of the expansions' files could not be made: the game loads its own, as ever. (The game's log says why.)";
        }
    }
}
