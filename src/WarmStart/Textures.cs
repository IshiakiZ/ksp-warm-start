using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace WarmStart
{
    /// <summary>
    /// Textures, handed to the game ready made.
    ///
    /// The game loads its textures one file at a time, each from start to finish before the next is begun, all
    /// on the one thread that also draws the screen. Timed on the Mac this was made on (1677 texture files):
    ///
    ///   974 DDS files, 1.3 GB      2.1 seconds   2 ms each: read, and handed to the graphics card as they are
    ///   666 PNG files, 130 MB      8.0 seconds  12 ms each: unpacked, squeezed into the card's own format,
    ///                                           afresh at every start
    ///
    /// A tenth of the data takes four fifths of the time, and comes out the same every time. So:
    ///
    /// * What the game makes of each PNG is kept (its finished pixels, as the card takes them), in one file,
    ///   from the first start on. At later starts those are read back and given to the card as they are: no
    ///   unpacking, no squeezing.
    /// * DDS files need no keeping, being finished already, but they are read by several threads at once, ahead
    ///   of the thread that hands them over, which then only hands over.
    ///
    /// Handing them to the game needs none of its code changed. For every texture file the game first asks its
    /// own list (GameDatabase.databaseTexture) whether a texture of that name is there already, and whether the
    /// file is newer than the last loading; if it is there and not newer, the file is passed over. That is how
    /// the game reloads only what has changed when it is asked to reload, and the list is open to mods. So just
    /// before the game reaches its textures they are put on the list, each exactly as the game's own loader
    /// would have left it (the same size, format, smaller copies, and the same three things the game notes of
    /// each: whether it is a normal map, whether its pixels can still be read, whether it counts as squeezed),
    /// and each file that was dealt with is given, for as long as the game is going through them, the date the
    /// game gives a file whose date it cannot read: the year 1, which is newer than nothing.
    ///
    /// What the game would have done is kept to in the corners too:
    /// * Where two files make the same name (Squad ships one such pair, a PNG and a DDS of the same picture),
    ///   the game ends up with the later one. So does this.
    /// * Where a mod brings its own loader for PNG or DDS files, the game would have run that as well as its
    ///   own. Files of that kind are then left to the game.
    /// * The list ends in the order the game would have made it in.
    ///
    /// A kept picture is used only while its file is the size and has the date it had; any that is not, the
    /// game loads itself as ever, and what it makes is kept in turn. The kinds this does not know (TGA, JPG,
    /// MBM, and DDS in formats the game itself refuses) are left to the game.
    /// </summary>
    static class Textures
    {
        public static string Report = "";
        /// <summary>How many kept pictures were handed over at this start.</summary>
        public static int Kept { get; private set; }

        // ---------------------------------------------------------------- what is kept of each PNG

        sealed class Picture
        {
            public string Url;
            public long SrcSize, SrcStamp;          // the PNG file it was made from
            public int Width, Height, Format, Mips, Wrap, Filter, Aniso;
            public byte Flags;                      // 1: a normal map, 2: its pixels can be read, 4: counts as squeezed
            public long At; public int Length;      // where its pixels are in the file of them
            public byte[] Sum;                      // what is in the PNG file (see Warm.Sum)
            public string Source;                   // (the PNG file itself, while it is being kept)
        }

        const string Sign = "WarmStartTextures3";
        static readonly Dictionary<string, Picture> kept = new Dictionary<string, Picture>();
        static string Data => Warm.Folder + "textures.data";
        static string List => Warm.Folder + "textures.list";
        static long dataSize;

        static void Read()
        {
            kept.Clear();
            dataSize = 0;
            try
            {
                if (!File.Exists(List) || !File.Exists(Data)) return;
                dataSize = new FileInfo(Data).Length;
                using (var r = new BinaryReader(File.OpenRead(List)))
                {
                    if (r.ReadString() != Sign || r.ReadString() != Application.unityVersion) return;
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        var k = new Picture
                        {
                            Url = r.ReadString(), SrcSize = r.ReadInt64(), SrcStamp = r.ReadInt64(), Width = r.ReadInt32(), Height = r.ReadInt32(), Format = r.ReadInt32(), Mips = r.ReadInt32(),
                            Wrap = r.ReadInt32(), Filter = r.ReadInt32(), Aniso = r.ReadInt32(), Flags = r.ReadByte(), At = r.ReadInt64(), Length = r.ReadInt32(), Sum = r.ReadBytes(16),
                        };
                        if (k.At >= 0 && k.Length > 0 && k.At + k.Length <= dataSize && k.Width > 0 && k.Height > 0) kept[k.Url] = k;
                    }
                }
            }
            catch (Exception) { kept.Clear(); }
        }

        static void Write(List<Picture> all, string to)
        {
            using (var w = new BinaryWriter(File.Create(to)))
            {
                w.Write(Sign); w.Write(Application.unityVersion); w.Write(all.Count);
                foreach (Picture k in all)
                {
                    w.Write(k.Url); w.Write(k.SrcSize); w.Write(k.SrcStamp); w.Write(k.Width); w.Write(k.Height); w.Write(k.Format); w.Write(k.Mips);
                    w.Write(k.Wrap); w.Write(k.Filter); w.Write(k.Aniso); w.Write(k.Flags); w.Write(k.At); w.Write(k.Length); w.Write(k.Sum != null && k.Sum.Length == 16 ? k.Sum : new byte[16]);
                }
            }
        }

        // ---------------------------------------------------------------- when

        static UrlDir first;
        static int stage = -1;

        /// <summary>At the very start, while the game is starting its plugins.</summary>
        public static void Begin()
        {
            if (stage >= 0) return;
            first = GameDatabase.Instance != null ? GameDatabase.Instance.root : null;
            stage = 0;
        }

        public static bool Busy => stage >= 0 && stage < 3;

        /// <summary>Every frame from then on, until there is no more to do.</summary>
        public static void Step()
        {
            if (!Busy) return;
            GameDatabase db = GameDatabase.Instance;
            if (db == null) return;
            if (stage == 0)
            {
                // The game lists its files twice: once to find the plugins, and again once those are started, with the kinds
                // of file their loaders take. Only in the second are textures known as textures. It then loads its sounds,
                // each of which takes a frame or more: so this comes round while it is at them, before it is at its textures.
                if (db.root == null || ReferenceEquals(db.root, first)) return;
                first = null;
                stage = 1;
                Offer(db);
            }
            else if (stage == 1)
            {
                // (the game has loaded a model, or is through altogether: so it is through its textures)
                if (db.databaseModel.Count == 0 && !db.IsReady()) return;
                stage = 2;
                Through(db);
            }
            else if (!Down()) stage = 3;
        }

        // ---------------------------------------------------------------- handing over

        sealed class Job
        {
            public UrlDir.UrlFile File; public string Url;
            public Picture From;                       // (a kept PNG; nothing for a DDS file)
            public byte[] Buffer; public int Start, Length;
            public int Width, Height; public TextureFormat Format; public bool Mips, Normal;
            public bool Failed, Done;
        }

        // The files are read into buffers that are used again and again (a new one for each of a thousand files would be a
        // gigabyte of rubbish for the game to carry for the rest of its run), and no more is read ahead of the handing over
        // than MostHeld: except the one the handing over is waiting for, which is always let through.
        const long MostHeld = 128L << 20;
        static readonly object gate = new object();
        static readonly Dictionary<int, Stack<byte[]>> pool = new Dictionary<int, Stack<byte[]>>();
        static long held;
        static int serving, next;
        static volatile bool stop;
        static List<Job> jobs;

        static byte[] Borrow(int size, int index)
        {
            int room = 4096;
            while (room < size) room <<= 1;
            lock (gate)
            {
                while (held > 0 && held + room > MostHeld && index > serving && !stop) Monitor.Wait(gate);
                held += room;
                if (pool.TryGetValue(room, out Stack<byte[]> some) && some.Count > 0) return some.Pop();
            }
            return new byte[room];
        }

        static void Return(byte[] buffer)
        {
            lock (gate)
            {
                held -= buffer.Length;
                if (!pool.TryGetValue(buffer.Length, out Stack<byte[]> some)) pool[buffer.Length] = some = new Stack<byte[]>();
                if (some.Count < 4) some.Push(buffer);
                Monitor.PulseAll(gate);
            }
        }

        static void Fill(Stream from, byte[] into, int count)
        {
            int at = 0;
            while (at < count)
            {
                int got = from.Read(into, at, count - at);
                if (got <= 0) throw new EndOfStreamException();
                at += got;
            }
        }

        static void Reading()
        {
            FileStream data = null;
            try
            {
                while (!stop)
                {
                    int i = Interlocked.Increment(ref next);
                    if (i >= jobs.Count) break;
                    Job job = jobs[i];
                    try
                    {
                        if (job.From != null)
                        {
                            if (data == null) data = new FileStream(Data, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                            job.Buffer = Borrow(job.From.Length, i);
                            data.Position = job.From.At;
                            Fill(data, job.Buffer, job.From.Length);
                            job.Start = 0; job.Length = job.From.Length;
                        }
                        else
                        {
                            using (var f = new FileStream(job.File.fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                            {
                                long length = f.Length;
                                if (length < 128 || length > int.MaxValue) job.Failed = true;
                                else
                                {
                                    job.Buffer = Borrow((int)length, i);
                                    Fill(f, job.Buffer, (int)length);
                                    if (!Dds(job.Buffer, (int)length, job)) job.Failed = true;
                                }
                            }
                        }
                    }
                    catch (Exception) { job.Failed = true; }
                    lock (gate) { job.Done = true; Monitor.PulseAll(gate); }
                }
            }
            finally { if (data != null) data.Dispose(); }
        }

        /// <summary>A DDS file's first 128 bytes, read as the game's own loader reads them. False for what that loader refuses.</summary>
        static bool Dds(byte[] b, int length, Job job)
        {
            if (BitConverter.ToUInt32(b, 0) != 0x20534444u) return false;                                // "DDS "
            uint height = BitConverter.ToUInt32(b, 12), width = BitConverter.ToUInt32(b, 16);
            uint flags = BitConverter.ToUInt32(b, 80), code = BitConverter.ToUInt32(b, 84), caps = BitConverter.ToUInt32(b, 108);
            if (code == 0x31545844u) job.Format = TextureFormat.DXT1;                                    // "DXT1"
            else if (code == 0x35545844u) job.Format = TextureFormat.DXT5;                               // "DXT5"
            else return false;
            if (width == 0 || height == 0 || width > 16384 || height > 16384) return false;
            job.Width = (int)width; job.Height = (int)height;
            job.Mips = (caps & 0x400000u) != 0;
            job.Normal = (flags & 0x80000u) != 0 || (flags & 0x80000000u) != 0;
            job.Start = 128; job.Length = length - 128;
            // (no fewer bytes than a texture of that size and kind has: a file with fewer is the game's to complain of)
            int needs = Bytes(job.Width, job.Height, job.Format == TextureFormat.DXT1 ? 8 : 16, job.Mips);
            return needs > 0 && job.Length >= needs;
        }

        static int Bytes(int w, int h, int block, bool mips)
        {
            long all = 0;
            while (true)
            {
                all += (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block;
                if (!mips || (w == 1 && h == 1)) break;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            return all > int.MaxValue ? -1 : (int)all;
        }

        static readonly List<KeyValuePair<UrlDir.UrlFile, DateTime>> passed = new List<KeyValuePair<UrlDir.UrlFile, DateTime>>();       // files the game is made to pass over, and the dates they had
        static readonly List<UrlDir.UrlFile> files = new List<UrlDir.UrlFile>();                    // every texture file, in the game's order
        static readonly HashSet<UrlDir.UrlFile> left = new HashSet<UrlDir.UrlFile>();               // PNG files the game has to load itself this time
        static readonly List<Picture> used = new List<Picture>();                                          // what was kept, and was handed over this time
        static int handed, redated;

        /// <summary>
        /// Every DDS file and every kept PNG is put on the game's list of textures, in the order the game would have come to
        /// them. Several threads read the files ahead; this one, the game's own, makes the textures of them.
        /// </summary>
        static void Offer(GameDatabase db)
        {
            if (Warm.Dated == null) { Warm.Log("this game does not keep its files' dates where they were expected: textures are left to it"); return; }
            long began = Stopwatch.GetTimestamp();

            // ---- whose loaders there are: where a mod has one of its own for a kind of file, the game would run both, so that kind is the game's
            bool dds = true, png = true;
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
            {
                Type[] types;
                try { types = a.assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch (Exception) { continue; }
                if (types == null) continue;
                foreach (Type t in types)
                {
                    if (t == null || t == typeof(DatabaseLoaderTexture_DDS) || t == typeof(DatabaseLoaderTexture_PNG) || !t.IsSubclassOf(typeof(DatabaseLoader<GameDatabase.TextureInfo>))) continue;
                    foreach (object note in t.GetCustomAttributes(true))
                    {
                        if (!(note is DatabaseLoaderAttrib its) || its.extensions == null) continue;
                        foreach (string kind in its.extensions)
                        {
                            if (kind == "dds" && dds) { dds = false; Warm.Log(t.FullName + " loads DDS files too: those are left to the game"); }
                            if (kind == "png" && png) { png = false; Warm.Log(t.FullName + " loads PNG files too: those are left to the game"); }
                        }
                    }
                }
            }
            if (!dds && !png) return;

            // ---- the files, and for each name the one the game would end up with: the last
            var urls = new List<string>();
            foreach (UrlDir.UrlFile file in db.root.GetFiles(UrlDir.FileType.Texture))
                if (file != null) { files.Add(file); urls.Add(file.url); }
            var last = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Count; i++) last[urls[i]] = i;
            var there = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameDatabase.TextureInfo info in db.databaseTexture) if (info != null && info.name != null) there.Add(info.name);

            Read();
            jobs = new List<Job>();
            int stale = 0;
            for (int i = 0; i < files.Count; i++)
            {
                UrlDir.UrlFile file = files[i];
                if (last[urls[i]] != i || there.Contains(urls[i])) continue;
                if (file.fileExtension == "dds") { if (dds) jobs.Add(new Job { File = file, Url = urls[i] }); }
                else if (file.fileExtension == "png" && png)
                {
                    Picture k = null;
                    try
                    {
                        var its = new FileInfo(file.fullPath);
                        if (kept.TryGetValue(urls[i], out k))
                        {
                            long stamp = its.LastWriteTimeUtc.Ticks;
                            if (its.Length != k.SrcSize) { k = null; stale++; }
                            else if (stamp != k.SrcStamp)
                            {
                                // (the same size at another date: a mod put back in place, a game checked over. What is in the file decides.)
                                if (Warm.Same(Warm.Sum(file.fullPath), k.Sum)) { k.SrcStamp = stamp; redated++; }
                                else { k = null; stale++; }
                            }
                        }
                    }
                    catch (Exception) { k = null; }
                    if (k != null) jobs.Add(new Job { File = file, Url = urls[i], From = k });
                    else left.Add(file);
                }
            }
            if (jobs.Count == 0) { Report = left.Count > 0 ? "No pictures are kept yet." : ""; return; }

            // ---- the reading ahead
            next = -1; serving = 0; held = 0; stop = false;
            int threads = Mathf.Clamp(SystemInfo.processorCount - 1, 2, 8);
            // (set by hand in the mod's settings file, "readers = 1" and up: for a disk that is the slower for being read in several places at once)
            if (int.TryParse(Warm.Page.Kept("readers"), out int asked) && asked >= 1 && asked <= 16) threads = asked;
            var readers = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                readers[t] = new Thread(Reading) { IsBackground = true, Name = "Warm Start reading textures" };
                readers[t].Start();
            }

            // ---- the handing over
            int pngs = 0, refused = 0;
            long bytes = 0, waited = 0, made = 0, filled = 0, sent = 0;
            var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    Job job = jobs[i];
                    long t0 = Stopwatch.GetTimestamp();
                    lock (gate)
                    {
                        serving = i;
                        Monitor.PulseAll(gate);
                        while (!job.Done) Monitor.Wait(gate);
                    }
                    long t1 = Stopwatch.GetTimestamp();
                    waited += t1 - t0;
                    if (!job.Failed)
                    {
                        Texture2D texture = null;
                        try
                        {
                            Picture k = job.From;
                            texture = k != null ? new Texture2D(k.Width, k.Height, (TextureFormat)k.Format, k.Mips > 1) : new Texture2D(job.Width, job.Height, job.Format, job.Mips);
                            long t2 = Stopwatch.GetTimestamp();
                            GCHandle pin = GCHandle.Alloc(job.Buffer, GCHandleType.Pinned);
                            try { texture.LoadRawTextureData(new IntPtr(pin.AddrOfPinnedObject().ToInt64() + job.Start), job.Length); }
                            finally { pin.Free(); }
                            long t3 = Stopwatch.GetTimestamp();
                            GameDatabase.TextureInfo info;
                            if (k == null)
                            {
                                texture.Apply(false, true);
                                info = new GameDatabase.TextureInfo(job.File, texture, job.Normal, false, true);
                            }
                            else
                            {
                                texture.wrapMode = (TextureWrapMode)k.Wrap; texture.filterMode = (FilterMode)k.Filter; texture.anisoLevel = k.Aniso;
                                bool readable = (k.Flags & 2) != 0;
                                texture.Apply(false, !readable);
                                info = new GameDatabase.TextureInfo(job.File, texture, (k.Flags & 1) != 0, readable, (k.Flags & 4) != 0);
                            }
                            long t4 = Stopwatch.GetTimestamp();
                            made += t2 - t1; filled += t3 - t2; sent += t4 - t3;
                            info.name = job.Url;
                            texture.name = job.Url;
                            db.databaseTexture.Add(info);
                            given.Add(job.Url);
                            handed++; bytes += job.Length;
                            if (k != null) { pngs++; used.Add(k); }
                        }
                        catch (Exception)
                        {
                            job.Failed = true;
                            if (texture != null) UnityEngine.Object.Destroy(texture);
                        }
                    }
                    if (job.Failed) { refused++; if (job.From != null) left.Add(job.File); }
                    if (job.Buffer != null) { Return(job.Buffer); job.Buffer = null; }
                }
            }
            finally
            {
                stop = true;
                lock (gate) Monitor.PulseAll(gate);
                foreach (Thread reader in readers) reader.Join(3000);
                lock (gate) { pool.Clear(); held = 0; }
                jobs = null;
            }

            // ---- and the game is made to pass over every file whose name it now has
            for (int i = 0; i < files.Count; i++)
            {
                if (!given.Contains(urls[i])) continue;
                passed.Add(new KeyValuePair<UrlDir.UrlFile, DateTime>(files[i], files[i].fileTime));
                Warm.Dated.SetValue(files[i], Warm.Never);
            }

            Kept = pngs;
            double tick = 1000.0 / Stopwatch.Frequency;
            double took = (Stopwatch.GetTimestamp() - began) * tick;
            Report = handed + " pictures (" + (bytes / 1e6).ToString("F0") + " MB) were handed to the game ready made in " + (took / 1000.0).ToString("F1") + " s: " + (handed - pngs) + " that were read ahead by " + threads + " threads, and " + pngs + " kept from an earlier start."
                   + (left.Count > 0 ? " " + left.Count + " PNG files were not kept yet." : "");
            Warm.Log(handed + " textures (" + (bytes / 1e6).ToString("F0") + " MB) handed over in " + took.ToString("F0") + " ms: " + (handed - pngs) + " DDS read ahead by " + threads + " threads, " + pngs + " PNG from what was kept"
                   + (refused > 0 ? "; " + refused + " left to the game" : "") + (stale > 0 ? "; " + stale + " kept ones out of date" : "") + (redated > 0 ? "; " + redated + " at another date than they were, with the same contents" : "") + "; " + left.Count + " PNG still to keep"
                   + " (waiting for the readers " + (waited * tick).ToString("F0") + " ms, making " + (made * tick).ToString("F0") + ", filling " + (filled * tick).ToString("F0") + ", sending to the card " + (sent * tick).ToString("F0") + ")");
        }

        // ---------------------------------------------------------------- once the game is through its textures

        static List<GameDatabase.TextureInfo> todo;
        static int at, normals;
        static readonly Queue<KeyValuePair<Picture, byte[]>> queue = new Queue<KeyValuePair<Picture, byte[]>>();
        static long queued;
        static bool closed;
        static volatile bool gaveUp;
        const long MostQueued = 96L << 20;

        static void Through(GameDatabase db)
        {
            // (the files have their dates again)
            foreach (KeyValuePair<UrlDir.UrlFile, DateTime> p in passed) Warm.Dated.SetValue(p.Key, p.Value);
            passed.Clear();

            // ---- the list is put in the order the game makes it in: that of the files
            if (handed > 0)
            {
                var order = new Dictionary<UrlDir.UrlFile, int>();
                for (int i = 0; i < files.Count; i++) order[files[i]] = i;
                List<GameDatabase.TextureInfo> list = db.databaseTexture;
                GameDatabase.TextureInfo[] sorted = list.ToArray();
                var keys = new long[sorted.Length];
                for (int i = 0; i < sorted.Length; i++)
                {
                    int place = sorted[i] != null && sorted[i].file != null && order.TryGetValue(sorted[i].file, out int o) ? o + 1 : 0;      // (what is not from a file stays in front, as it was)
                    keys[i] = ((long)place << 32) | (uint)i;
                }
                Array.Sort(keys, sorted);
                for (int i = 0; i < sorted.Length; i++) if (!ReferenceEquals(list[i], sorted[i])) list[i] = sorted[i];
            }

            // ---- what the game made of the PNG files it had to load itself is to be taken down
            todo = new List<GameDatabase.TextureInfo>();
            foreach (GameDatabase.TextureInfo info in db.databaseTexture)
                if (info != null && info.file != null && info.texture != null && left.Contains(info.file)) todo.Add(info);
            left.Clear(); files.Clear();
            long unused = dataSize;
            foreach (Picture k in used) unused -= k.Length;
            // (nothing new, and little kept that is no longer wanted: what is kept stays as it is)
            if (todo.Count == 0 && unused < (32L << 20) && redated == 0) { stage = 3; used.Clear(); return; }
            at = 0; normals = 0; closed = false; queued = 0;
            var before = new List<Picture>(used);
            used.Clear();
            new Thread(() => Keeping(before)) { IsBackground = true, Name = "Warm Start keeping textures" }.Start();
        }

        /// <summary>
        /// A frame's worth of taking down what the game made. Only a texture whose pixels can still be read can be taken down;
        /// a normal map, which the game makes unreadable, is made again here from its file by the game's own rule (green into
        /// red, green and blue; red into the fourth channel). False once there is no more to do.
        /// </summary>
        static bool Down()
        {
            long began = Stopwatch.GetTimestamp();
            // (the thread that writes them out has given up: nothing more is taken down for it)
            if (gaveUp) { lock (queue) queue.Clear(); todo = null; return false; }
            while (at < todo.Count)
            {
                lock (queue) if (queued >= MostQueued) return true;
                if ((Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency > 20.0) return true;
                GameDatabase.TextureInfo info = todo[at++];
                try
                {
                    if (info.texture == null || info.file.fileExtension != "png") continue;
                    var its = new FileInfo(info.file.fullPath);
                    Texture2D t = info.texture;
                    byte[] pixels;
                    var k = new Picture { Url = info.file.url, Source = info.file.fullPath, SrcSize = its.Length, SrcStamp = its.LastWriteTimeUtc.Ticks, Wrap = (int)t.wrapMode, Filter = (int)t.filterMode, Aniso = t.anisoLevel };
                    if (t.isReadable && info.isReadable && !info.isNormalMap)
                    {
                        pixels = t.GetRawTextureData();
                        k.Width = t.width; k.Height = t.height; k.Format = (int)t.format; k.Mips = t.mipmapCount;
                        k.Flags = (byte)(2 | (info.isCompressed ? 4 : 0));
                    }
                    else if (info.isNormalMap && !info.isReadable && t.format == TextureFormat.RGBA32 && Path.GetFileNameWithoutExtension(info.file.fullPath).EndsWith("NRM"))
                    {
                        var from = new Texture2D(2, 2);
                        if (!from.LoadImage(File.ReadAllBytes(info.file.fullPath)) || from.width != t.width || from.height != t.height) { UnityEngine.Object.Destroy(from); continue; }
                        Color32[] p = from.GetPixels32();
                        for (int i = 0; i < p.Length; i++) p[i] = new Color32(p[i].g, p[i].g, p[i].g, p[i].r);
                        var again = new Texture2D(from.width, from.height, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
                        again.SetPixels32(p);
                        again.Apply(true, false);
                        pixels = again.GetRawTextureData();
                        k.Width = again.width; k.Height = again.height; k.Format = (int)again.format; k.Mips = again.mipmapCount;
                        k.Flags = (byte)(1 | (info.isCompressed ? 4 : 0));
                        UnityEngine.Object.Destroy(from); UnityEngine.Object.Destroy(again);
                        normals++;
                    }
                    else continue;
                    if (pixels == null || pixels.Length == 0) continue;
                    k.Length = pixels.Length;
                    lock (queue)
                    {
                        queue.Enqueue(new KeyValuePair<Picture, byte[]>(k, pixels));
                        queued += pixels.Length;
                        Monitor.PulseAll(queue);
                    }
                }
                catch (Exception) { }
            }
            lock (queue) { closed = true; Monitor.PulseAll(queue); }
            todo = null;
            return false;
        }

        /// <summary>On a thread of its own: what was kept and is still wanted, then what is taken down this time, written out as the new file of them.</summary>
        static void Keeping(List<Picture> before)
        {
            string data = Data + ".making", list = List + ".making";
            try
            {
                Directory.CreateDirectory(Warm.Folder);
                var all = new List<Picture>();
                int fresh = 0;
                using (var w = new FileStream(data, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    if (before.Count > 0)
                        using (var r = new FileStream(Data, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
                        {
                            byte[] buffer = new byte[1 << 20];
                            foreach (Picture k in before)
                            {
                                r.Position = k.At;
                                var moved = new Picture { Url = k.Url, SrcSize = k.SrcSize, SrcStamp = k.SrcStamp, Width = k.Width, Height = k.Height, Format = k.Format, Mips = k.Mips, Wrap = k.Wrap, Filter = k.Filter, Aniso = k.Aniso, Flags = k.Flags, At = w.Position, Length = k.Length, Sum = k.Sum };
                                int togo = k.Length;
                                while (togo > 0)
                                {
                                    int got = r.Read(buffer, 0, Math.Min(buffer.Length, togo));
                                    if (got <= 0) throw new EndOfStreamException();
                                    w.Write(buffer, 0, got); togo -= got;
                                }
                                all.Add(moved);
                            }
                        }
                    while (true)
                    {
                        KeyValuePair<Picture, byte[]> one;
                        lock (queue)
                        {
                            while (queue.Count == 0 && !closed) Monitor.Wait(queue);
                            if (queue.Count == 0) break;
                            one = queue.Dequeue();
                        }
                        lock (queue) queued -= one.Value.Length;
                        // (what is in the PNG file it was made from; and if that file is no longer what it was a moment ago, this is not kept)
                        try
                        {
                            one.Key.Sum = Warm.Sum(one.Key.Source);
                            var its = new FileInfo(one.Key.Source);
                            if (its.Length != one.Key.SrcSize || its.LastWriteTimeUtc.Ticks != one.Key.SrcStamp) continue;
                        }
                        catch (Exception) { continue; }
                        one.Key.At = w.Position;
                        w.Write(one.Value, 0, one.Value.Length);
                        all.Add(one.Key); fresh++;
                    }
                }
                Write(all, list);
                // (the list goes last, and comes away first: a list that is there always has its pixels there)
                if (File.Exists(List)) File.Delete(List);
                if (File.Exists(Data)) File.Delete(Data);
                File.Move(data, Data);
                File.Move(list, List);
                long size = new FileInfo(Data).Length;
                if (fresh > 0) Report += " What the game made of the PNG files it had to load itself this time (" + fresh + ") is kept for the next start: " + (size / 1e6).ToString("F0") + " MB kept in all.";
                UnityEngine.Debug.Log(fresh > 0 ? "[Warm Start] what the game made of its PNG files is kept for the next start: " + fresh + " new (" + normals + " of them normal maps, made over), " + all.Count + " in all, " + (size / 1e6).ToString("F0") + " MB"
                                                : "[Warm Start] what is kept of the PNG files is written out again, with their dates as they are now: " + all.Count + " in all, " + (size / 1e6).ToString("F0") + " MB");
            }
            catch (Exception ex)
            {
                gaveUp = true;
                lock (queue) queue.Clear();
                try { if (File.Exists(data)) File.Delete(data); if (File.Exists(list)) File.Delete(list); } catch (Exception) { }
                UnityEngine.Debug.Log("[Warm Start] what the game made of its PNG files could not be kept (" + ex.Message + ")");
            }
        }

        // ---------------------------------------------------------------- for comparing one start with another

        /// <summary>
        /// Every texture the game has, with its size, kind and what the game notes of it, and (where its pixels can be read) a
        /// sum of them, written out in order of name, with the order they are in on the game's list beside it.
        /// </summary>
        public static void Tell(string to)
        {
            var lines = new List<string>();
            int place = 0;
            foreach (GameDatabase.TextureInfo info in GameDatabase.Instance.databaseTexture)
            {
                place++;
                if (info == null || info.texture == null) { lines.Add((info != null ? info.name : "?") + "  (nothing)  #" + place); continue; }
                Texture2D t = info.texture;
                string sum = "";
                if (t.isReadable)
                {
                    // (FNV-1a, 64 bits)
                    byte[] pixels = t.GetRawTextureData();
                    ulong h = 14695981039346656037UL;
                    for (int i = 0; i < pixels.Length; i++) { h ^= pixels[i]; h *= 1099511628211UL; }
                    sum = " sum " + h.ToString("x16") + " of " + pixels.Length;
                }
                lines.Add(info.name + "  " + t.width + "x" + t.height + " " + t.format + " mips " + t.mipmapCount + " wrap " + t.wrapMode + " filter " + t.filterMode + " aniso " + t.anisoLevel
                    + (info.isNormalMap ? " normal" : "") + (info.isReadable ? " readable" : "") + (info.isCompressed ? " squeezed" : "") + " name " + t.name
                    + " file " + (info.file != null ? info.file.name + "." + info.file.fileExtension : "(none)") + sum + "  #" + place);
            }
            lines.Sort(StringComparer.Ordinal);
            File.WriteAllLines(to, lines.ToArray());
        }
    }
}
