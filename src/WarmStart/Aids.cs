#if DEV
using System.Collections.Generic;
using KSPAssets;
using KSPAssets.KSPedia;
using KSPAssets.Loaders;

namespace WarmStart
{
    /// <summary>For testing, in the development build only (called through the bridge's dev_call).</summary>
    public static class Aids
    {
        /// <summary>Opens the game's manual, whose pages are in the small bundles: the test that their kept descriptions serve.</summary>
        public static string Manual()
        {
            KSPediaSpawner.Show();
            return "the manual is asked for; the game's reader of bundles is " + (AssetLoader.Ready ? "ready" : "not ready") + ", with " + AssetLoader.AssetDefinitions.Count + " assets described and " + AssetLoader.LoadedBundles.Count + " bundles open";
        }

        /// <summary>(For finding where a part's icon's time goes: the steps of the game's own CreatePartIcon, done again for the
        /// first N parts loaded, each timed: the copy made, woken, its modules told, their parts stripped, and thrown away.)</summary>
        public static string IconSteps(string arg)
        {
            int n = string.IsNullOrEmpty(arg) ? 100 : int.Parse(arg);
            const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            System.Reflection.MethodInfo strip1 = null, strip3 = null, stripGo = null;
            foreach (System.Reflection.MethodInfo m in typeof(PartLoader).GetMethods(Any))
            {
                if (m.Name == "StripComponent" && m.GetParameters().Length == 1) strip1 = m;
                if (m.Name == "StripComponent" && m.GetParameters().Length == 3) strip3 = m;
                if (m.Name == "StripGameObject") stripGo = m;
            }
            if (strip1 == null || strip3 == null || stripGo == null) return "the game's strip methods were not found";
            var watch = new System.Diagnostics.Stopwatch();
            double copy = 0, wake = 0, tell = 0, strip = 0, gone = 0; int done = 0, modules = 0;
            System.Type[] ones = { typeof(Part), typeof(PartModule), typeof(EffectBehaviour), typeof(UnityEngine.Collider), typeof(UnityEngine.WheelCollider), typeof(SmokeTrailControl), typeof(FXPrefab), typeof(UnityEngine.ParticleSystem), typeof(UnityEngine.Light), typeof(UnityEngine.Animation), typeof(DAE) };
            System.Type[] hidden = { typeof(UnityEngine.MeshRenderer), typeof(UnityEngine.MeshFilter), typeof(UnityEngine.SkinnedMeshRenderer) };
            foreach (AvailablePart ap in PartLoader.LoadedPartsList)
            {
                if (done >= n) break;
                if (ap?.partPrefab == null) continue;
                watch.Restart();
                UnityEngine.GameObject g = UnityEngine.Object.Instantiate(ap.partPrefab.gameObject);
                copy += watch.Elapsed.TotalMilliseconds; watch.Restart();
                g.SetActive(true);
                wake += watch.Elapsed.TotalMilliseconds; watch.Restart();
                Part p = g.GetComponent<Part>();
                if (p != null) for (int i = 0; i < p.Modules.Count; i++) { p.Modules[i].OnIconCreate(); modules++; }
                tell += watch.Elapsed.TotalMilliseconds; watch.Restart();
                foreach (System.Type t in ones)
                {
                    if (t == typeof(UnityEngine.Collider)) stripGo.MakeGenericMethod(t).Invoke(PartLoader.Instance, new object[] { g, "collider" });
                    strip1.MakeGenericMethod(t).Invoke(PartLoader.Instance, new object[] { g });
                }
                foreach (System.Type t in hidden) strip3.MakeGenericMethod(t).Invoke(PartLoader.Instance, new object[] { g, "Icon_Hidden", true });
                strip += watch.Elapsed.TotalMilliseconds; watch.Restart();
                UnityEngine.Object.DestroyImmediate(g);
                gone += watch.Elapsed.TotalMilliseconds;
                done++;
            }
            return done + " parts (" + modules + " modules), ms each: copy " + (copy / done).ToString("F3") + ", wake " + (wake / done).ToString("F3") + ", modules told " + (tell / done).ToString("F3") + ", strip " + (strip / done).ToString("F3") + ", thrown away " + (gone / done).ToString("F3");
        }

