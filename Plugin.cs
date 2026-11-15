using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace HayesUltrawiderescale
{
    [BepInPlugin("com.hayes.ultrawiderescale", "HayesUltrawiderescale", "1.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        // 1 General
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> RefWidth;
        internal static ConfigEntry<float> RefHeight;
        internal static ConfigEntry<float> Match;

        // 2 Canvas Groups
        internal static ConfigEntry<float> HudScale;
        internal static ConfigEntry<float> MenuScale;
        internal static ConfigEntry<float> OtherScale;
        internal static ConfigEntry<string> HudNames;
        internal static ConfigEntry<string> MenuNames;

        // 3 Element Overrides
        internal static ConfigEntry<string> ElementRules;

        // 4 Debug
        internal static ConfigEntry<bool> DumpCanvases;
        internal static ConfigEntry<int> DumpDepth;

        private const int GroupHud = 0, GroupMenu = 1, GroupOther = 2;
        private const float RescanInterval = 1f;                                   // seconds between element scans
        private static readonly long BudgetTicks = (long)(Stopwatch.Frequency * 0.0004); // 0.4 ms of scanning per frame max

        private struct Rule
        {
            public string[] Segments;   // path suffix, e.g. ["Inventory Screen", "Items Panel"]
            public float Scale;
            public Vector2 Offset;
            public bool HasOffset;
        }

        private struct ElementState
        {
            public Vector3 Scale;
            public Vector2 Pos;
            public bool PosTouched;
            public int RuleIndex;
        }

        private struct ScalerState
        {
            public CanvasScaler.ScaleMode Mode;
            public Vector2 Ref;
            public CanvasScaler.ScreenMatchMode MatchMode;
            public float Match;
        }

        private sealed class Tracked
        {
            public int Id;
            public CanvasScaler Scaler;
            public Canvas Canvas;
            public string Name;
            public int Group;
            public bool HasOriginal;
            public ScalerState Original;
            public int Corrections;
            public bool Warned;
        }

        // ----- canvas scaler state (event driven: filled by the OnEnable hook, never searched for) -----
        private static readonly List<Tracked> TrackedList = new List<Tracked>();
        private static readonly HashSet<int> TrackedIds = new HashSet<int>();
        private static readonly Vector2[] TargetRef = new Vector2[3];
        private static string[] _hudFrags = new string[0];
        private static string[] _menuFrags = new string[0];

        // ----- element override state -----
        private static List<Rule> _rules = new List<Rule>();
        private static readonly Dictionary<RectTransform, ElementState> ElementOriginals = new Dictionary<RectTransform, ElementState>();
        private static readonly List<RectTransform> Matched = new List<RectTransform>();
        private static readonly Dictionary<int, int> RuleCache = new Dictionary<int, int>(); // instanceID -> rule index or -1

        // incremental scan state
        private static readonly List<Transform> StackT = new List<Transform>(64);
        private static readonly List<int> StackI = new List<int>(64);
        private static readonly Stopwatch Clock = new Stopwatch();
        private static bool _scanning;
        private static int _scanCanvasIndex;
        private static float _nextScanTime;

        private void Awake()
        {
            Log = Logger;

            const string g1 = "1 General";
            const string g2 = "2 Canvas Groups";
            const string g3 = "3 Element Overrides";
            const string g4 = "4 Debug";

            Enabled   = Config.Bind(g1, "Enabled", true, "Force ScaleWithScreenSize on all root canvases");
            RefWidth  = Config.Bind(g1, "Reference Width", 3440f, new ConfigDescription("Reference resolution width", new AcceptableValueRange<float>(800f, 7680f)));
            RefHeight = Config.Bind(g1, "Reference Height", 1440f, new ConfigDescription("Reference resolution height", new AcceptableValueRange<float>(600f, 4320f)));
            Match     = Config.Bind(g1, "Match Width/Height", 0.5f, new ConfigDescription("0 = match width, 1 = match height", new AcceptableValueRange<float>(0f, 1f)));

            HudScale   = Config.Bind(g2, "HUD Scale", 1f, new ConfigDescription("Size multiplier for canvases matching the HUD names", new AcceptableValueRange<float>(0.25f, 3f)));
            MenuScale  = Config.Bind(g2, "Menu Scale", 1f, new ConfigDescription("Size multiplier for canvases matching the menu names (inventory and stash screens usually live here)", new AcceptableValueRange<float>(0.25f, 3f)));
            OtherScale = Config.Bind(g2, "Other Scale", 1f, new ConfigDescription("Size multiplier for every other canvas", new AcceptableValueRange<float>(0.25f, 3f)));
            HudNames   = Config.Bind(g2, "HUD Canvas Names", "Battle,Hud", "Comma separated, case-insensitive name fragments. Check the BepInEx log or use Dump Canvases to find the real names.");
            MenuNames  = Config.Bind(g2, "Menu Canvas Names", "Menu,Common,Preloader", "Comma separated, case-insensitive name fragments.");

            ElementRules = Config.Bind(g3, "Element Rules", "",
                "Per-object tweaks inside any canvas that has a CanvasScaler. Format: Name=scale or Name=scale,x,y separated by ';'. " +
                "Name is a GameObject name, or a path ending ('Parent/Child') to tell duplicates apart. " +
                "Example: Stash Panel=1.2;Battle UI/Health Panel=0.9,40,-20");

            DumpCanvases = Config.Bind(g4, "Dump Canvases", false, "Tick to write all canvas and child names to the BepInEx log (resets itself)");
            DumpDepth    = Config.Bind(g4, "Dump Depth", 3, new ConfigDescription("How many child levels the dump goes down", new AcceptableValueRange<int>(1, 6)));

            RebuildTargets();
            _rules = ParseRules(ElementRules.Value);

            Enabled.SettingChanged    += (s, e) => Refresh();
            RefWidth.SettingChanged   += (s, e) => Refresh();
            RefHeight.SettingChanged  += (s, e) => Refresh();
            Match.SettingChanged      += (s, e) => Refresh();
            HudScale.SettingChanged   += (s, e) => Refresh();
            MenuScale.SettingChanged  += (s, e) => Refresh();
            OtherScale.SettingChanged += (s, e) => Refresh();
            HudNames.SettingChanged   += (s, e) => Refresh();
            MenuNames.SettingChanged  += (s, e) => Refresh();
            ElementRules.SettingChanged += (s, e) =>
            {
                RestoreElements();
                _rules = ParseRules(ElementRules.Value);
            };
            DumpCanvases.SettingChanged += (s, e) =>
            {
                if (!DumpCanvases.Value) return;
                Dump();
                DumpCanvases.Value = false;
            };

            new Harmony("com.hayes.ultrawiderescale").PatchAll();
        }

        private void Start()
        {
            // One-time pickup of scalers that were already enabled before the hook existed.
            // This is the only whole-project search the plugin ever does (at startup).
            foreach (var s in Resources.FindObjectsOfTypeAll<CanvasScaler>())
                if (s.gameObject.scene.IsValid()) Register(s);
        }

        private void Update()
        {
            if (!Enabled.Value) return;

            ApplyTracked();

            if (_rules.Count > 0)
            {
                ScanStep();
                ApplyMatched();
            }
        }

        // ---------- Config -> cached values (done on change only, never per frame) ----------

        private static void RebuildTargets()
        {
            _hudFrags = SplitFragments(HudNames.Value);
            _menuFrags = SplitFragments(MenuNames.Value);

            TargetRef[GroupHud]   = new Vector2(RefWidth.Value / HudScale.Value,   RefHeight.Value / HudScale.Value);
            TargetRef[GroupMenu]  = new Vector2(RefWidth.Value / MenuScale.Value,  RefHeight.Value / MenuScale.Value);
            TargetRef[GroupOther] = new Vector2(RefWidth.Value / OtherScale.Value, RefHeight.Value / OtherScale.Value);
        }

        private static string[] SplitFragments(string csv)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(csv))
            {
                foreach (var part in csv.Split(','))
                {
                    var p = part.Trim();
                    if (p.Length > 0) list.Add(p);
                }
            }
            return list.ToArray();
        }

        private static bool NameMatches(string name, string[] frags)
        {
            for (int i = 0; i < frags.Length; i++)
                if (name.IndexOf(frags[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static void Classify(Tracked t)
        {
            t.Group = NameMatches(t.Name, _hudFrags) ? GroupHud
                    : NameMatches(t.Name, _menuFrags) ? GroupMenu
                    : GroupOther;
        }

        private static void Refresh()
        {
            RebuildTargets();
            foreach (var t in TrackedList) Classify(t);

            if (!Enabled.Value)
            {
                RestoreScalers();
                RestoreElements();
                return;
            }
            foreach (var t in TrackedList) ApplyScaler(t, false);
        }

        // ---------- Canvas scaling ----------

        internal static void Register(CanvasScaler scaler)
        {
            if (Enabled == null || scaler == null) return;

            int id = scaler.GetInstanceID();
            if (!TrackedIds.Add(id)) return;

            var canvas = scaler.GetComponent<Canvas>();
            if (canvas == null)
            {
                TrackedIds.Remove(id);
                return;
            }

            var t = new Tracked { Id = id, Scaler = scaler, Canvas = canvas, Name = canvas.gameObject.name };
            Classify(t);
            TrackedList.Add(t);

            if (Enabled.Value) ApplyScaler(t, false);
        }

        private static void ApplyTracked()
        {
            for (int i = TrackedList.Count - 1; i >= 0; i--)
            {
                var t = TrackedList[i];
                if (t.Scaler == null)
                {
                    TrackedIds.Remove(t.Id);
                    TrackedList.RemoveAt(i);
                    continue;
                }
                ApplyScaler(t, true);
            }
        }

        private static void ApplyScaler(Tracked t, bool countCorrections)
        {
            var s = t.Scaler;
            var c = t.Canvas;
            if (s == null || c == null || !c.isRootCanvas || c.renderMode == RenderMode.WorldSpace) return;

            if (!t.HasOriginal)
            {
                t.Original = new ScalerState
                {
                    Mode = s.uiScaleMode,
                    Ref = s.referenceResolution,
                    MatchMode = s.screenMatchMode,
                    Match = s.matchWidthOrHeight
                };
                t.HasOriginal = true;
                Log.LogInfo(string.Format("Canvas '{0}' was {1}, ref {2}, match {3}", t.Name, t.Original.Mode, t.Original.Ref, t.Original.Match));
            }

            var target = TargetRef[t.Group];
            bool changed = false;

            if (s.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
            {
                s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                changed = true;
            }
            if (s.referenceResolution != target)
            {
                s.referenceResolution = target;
                changed = true;
            }
            if (s.screenMatchMode != CanvasScaler.ScreenMatchMode.MatchWidthOrHeight)
            {
                s.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                changed = true;
            }
            if (!Mathf.Approximately(s.matchWidthOrHeight, Match.Value))
            {
                s.matchWidthOrHeight = Match.Value;
                changed = true;
            }

            // Diagnostic: if the game keeps undoing our values, that forces a canvas rebuild every time
            if (changed && countCorrections && ++t.Corrections == 120 && !t.Warned)
            {
                t.Warned = true;
                Log.LogWarning(string.Format("Canvas '{0}' keeps being reset by the game ({1} corrections). This can cause stutter; please report this canvas name.", t.Name, t.Corrections));
            }
        }

        private static void RestoreScalers()
        {
            foreach (var t in TrackedList)
            {
                if (!t.HasOriginal) continue;
                var s = t.Scaler;
                if (s != null)
                {
                    s.uiScaleMode = t.Original.Mode;
                    s.referenceResolution = t.Original.Ref;
                    s.screenMatchMode = t.Original.MatchMode;
                    s.matchWidthOrHeight = t.Original.Match;
                }
                t.HasOriginal = false;
            }
        }

        // ---------- Element overrides ----------
        // The hierarchy is walked a little each frame (hard time budget) instead of all at once,
        // and only under canvases the plugin already tracks, so there is no whole-project search.

        private static void ScanStep()
        {
            if (!_scanning)
            {
                if (Time.unscaledTime < _nextScanTime) return;
                _scanning = true;
                _scanCanvasIndex = 0;
                StackT.Clear();
                StackI.Clear();
            }

            Clock.Restart();
            int iter = 0;

            while (true)
            {
                if ((++iter & 31) == 0 && Clock.ElapsedTicks > BudgetTicks) return;

                if (StackT.Count == 0)
                {
                    if (!BeginNextCanvas())
                    {
                        _scanning = false;
                        _nextScanTime = Time.unscaledTime + RescanInterval;
                        if (RuleCache.Count > 100000) RuleCache.Clear();
                        return;
                    }
                    continue;
                }

                int top = StackT.Count - 1;
                var parent = StackT[top];
                if (parent == null)
                {
                    StackT.RemoveAt(top);
                    StackI.RemoveAt(top);
                    continue;
                }

                int idx = StackI[top];
                if (idx >= parent.childCount)
                {
                    StackT.RemoveAt(top);
                    StackI.RemoveAt(top);
                    continue;
                }
                StackI[top] = idx + 1;

                var child = parent.GetChild(idx);
                Visit(child);
                if (child.childCount > 0)
                {
                    StackT.Add(child);
                    StackI.Add(0);
                }
            }
        }

        private static bool BeginNextCanvas()
        {
            while (_scanCanvasIndex < TrackedList.Count)
            {
                var t = TrackedList[_scanCanvasIndex++];
                var c = t.Canvas;
                if (t.Scaler == null || c == null || !c.isRootCanvas || c.renderMode == RenderMode.WorldSpace) continue;
                StackT.Add(c.transform);
                StackI.Add(0);
                return true;
            }
            return false;
        }

        private static void Visit(Transform child)
        {
            var rt = child as RectTransform;
            if (rt == null) return;

            int id = rt.GetInstanceID();
            int ruleIndex;
            if (!RuleCache.TryGetValue(id, out ruleIndex))
            {
                ruleIndex = FindRule(rt);
                RuleCache[id] = ruleIndex;
            }
            if (ruleIndex < 0 || ElementOriginals.ContainsKey(rt)) return;

            ElementOriginals[rt] = new ElementState
            {
                Scale = rt.localScale,
                Pos = rt.anchoredPosition,
                PosTouched = false,
                RuleIndex = ruleIndex
            };
            Matched.Add(rt);
        }

        // Cheap: only touches the handful of objects that matched a rule
        private static void ApplyMatched()
        {
            for (int i = Matched.Count - 1; i >= 0; i--)
            {
                var rt = Matched[i];
                if (rt == null)
                {
                    ElementOriginals.Remove(rt);
                    Matched.RemoveAt(i);
                    continue;
                }

                var st = ElementOriginals[rt];
                var rule = _rules[st.RuleIndex];

                Vector3 s = st.Scale * rule.Scale;
                if (rt.localScale != s) rt.localScale = s;

                if (rule.HasOffset)
                {
                    Vector2 p = st.Pos + rule.Offset;
                    if (rt.anchoredPosition != p) rt.anchoredPosition = p;
                    if (!st.PosTouched)
                    {
                        st.PosTouched = true;
                        ElementOriginals[rt] = st;
                    }
                }
            }
        }

        private static void RestoreElements()
        {
            for (int i = 0; i < Matched.Count; i++)
            {
                var rt = Matched[i];
                if (rt == null) continue;
                var st = ElementOriginals[rt];
                rt.localScale = st.Scale;
                if (st.PosTouched) rt.anchoredPosition = st.Pos;
            }
            Matched.Clear();
            ElementOriginals.Clear();
            RuleCache.Clear();
            StackT.Clear();
            StackI.Clear();
            _scanning = false;
            _nextScanTime = 0f;
        }

        private static int FindRule(Transform t)
        {
            string name = t.name;
            for (int i = 0; i < _rules.Count; i++)
            {
                var segs = _rules[i].Segments;
                if (!string.Equals(name, segs[segs.Length - 1], StringComparison.OrdinalIgnoreCase)) continue;
                if (segs.Length == 1 || PathMatches(t.parent, segs, segs.Length - 2)) return i;
            }
            return -1;
        }

        private static bool PathMatches(Transform t, string[] segs, int last)
        {
            for (int i = last; i >= 0; i--)
            {
                if (t == null || !string.Equals(t.name, segs[i], StringComparison.OrdinalIgnoreCase)) return false;
                t = t.parent;
            }
            return true;
        }

        private static List<Rule> ParseRules(string text)
        {
            var list = new List<Rule>();
            if (string.IsNullOrEmpty(text)) return list;

            foreach (var entry in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = entry.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;

                var segs = new List<string>();
                foreach (var seg in kv[0].Split('/'))
                {
                    var trimmed = seg.Trim();
                    if (trimmed.Length > 0) segs.Add(trimmed);
                }
                if (segs.Count == 0) continue;

                var nums = kv[1].Split(',');
                float scale, x = 0f, y = 0f;
                if (!TryFloat(nums[0], out scale) || scale <= 0f) continue;

                bool hasOffset = nums.Length >= 3 && TryFloat(nums[1], out x) && TryFloat(nums[2], out y)
                                 && (x != 0f || y != 0f);

                list.Add(new Rule
                {
                    Segments = segs.ToArray(),
                    Scale = scale,
                    Offset = new Vector2(x, y),
                    HasOffset = hasOffset
                });
            }
            return list;
        }

        private static bool TryFloat(string s, out float f)
        {
            return float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f);
        }

        // ---------- Debug (on demand only) ----------

        private static void Dump()
        {
            var sb = new StringBuilder("Root canvases:\n");
            foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (c == null || !c.gameObject.scene.IsValid() || !c.isRootCanvas || c.renderMode == RenderMode.WorldSpace) continue;
                var sc = c.GetComponent<CanvasScaler>();
                sb.AppendFormat("[{0}] mode={1} sort={2} active={3} scaler={4}\n",
                    c.name, c.renderMode, c.sortingOrder, c.gameObject.activeInHierarchy,
                    sc != null ? sc.uiScaleMode.ToString() : "none");
                DumpChildren(c.transform, 1, DumpDepth.Value, sb);
            }
            Log.LogInfo(sb.ToString());
        }

        private static void DumpChildren(Transform t, int depth, int maxDepth, StringBuilder sb)
        {
            if (depth > maxDepth) return;
            foreach (Transform child in t)
            {
                sb.Append(' ', depth * 2).Append(child.name);
                if (!child.gameObject.activeSelf) sb.Append(" (inactive)");
                sb.Append('\n');
                DumpChildren(child, depth + 1, maxDepth, sb);
            }
        }
    }

    // New scalers are picked up the moment they are enabled
    [HarmonyPatch(typeof(CanvasScaler), "OnEnable")]
    internal static class CanvasScalerOnEnablePatch
    {
        [HarmonyPostfix]
        private static void Postfix(CanvasScaler __instance)
        {
            Plugin.Register(__instance);
        }
    }
}
