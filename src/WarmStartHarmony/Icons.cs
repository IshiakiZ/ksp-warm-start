using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace WarmStartHarmony
{
    /// <summary>
    /// The parts' icons, made without copying and waking what is thrown away. The game makes a part's icon (the little model
    /// that turns in the editor's list) by copying the whole part, awake: the part and every one of its modules set themselves
    /// up in the copy as they would on a craft, and are then taken out of it again, with its colliders, lights, effects and
    /// animations, until only the meshes are left. 1.5 thousandths of a second a part where this was measured, a third of all
    /// the time a part takes to compile; and of the copying alone, nine tenths is the part and its modules.
    ///
    /// Here, where everything on the part's root is of the kinds the game takes out (the part, its modules, its sounds and
    /// effects) and the highlighter that the icon keeps, only what is below the root is copied (the model, mostly), under a
    /// new root of the same name, place and switches, with a new highlighter, and nothing below reaching across to another
    /// child or up to the root. Anywhere else the whole part is copied. Either way the copy is made inside a holder that is
    /// switched off, so that nothing in it wakes; what the game would take out is taken out as the game's own helpers do it;
    /// and only then is the copy let out, for what is left (the meshes) to wake. From there on it is the game's own steps.
    ///
    /// Kept true: a module or component with code of its own for waking (nearly all of it only setting up numbers) could make
    /// the game's icon differ from this one. So every such kind is tried before it is trusted: the first part with it is made
    /// both ways and the two icons compared, every object, mesh, material, position and switch. Where they differ, parts with
    /// that kind are left to the game for the rest of the session (the drills' dust is one: it puts back, as it wakes, what
    /// the game took out). One kind of the game's own is done here as it does it: the part variants make their own copy of each
    /// of the model's materials as they wake, which shows in the icon's materials' names. Trusted kinds are tried again, one
    /// part in 16, and where a comparison is made the game's own icon is the one kept. A difference where none was expected
    /// puts the whole thing away for the session; the mod's page says so.
    ///
    /// What it does not do that the game did: tell the game's listeners that the copy's part was destroyed (the game says so
    /// for every icon it makes; nothing in the game listens for that while it loads).
    /// </summary>
    static class Icons
    {
        const string Id = "warmstart.icons";
        const int Trusted = 1, Never = -1, Again = 16;

        static bool broken, all, inside;
        static string why = "";
        static int seen, fast, light, left, checks, againCount;
        static readonly List<string> differed = new List<string>();

        static MethodInfo original;
        static Func<PartLoader, GameObject, Bounds> bounds;
        static Action<PartLoader, GameObject> materials;
        static GameObject holder;

        /// <summary>For each kind found in a part: 0 needs no trying, Never cannot be made this way, else 1 + the tries it has passed.</summary>
        static readonly Dictionary<Type, int> kinds = new Dictionary<Type, int>();
        static readonly Dictionary<Type, bool> wakes = new Dictionary<Type, bool>();

        // (Unity's calls to a script that could notice when it wakes against when the rest is taken out)
        static readonly string[] messages = { "Awake", "OnEnable", "OnDisable", "OnDestroy", "OnTransformParentChanged", "OnTransformChildrenChanged",
                                              "OnBeforeTransformParentChanged", "OnValidate", "OnDidApplyAnimationProperties", "OnRectTransformDimensionsChange",
                                              "OnCanvasGroupChanged", "OnCanvasHierarchyChanged" };

        public static void Begin()
        {
            if (!WarmStart.Companion.WantsIcons) return;
            all = WarmStart.Companion.Kept("iconcheck") == "all";
            original = AccessTools.Method(typeof(PartLoader), "CreatePartIcon");
            bounds = AccessTools.MethodDelegate<Func<PartLoader, GameObject, Bounds>>(AccessTools.Method(typeof(PartLoader), "GetPartBounds"));
            materials = AccessTools.MethodDelegate<Action<PartLoader, GameObject>>(AccessTools.Method(typeof(PartLoader), "SetPartIconMaterials"));
            if (original == null || bounds == null || materials == null) throw new Exception("the game's part loader is not as it was when this was written");
            var h = new Harmony(Id);
            try { h.UnpatchAll(Id); } catch (Exception) { }
            h.Patch(original, new HarmonyMethod(typeof(Icons).GetMethod(nameof(Make), BindingFlags.Static | BindingFlags.NonPublic)));
            WarmStart.Companion.IconsReport = Report;
            WarmStart.Companion.Log("the parts' icons are made without copying and waking what is thrown away" + (all ? " (every one also made the game's way and compared, as asked)" : ""));
        }

        static string Report()
        {
            if (broken) return "Icons made without waking: put away this session, " + why + " The game makes its own, as it always has.";
            return "Icons made without waking: " + fast + " of " + seen + " parts' icons (" + light + " of them copying only what is below the part); " + checks + " made both ways and compared, the game's kept"
                 + (differed.Count > 0 ? " (" + differed.Count + " different, all with kinds not yet trusted, now left to the game)" : " (all the same)")
                 + (left > 0 ? "; " + left + " left to the game, a kind in them changing its icon" : "") + ".";
        }

        // ---- which parts can be made this way, and how

        /// <summary>Whether a thing in the copy would be awake were the copy awake: switched on, and everything above it up to the copy.</summary>
        static bool Lit(Transform t, Transform root)
        {
            for (; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return false;
                if (t == root) return true;
            }
            return false;
        }

        /// <summary>Whether a kind has code of its own that runs as it wakes or goes (between it and the game's own base, which was read).</summary>
        static bool Wakes(Type t)
        {
            if (wakes.TryGetValue(t, out bool w)) return w;
            Type stop = typeof(PartModule).IsAssignableFrom(t) ? typeof(PartModule) : typeof(MonoBehaviour);
            w = false;
            for (Type k = t; k != null && k != stop && !w; k = k.BaseType)
            {
                const BindingFlags here = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (string m in messages) if (k.GetMethod(m, here, null, Type.EmptyTypes, null) != null) { w = true; break; }
                if (stop == typeof(PartModule) && k.GetMethod("OnAwake", here, null, Type.EmptyTypes, null) != null) w = true;
            }
            wakes[t] = w;
            return w;
        }

        /// <summary>For a kind of component awake in the part: 0 where it needs no trying, Never where this way cannot make its icon, else 1 + its tries so far.</summary>
        static int Kind(Type t)
        {
            if (kinds.TryGetValue(t, out int k)) return k;
            if (t == typeof(Transform) || t == typeof(MeshFilter) || t == typeof(MeshRenderer) || t == typeof(SkinnedMeshRenderer)
                || typeof(Collider).IsAssignableFrom(t) || t == typeof(ParticleSystem) || t == typeof(ParticleSystemRenderer) || t == typeof(Light) || t == typeof(Animation)
                || t == typeof(Part) || t == typeof(Highlighting.Highlighter))
                k = 0;      // (built into Unity and only kept or only taken out; or read through: the part, and the highlighter it adds)
            else if (typeof(PartModule).IsAssignableFrom(t))
            {
                // (a module that does something of its own as its icon is made: the game asks it to, this way does not)
                MethodInfo icon = t.GetMethod("OnIconCreate", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                k = icon != null && icon.DeclaringType != typeof(PartModule) ? Never : Wakes(t) ? 1 : 0;
            }
            else if (typeof(MonoBehaviour).IsAssignableFrom(t)) k = typeof(Part).IsAssignableFrom(t) || Wakes(t) ? 1 : 0;
            else k = 1;     // (anything else of Unity's own: animators, bodies, sounds; tried like any other)
            kinds[t] = k;
            return k;
        }

        /// <summary>
        /// Whether a component may be left out of a copy of what is below the root, or copied with the child it is under: on the
        /// root, only what the game takes out or the icon gets afresh; below it, only what refers to nothing outside its own child.
        /// </summary>
        static bool Below(Component c, Type t, bool lit, Transform root)
        {
            if (c.transform == root)
                return t == typeof(Transform) || c is Part || c is PartModule || c is EffectBehaviour || t == typeof(Highlighting.Highlighter)
                    || c is Collider || t == typeof(Light) || t == typeof(Animation);
            Transform top = c.transform;
            while (top.parent != root) top = top.parent;
            if (t == typeof(Transform) || t == typeof(MeshFilter) || c is Collider || t == typeof(Light) || t == typeof(Animation)) return true;
            if (c is Renderer r)
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer || r is ParticleSystemRenderer)) return false;
                if (r.probeAnchor != null && !r.probeAnchor.IsChildOf(top)) return false;
                if (r.lightProbeProxyVolumeOverride != null && !r.lightProbeProxyVolumeOverride.transform.IsChildOf(top)) return false;
                if (r is SkinnedMeshRenderer s)
                {
                    if (s.rootBone != null && !s.rootBone.IsChildOf(top)) return false;
                    foreach (Transform b in s.bones) if (b != null && !b.IsChildOf(top)) return false;
                }
                return true;
            }
            // (what the game takes out, where it does: nothing of it is left to refer to anything)
            return lit && (t == typeof(ParticleSystem) || c is EffectBehaviour || c is SmokeTrailControl || c is FXPrefab || c is DAE);
        }

        // (what was found of the part being looked at, kept from one part to the next)
        static readonly List<Type> theirs = new List<Type>();
        static bool belowOnly, variants, eva, asteroid, comet;
        static int highlighters;

        /// <summary>What to do with a part: -1 leave it to the game, 0 make it this way, 1 make it both ways and compare.</summary>
        static int Decide(GameObject newPart, out bool expected)
        {
            seen++;
            theirs.Clear();
            belowOnly = true; variants = eva = asteroid = comet = false; highlighters = 0;
            bool trusted = true;
            Transform root = newPart.transform;
            Component[] found = newPart.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < found.Length; i++)
            {
                Component c = found[i];
                if (c == null) { belowOnly = false; continue; }     // (a script that is missing: copied as it is, with the whole part)
                Type t = c.GetType();
                bool lit = Lit(c.transform, root);
                if (belowOnly && !Below(c, t, lit, root)) belowOnly = false;
                if (c.transform == root)
                {
                    if (t == typeof(Highlighting.Highlighter)) highlighters++;
                    else if (t == typeof(ModulePartVariants)) variants = true;
                    else if (c is KerbalEVA) eva = true;
                    else if (c is ModuleAsteroid) asteroid = true;
                    else if (c is ModuleComet) comet = true;
                }
                if (!lit) continue;                                     // (never woken either way)
                int k = Kind(t);
                if (k == 0) continue;
                if (k == Never) { expected = false; return -1; }
                if (!theirs.Contains(t)) theirs.Add(t);
                if (k < Trusted + 1) trusted = false;
            }
            if (highlighters > 1) belowOnly = false;
            expected = trusted;
            if (!trusted || all || seen <= 4) return 1;
            return ++againCount % Again == 0 ? 1 : 0;
        }

        // ---- the icon

        /// <summary>In place of the game's own: an icon made this way, or (where a part is to be compared) both ways, the game's kept.</summary>
        static bool Make(PartLoader __instance, GameObject newPart, ref float iconScale, ref GameObject __result)
        {
            if (broken || inside || newPart == null) return true;
            int what;
            bool expected;
            try { what = Decide(newPart, out expected); }
            catch (Exception ex) { Put("looking at the part " + newPart.name + " failed (" + ex.Message + ")."); return true; }
            if (what < 0) { left++; return true; }
            if (what == 0)
            {
                GameObject made;
                try { made = Mine(__instance, newPart, out iconScale, null); }
                catch (Exception ex) { Put("making the icon of " + newPart.name + " failed (" + ex.Message + ")."); return true; }
                fast++;
                if (belowOnly) light++;
                __result = made;
                return false;
            }
            // (both ways: the game's own first, as it would have been, then this way's, compared and thrown away)
            object[] args = { newPart, 0f };
            GameObject theirsMade;
            inside = true;
            try { theirsMade = (GameObject)original.Invoke(__instance, args); }
            finally { inside = false; }
            float theirScale = (float)args[1];
            var made2 = new List<Material>();
            GameObject mine = null;
            string differs;
            try
            {
                mine = Mine(__instance, newPart, out float myScale, made2);
                differs = !myScale.Equals(theirScale) ? "its size " + myScale.ToString("R") + " against the game's " + theirScale.ToString("R") : theirsMade == null ? "the game made none" : Differ(mine.transform, theirsMade.transform, mine.name);
            }
            catch (Exception ex) { differs = "making it this way failed (" + ex.Message + ")"; }
            if (mine != null) UnityEngine.Object.DestroyImmediate(mine);
            for (int i = 0; i < made2.Count; i++) if (made2[i] != null) UnityEngine.Object.DestroyImmediate(made2[i]);
            checks++;
            if (differs == null)
            {
                for (int i = 0; i < theirs.Count; i++) if (kinds[theirs[i]] > 0 && kinds[theirs[i]] <= Trusted) kinds[theirs[i]]++;
            }
            else if (expected) Put("the icon of " + newPart.name + " came out different from the game's: " + differs + ".");
            else
            {
                var named = new StringBuilder();
                for (int i = 0; i < theirs.Count; i++)
                {
                    if (kinds[theirs[i]] > Trusted) continue;
                    kinds[theirs[i]] = Never;
                    named.Append(named.Length > 0 ? ", " : "").Append(theirs[i].Name);
                }
                differed.Add(newPart.name);
                WarmStart.Companion.Log("the icon of " + newPart.name + " made without waking differs from the game's (" + differs + "): parts with " + named + " are left to the game");
            }
            iconScale = theirScale;
            __result = theirsMade;
            return false;
        }

        static void Put(string what)
        {
            if (broken) return;
            broken = true;
            why = what;
            WarmStart.Companion.Log("ICONS MADE WITHOUT WAKING PUT AWAY: " + what + " The game makes its own icons from here on. Please tell the mod's author.");
        }

        /// <summary>
        /// The copy, asleep in the holder: of what is below the part's root only, where Decide found that enough, under a new
        /// root as the game's copy's would be (named as a copy, placed and switched as the part, with the highlighter the game's
        /// copy keeps); else of the whole part.
        /// </summary>
        static GameObject Copy(GameObject newPart)
        {
            if (!belowOnly) return UnityEngine.Object.Instantiate(newPart, holder.transform, false);
            var root = new GameObject(newPart.name + "(Clone)");
            Transform from = newPart.transform, to = root.transform;
            to.SetParent(holder.transform, false);
            root.layer = newPart.layer;
            root.tag = newPart.tag;
            root.hideFlags = newPart.hideFlags;
            root.SetActive(newPart.activeSelf);
            to.localPosition = from.localPosition;
            to.localRotation = from.localRotation;
            to.localScale = from.localScale;
            if (highlighters == 1) root.AddComponent<Highlighting.Highlighter>();
            for (int i = 0; i < from.childCount; i++)
            {
                GameObject child = from.GetChild(i).gameObject;
                GameObject copy = UnityEngine.Object.Instantiate(child, to, false);
                copy.name = child.name;
            }
            return root;
        }

        /// <summary>
        /// The game's own steps, in its own order, but for the copy being made asleep: what it takes out is found as its helpers
        /// find it (everything of that kind that is awake, which here means that would be), and taken out with the same calls.
        /// </summary>
        static GameObject Mine(PartLoader loader, GameObject newPart, out float iconScale, List<Material> made)
        {
            if (holder == null) { holder = new GameObject("Warm Start: icons being made"); holder.SetActive(false); }
            GameObject gameObject = Copy(newPart);
            GameObject gameObject2 = null;
            try
            {
                gameObject.SetActive(true);
                Transform root = gameObject.transform;
                // (what the part variants do as they wake: a copy of its own of each of the model's renderers' materials)
                if (variants)
                {
                    if (eva) OwnMaterials(root.Find("model01"));
                    if (asteroid) OwnMaterials(root.Find("Asteroid"));
                    if (comet) OwnMaterials(root.Find("Comet"));
                    OwnMaterials(root.Find("model"));
                }
                // (no module's OnIconCreate: only parts whose modules all keep the game's own, which does nothing, are made this way)
                Strip<Part>(gameObject, root);
                Strip<PartModule>(gameObject, root);
                Strip<EffectBehaviour>(gameObject, root);
                StripObjects<Collider>(gameObject, "collider", root);
                Strip<Collider>(gameObject, root);
                Strip<WheelCollider>(gameObject, root);
                Strip<SmokeTrailControl>(gameObject, root);
                Strip<FXPrefab>(gameObject, root);
                Strip<ParticleSystem>(gameObject, root);
                Strip<Light>(gameObject, root);
                Strip<Animation>(gameObject, root);
                Strip<DAE>(gameObject, root);
                StripTagged<MeshRenderer>(gameObject, "Icon_Hidden", root);
                StripTagged<MeshFilter>(gameObject, "Icon_Hidden", root);
                StripTagged<SkinnedMeshRenderer>(gameObject, "Icon_Hidden", root);
                // (let out: what is left wakes now, as all of it did at once the game's way)
                root.SetParent(null, false);
                Bounds partBounds = bounds(loader, gameObject);
                materials(loader, gameObject);
                if (made != null)
                    foreach (Renderer r in gameObject.GetComponentsInChildren<Renderer>())
                        foreach (Material m in r.sharedMaterials) if (m != null) made.Add(m);
                float num = (iconScale = 1f / Mathf.Max(Mathf.Abs(partBounds.size.x), Mathf.Max(Mathf.Abs(partBounds.size.y), Mathf.Abs(partBounds.size.z))));
                gameObject2 = new GameObject();
                gameObject2.name = gameObject.name + " icon";
                gameObject.transform.parent = gameObject2.transform;
                gameObject.transform.localScale = Vector3.one * num;
                gameObject.transform.localPosition = partBounds.center * (0f - num);
                gameObject2.transform.parent = loader.transform;
                gameObject2.SetActive(false);
                return gameObject2;
            }
            catch
            {
                if (gameObject2 != null) UnityEngine.Object.DestroyImmediate(gameObject2);
                if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
                throw;
            }
        }

        // (Part.FindModelComponents<Renderer>, under one of the part's children, with each renderer's own material asked for, as ModulePartVariants.OnAwake asks)
        static void OwnMaterials(Transform t)
        {
            if (t == null) return;
            Renderer r = t.gameObject.GetComponent<Renderer>();
            if (r != null) _ = r.material;
            for (int i = 0; i < t.childCount; i++) OwnMaterials(t.GetChild(i));
        }

        // (what the game's helpers would find: everything of that kind awake in the copy, in the order they give it)
        static List<T> Awake<T>(GameObject at, Transform root) where T : Component
        {
            T[] found = at.GetComponentsInChildren<T>(true);
            var list = new List<T>(found.Length);
            for (int i = 0; i < found.Length; i++) if (Lit(found[i].transform, root)) list.Add(found[i]);
            return list;
        }

        // (PartLoader.StripComponent<T>(part))
        static void Strip<T>(GameObject part, Transform root) where T : Component
        {
            List<T> found = Awake<T>(part, root);
            for (int i = 0, n = found.Count; i < n; i++) UnityEngine.Object.DestroyImmediate(found[i]);
        }

        // (PartLoader.StripGameObject<T>(part, name))
        static void StripObjects<T>(GameObject part, string name, Transform root) where T : Component
        {
            List<T> found = Awake<T>(part, root);
            for (int i = 0, n = found.Count; i < n; i++)
                if (found[i].gameObject.name == name) UnityEngine.Object.DestroyImmediate(found[i].gameObject);
        }

        // (PartLoader.StripComponent<T>(part, tag, parseChildren: true))
        static void StripTagged<T>(GameObject part, string tag, Transform root) where T : Component
        {
            List<T> found = Awake<T>(part, root);
            for (int i = 0, n = found.Count; i < n; i++)
            {
                if (found[i] == null) continue;
                if (!found[i].gameObject.CompareTag(tag)) continue;
                Strip<T>(found[i].gameObject, root);
                UnityEngine.Object.DestroyImmediate(found[i]);
            }
        }

        // ---- comparing two icons

        /// <summary>Where two icons differ, in words, or null where they are the same in everything compared.</summary>
        static string Differ(Transform a, Transform b, string path)
        {
            if (a.name != b.name) return path + " is named \"" + b.name + "\" the game's way";
            if (a.gameObject.activeSelf != b.gameObject.activeSelf) return path + " is " + (b.gameObject.activeSelf ? "on" : "off") + " the game's way";
            if (a.gameObject.layer != b.gameObject.layer || a.tag != b.tag || a.gameObject.hideFlags != b.gameObject.hideFlags) return path + " has another layer, tag or flag the game's way";
            if (!a.localPosition.Equals(b.localPosition) || !a.localRotation.Equals(b.localRotation) || !a.localScale.Equals(b.localScale)) return path + " is placed otherwise the game's way";
            Component[] ca = a.GetComponents<Component>(), cb = b.GetComponents<Component>();
            if (ca.Length != cb.Length) return path + " has " + cb.Length + " components the game's way, " + ca.Length + " this way (" + Kinds(cb) + " against " + Kinds(ca) + ")";
            for (int i = 0; i < ca.Length; i++)
            {
                if (ca[i] == null || cb[i] == null) { if ((ca[i] == null) != (cb[i] == null)) return path + " has a missing script in one only"; continue; }
                if (ca[i].GetType() != cb[i].GetType()) return path + " has " + cb[i].GetType().Name + " the game's way where this way has " + ca[i].GetType().Name;
                string d = Differ(ca[i], cb[i], a, b);
                if (d != null) return path + "'s " + ca[i].GetType().Name + " " + d;
            }
            if (a.childCount != b.childCount) return path + " has " + b.childCount + " children the game's way, " + a.childCount + " this way";
            for (int i = 0; i < a.childCount; i++)
            {
                string d = Differ(a.GetChild(i), b.GetChild(i), path + "/" + a.GetChild(i).name);
                if (d != null) return d;
            }
            return null;
        }

        static string Kinds(Component[] cs)
        {
            var s = new StringBuilder();
            foreach (Component c in cs) s.Append(s.Length > 0 ? " " : "").Append(c == null ? "missing" : c.GetType().Name);
            return s.ToString();
        }

        /// <summary>The path of a thing in an icon, from the icon's top: what a reference is compared by, its own copy in each.</summary>
        static string Where(Transform t)
        {
            if (t == null) return null;
            var s = new StringBuilder(t.name);
            for (Transform p = t.parent; p != null; p = p.parent) s.Insert(0, p.name + "/");
            return s.ToString();
        }

        static string Differ(Component x, Component y, Transform ax, Transform bx)
        {
            if (x is Behaviour bhx && bhx.enabled != ((Behaviour)y).enabled) return "is switched otherwise";
            if (x is MeshFilter fx && fx.sharedMesh != ((MeshFilter)y).sharedMesh) return "has another mesh";
            if (x is Renderer rx)
            {
                var ry = (Renderer)y;
                if (rx.enabled != ry.enabled || rx.shadowCastingMode != ry.shadowCastingMode || rx.receiveShadows != ry.receiveShadows || rx.motionVectorGenerationMode != ry.motionVectorGenerationMode
                    || rx.lightProbeUsage != ry.lightProbeUsage || rx.reflectionProbeUsage != ry.reflectionProbeUsage || rx.sortingLayerID != ry.sortingLayerID || rx.sortingOrder != ry.sortingOrder
                    || rx.renderingLayerMask != ry.renderingLayerMask || rx.allowOcclusionWhenDynamic != ry.allowOcclusionWhenDynamic || Where(rx.probeAnchor) != Where(ry.probeAnchor)) return "is drawn otherwise";
                Material[] ma = rx.sharedMaterials, mb = ry.sharedMaterials;
                if (ma.Length != mb.Length) return "has " + mb.Length + " materials the game's way, " + ma.Length + " this way";
                for (int i = 0; i < ma.Length; i++)
                {
                    if (ma[i] == null || mb[i] == null) { if ((ma[i] == null) != (mb[i] == null)) return "has a material missing in one only"; continue; }
                    if (ma[i].name != mb[i].name) return "has the material \"" + mb[i].name + "\" the game's way where this way has \"" + ma[i].name + "\"";
                    if (ma[i].shader != mb[i].shader || ma[i].renderQueue != mb[i].renderQueue || string.Join(" ", ma[i].shaderKeywords) != string.Join(" ", mb[i].shaderKeywords)) return "has its material \"" + ma[i].name + "\" drawn otherwise";
                    if (ma[i].HasProperty("_MainTex") && ma[i].mainTexture != mb[i].mainTexture) return "has another picture on \"" + ma[i].name + "\"";
                    if (ma[i].HasProperty("_Color") && !ma[i].color.Equals(mb[i].color)) return "has another colour on \"" + ma[i].name + "\"";
                }
                if (x is SkinnedMeshRenderer sx)
                {
                    var sy = (SkinnedMeshRenderer)y;
                    if (sx.sharedMesh != sy.sharedMesh || !sx.localBounds.Equals(sy.localBounds) || sx.quality != sy.quality || sx.updateWhenOffscreen != sy.updateWhenOffscreen || sx.skinnedMotionVectors != sy.skinnedMotionVectors) return "is skinned otherwise";
                    if (Where(sx.rootBone) != Where(sy.rootBone)) return "has another root bone";
                    Transform[] ba = sx.bones, bb = sy.bones;
                    if (ba.Length != bb.Length) return "has another number of bones";
                    for (int i = 0; i < ba.Length; i++) if (Where(ba[i]) != Where(bb[i])) return "has another bone (" + Where(bb[i]) + " the game's way, " + Where(ba[i]) + " this way)";
                    if (sx.sharedMesh != null) for (int i = 0; i < sx.sharedMesh.blendShapeCount; i++) if (!sx.GetBlendShapeWeight(i).Equals(sy.GetBlendShapeWeight(i))) return "has a blend shape weighed otherwise";
                }
            }
            return null;
        }
    }
}