        /// <summary>(For the icons: what a part's copy costs whole, against its root's children copied one by one, and what is
        /// on the parts' roots and under them that a copy of the children alone would have to see to.)</summary>
        public static string Copies(string arg)
        {
            var watch = new System.Diagnostics.Stopwatch();
            var holder = new UnityEngine.GameObject("aid holder"); holder.SetActive(false);
            double whole = 0, children = 0; int done = 0, one = 0, outside = 0;
            var rootKinds = new Dictionary<string, int>(); var childNames = new Dictionary<string, int>(); var kept = new Dictionary<string, int>();
            foreach (AvailablePart ap in PartLoader.LoadedPartsList)
            {
                if (ap?.partPrefab == null) continue;
                UnityEngine.GameObject g = ap.partPrefab.gameObject;
                watch.Restart();
                UnityEngine.GameObject c = UnityEngine.Object.Instantiate(g, holder.transform, false);
                whole += watch.Elapsed.TotalMilliseconds;
                UnityEngine.Object.DestroyImmediate(c);
                watch.Restart();
                for (int i = 0; i < g.transform.childCount; i++) { UnityEngine.GameObject k = UnityEngine.Object.Instantiate(g.transform.GetChild(i).gameObject, holder.transform, false); UnityEngine.Object.DestroyImmediate(k); }
                children += watch.Elapsed.TotalMilliseconds;
                done++;
                if (g.transform.childCount == 1) one++;
                for (int i = 0; i < g.transform.childCount; i++) { string nm = g.transform.GetChild(i).name; childNames[nm] = childNames.TryGetValue(nm, out int v) ? v + 1 : 1; }
                foreach (UnityEngine.Component comp in g.GetComponents<UnityEngine.Component>())
                {
                    string nm = comp == null ? "missing" : comp is PartModule ? "(module)" : comp.GetType().Name;
                    rootKinds[nm] = rootKinds.TryGetValue(nm, out int v) ? v + 1 : 1;
                }
                bool far = false;
                foreach (UnityEngine.Component comp in g.GetComponentsInChildren<UnityEngine.Component>(true))
                {
                    if (comp == null || comp.transform == g.transform) continue;
                    string nm = comp.GetType().Name;
                    if (!(comp is UnityEngine.Transform || comp is UnityEngine.MeshFilter || comp is UnityEngine.MeshRenderer || comp is UnityEngine.SkinnedMeshRenderer)) kept[nm] = kept.TryGetValue(nm, out int v) ? v + 1 : 1;
                    UnityEngine.Transform top = comp.transform; while (top.parent != g.transform) top = top.parent;
                    if (comp is UnityEngine.SkinnedMeshRenderer smr)
                    {
                        foreach (UnityEngine.Transform b in smr.bones) if (b != null && !b.IsChildOf(top)) far = true;
                        if (smr.rootBone != null && !smr.rootBone.IsChildOf(top)) far = true;
                    }
                    if (comp is UnityEngine.Renderer r && r.probeAnchor != null && !r.probeAnchor.IsChildOf(top)) far = true;
                }
                if (far) outside++;
            }
            UnityEngine.Object.DestroyImmediate(holder);
            string Top(Dictionary<string, int> d, int n) { var l = new List<KeyValuePair<string, int>>(d); l.Sort((x, y) => y.Value.CompareTo(x.Value)); var s = new List<string>(); for (int i = 0; i < l.Count && i < n; i++) s.Add(l[i].Key + " " + l[i].Value); return string.Join(", ", s.ToArray()); }
            return done + " parts: copied whole " + (whole / done).ToString("F3") + " ms each, their root's children one by one " + (children / done).ToString("F3") + " ms; " + one + " with one child under the root; "
                 + outside + " with a skinned mesh or a probe anchor reaching outside its own child\nchildren of the root: " + Top(childNames, 12) + "\non the root: " + Top(rootKinds, 12) + "\nbelow it, besides meshes: " + Top(kept, 25);
        }

        /// <summary>Where every launch site is: for comparing one start with another.</summary>
        public static string Sites()
        {
            var lines = new List<string>();
            if (PSystemSetup.Instance == null) return "no launch sites yet";
            foreach (LaunchSite site in PSystemSetup.Instance.LaunchSites)
            {
                UnityEngine.Transform t = site.pqsCity != null ? site.pqsCity.transform : site.pqsCity2 != null ? site.pqsCity2.transform : null;
                lines.Add(site.name + ": " + (t != null ? t.localPosition.ToString("F3") + " " + t.localRotation.eulerAngles.ToString("F3") : "(no place)"));
            }
            return string.Join("\n", lines.ToArray());
        }
    }
}
#endif
