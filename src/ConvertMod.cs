using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using Debug = UnityEngine.Debug;

namespace SaveLanguageConverter
{
    // Adds "Convert a copy" to the load dialog that warns about a save started in another language.
    // The copy is made with the game's own slot duplication; its text is translated in memory right after
    // the game reads it (before inventories are rebuilt) and written back by the game's normal save.
    public sealed class ConvertMod : IMod
    {
        public const string ModId = "save_language_converter";
        const string HarmonyId = "mod." + ModId;
        const string Tag = "[SaveLanguageConverter] ";

        // Most central tables first: their entries win when several share a source string.
        static readonly string[] Tables = { "Item", "ClientName", "Mechanic", "UI", "Interactable", "NetworkUpgrade",
            "RepPerkTable", "PerkTable", "Dialogue", "PhoneDialogue", "Hint", "MainMenu", "Tutorial", "NewGame" };

        const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static string modId = ModId;
        static Harmony harmony;
        static bool hooked;

        sealed class Pending { public int SlotId; public string File; public string From; public string To; }
        static Pending pending;
        static NewGameCommonButton convertButton;
        static LoadGameFrame buttonFrame;
        static RectTransform confirmRoot, cancelRoot, convertRoot;
        static Vector2 confirmPos, confirmSize, cancelPos, cancelSize;
        static bool layoutChanged;

        public void Init(ModManifest manifest)
        {
            if (manifest != null && !string.IsNullOrEmpty(manifest.ID)) modId = manifest.ID;
        }

        public void OnEnable()
        {
            if (harmony == null)
            {
                harmony = new Harmony(HarmonyId);
                harmony.Patch(typeof(LoadGameUI).GetMethod("OnLoadClicked", AnyStatic),
                    new HarmonyMethod(typeof(ConvertMod).GetMethod("OnLoadClickedPrefix", AnyStatic)));
                harmony.Patch(typeof(LoadGameUI).GetMethod("ShowDialog", AnyStatic),
                    null, new HarmonyMethod(typeof(ConvertMod).GetMethod("ShowDialogPostfix", AnyStatic)));
                harmony.Patch(typeof(LoadGameUI).GetMethod("HideDialog", AnyStatic),
                    null, new HarmonyMethod(typeof(ConvertMod).GetMethod("HideDialogPostfix", AnyStatic)));
            }
            if (!hooked) { ModHook.OnGameLoadedEarly += OnGameLoadedEarly; hooked = true; }
        }

        public void OnDisable()
        {
            if (hooked) { ModHook.OnGameLoadedEarly -= OnGameLoadedEarly; hooked = false; }
            if (harmony != null) { harmony.UnpatchAll(HarmonyId); harmony = null; }
            HideConvertButton();
            if (convertButton != null && convertButton.button != null) UnityEngine.Object.Destroy(convertRoot != null ? convertRoot.gameObject : convertButton.button.gameObject);
            convertButton = null; buttonFrame = null;
        }

        static string L(string key, params object[] args)
        {
            return ModHelper.GetLocalized(modId, key, args);
        }

        static string LanguageName(string code)
        {
            string name = L("lang_" + code);
            if (!string.IsNullOrEmpty(name) && name != "lang_" + code) return name;
            var locale = FindLocale(code);
            return locale != null ? locale.LocaleName : code;
        }

        static Locale FindLocale(string code)
        {
            var locales = LocalizationSettings.AvailableLocales.Locales;
            var hit = locales.FirstOrDefault(l => l != null && l.Identifier.Code == code);
            if (hit != null) return hit;
            string want = (code ?? "").Replace("-", "").Replace("_", "").ToLowerInvariant();
            return locales.FirstOrDefault(l => l != null && l.Identifier.Code.Replace("-", "").Replace("_", "").ToLowerInvariant() == want);
        }

        // ---------------------------------------------------------------- load dialog

        static void OnLoadClickedPrefix(int slotId, string file)
        {
            pending = null;
            var preview = SaveUIManager.GetPreview(slotId);
            string current = LocHelper.GetCurrentLocaleCode();
            if (preview == null || string.IsNullOrEmpty(preview.startLocale) || preview.startLocale == current) return;
            if (FindLocale(preview.startLocale) == null || FindLocale(current) == null) return;
            pending = new Pending { SlotId = slotId, File = file, From = preview.startLocale, To = current };
        }

        static void ShowDialogPostfix(string body)
        {
            var p = pending;
            string expected = p == null ? null
                : LocHelper.GetLocalizedMainMenu("load_locale_mismatch_warning", new object[] { p.From, p.To });
            if (p == null || body != expected) { HideConvertButton(); return; }
            try { ShowConvertButton(p); }
            catch (Exception e) { Debug.LogWarning(Tag + "could not add the convert button: " + e); }
        }

        static void HideDialogPostfix()
        {
            HideConvertButton();
        }

        static LoadGameFrame CurrentFrame()
        {
            var f = typeof(LoadGameUI).GetField("frame", AnyStatic);
            return f == null ? null : f.GetValue(null) as LoadGameFrame;
        }

        static RectTransform RootUnder(Transform t, Transform ancestor)
        {
            while (t.parent != null && t.parent != ancestor) t = t.parent;
            return t as RectTransform;
        }

        static void ShowConvertButton(Pending p)
        {
            var frame = CurrentFrame();
            if (frame == null || frame.dialogConfirm == null || frame.dialogCancel == null) return;
            Transform confirmT = frame.dialogConfirm.button.transform, cancelT = frame.dialogCancel.button.transform;
            Transform parent = confirmT.parent;
            while (parent != null && !cancelT.IsChildOf(parent)) parent = parent.parent;
            if (parent == null) return;

            string label = L("button_convert", LanguageName(p.To));
            if (convertButton == null || buttonFrame != frame || convertButton.button == null)
            {
                confirmRoot = RootUnder(confirmT, parent);
                cancelRoot = RootUnder(cancelT, parent);
                confirmPos = confirmRoot.anchoredPosition; confirmSize = confirmRoot.sizeDelta;
                cancelPos = cancelRoot.anchoredPosition; cancelSize = cancelRoot.sizeDelta;
                convertButton = NewGameCommonButton.Build(frame, parent, "SaveLanguageConverter_Convert",
                    0f, 0f, confirmSize.x, confirmSize.y, label, new UnityAction(OnConvertClicked));
                buttonFrame = frame;
                convertRoot = RootUnder(convertButton.button.transform, parent);
                convertRoot.anchorMin = confirmRoot.anchorMin;
                convertRoot.anchorMax = confirmRoot.anchorMax;
                convertRoot.pivot = confirmRoot.pivot;
                if (convertButton.label != null && frame.dialogConfirm.label != null)
                {
                    convertButton.label.enableAutoSizing = true;
                    convertButton.label.fontSizeMax = frame.dialogConfirm.label.fontSize;
                    convertButton.label.fontSizeMin = Mathf.Max(8f, frame.dialogConfirm.label.fontSize * 0.5f);
                }
            }
            if (convertButton.label != null) convertButton.label.text = label;

            // Three buttons in the span the two original ones used: [Confirm] [Convert a copy] [Cancel].
            float px = confirmRoot.pivot.x;
            float left = confirmPos.x - px * confirmSize.x;
            float right = cancelPos.x + (1f - px) * cancelSize.x;
            float gap = Mathf.Max(8f, (cancelPos.x - confirmPos.x) - confirmSize.x);
            float w = (right - left - 2f * gap) / 3f;
            SetWidth(confirmRoot, w);
            SetWidth(convertRoot, w);
            SetWidth(cancelRoot, w);
            confirmRoot.anchoredPosition = new Vector2(left + px * w, confirmPos.y);
            convertRoot.anchoredPosition = new Vector2(left + w + gap + px * w, confirmPos.y);
            cancelRoot.anchoredPosition = new Vector2(left + 2f * (w + gap) + px * w, cancelPos.y);
            layoutChanged = true;
            convertRoot.gameObject.SetActive(true);
            convertRoot.SetAsLastSibling();

            if (frame.dialogText != null) frame.dialogText.text += "\n" + L("dialog_hint", LanguageName(p.To));
        }

        // The game's dialog buttons draw their frames and label as fixed-size children, so a narrower button
        // takes its children along: each loses the same width, keeping its insets and its centred text.
        static void SetWidth(RectTransform root, float width)
        {
            float delta = width - root.sizeDelta.x;
            if (delta == 0f) return;
            root.sizeDelta = new Vector2(width, root.sizeDelta.y);
            foreach (Transform t in root)
            {
                var child = t as RectTransform;
                // A child stretched across the button follows it by itself.
                if (child == null || child.anchorMin.x != child.anchorMax.x) continue;
                child.sizeDelta = new Vector2(child.sizeDelta.x + delta, child.sizeDelta.y);
            }
        }

        static void HideConvertButton()
        {
            if (convertRoot != null) convertRoot.gameObject.SetActive(false);
            if (layoutChanged && confirmRoot != null && cancelRoot != null)
            {
                confirmRoot.anchoredPosition = confirmPos; SetWidth(confirmRoot, confirmSize.x);
                cancelRoot.anchoredPosition = cancelPos; SetWidth(cancelRoot, cancelSize.x);
            }
            layoutChanged = false;
        }

        static void InvokeLoadGameUI(string method, params object[] args)
        {
            var m = typeof(LoadGameUI).GetMethod(method, AnyStatic);
            if (m == null) throw new MissingMethodException("LoadGameUI", method);
            m.Invoke(null, args);
        }

        static void OnConvertClicked()
        {
            var p = pending;
            pending = null;
            InvokeLoadGameUI("HideDialog");
            if (p == null) return;
            try
            {
                var preview = SaveUIManager.GetPreview(p.SlotId);
                string name = preview != null ? preview.GetDisplayName() : ("#" + p.SlotId);
                string copyName = L("copy_name", name, LanguageName(p.To));
                int newSlot = SaveFiles.DuplicateSlot(p.SlotId, copyName);
                if (newSlot < 0)
                {
                    InvokeLoadGameUI("ShowDialog", LocHelper.GetLocalizedMainMenu("load_game", new object[0]), L("copy_failed"), null, false);
                    return;
                }

                // Mark the copy as being in the current language. The text inside its files is converted
                // whenever one of them is loaded while its locale still differs (see OnGameLoadedEarly).
                var copy = SaveFiles.IndexLoad<SavePreviewData>("preview_" + newSlot, null);
                if (copy != null)
                {
                    copy.startLocale = p.To;
                    copy.startName = NewGameData.GetStartDisplayName(copy.startType, copy.startScenarioId);
                    copy.customName = copyName;
                    SaveFiles.RegisterSlot(newSlot, copy);
                }

                string newFile = null;
                if (!string.IsNullOrEmpty(p.File) && p.File != SaveFiles.Slot(p.SlotId))
                {
                    var entry = SaveFiles.LoadHistory(p.SlotId).FirstOrDefault(h => h.file == p.File);
                    if (entry != null) newFile = SaveFiles.HistoryFile(newSlot, entry.dayCount, entry.saveDateTime);
                }
                // The game fixes some text, such as the water qualities that customers ask for, in the language it
                // started in, and keeps it after a language change. Loaded now, the copy would mix the two, so after a
                // change the copy waits for a restart; it is converted when it is loaded then.
                if (StartedInAnotherLanguage())
                {
                    Debug.Log(Tag + "copied slot " + p.SlotId + " (" + p.From + ") to slot " + newSlot + " for " + p.To + "; the game started in another language, asking for a restart");
                    InvokeLoadGameUI("ShowDialog", LocHelper.GetLocalizedMainMenu("load_game", new object[0]),
                        L("restart_needed", copyName, LanguageName(p.To)), null, false);
                    return;
                }
                Debug.Log(Tag + "copied slot " + p.SlotId + " (" + p.From + ") to slot " + newSlot + " for " + p.To + ", loading " + (newFile ?? "latest"));
                InvokeLoadGameUI("LoadSlot", newSlot, newFile);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "conversion failed: " + e);
                InvokeLoadGameUI("ShowDialog", LocHelper.GetLocalizedMainMenu("load_game", new object[0]), L("copy_failed"), null, false);
            }
        }

        // True when the water qualities the game fixed at its first use are not in the current language, i.e. the
        // language was changed since the game started. Reading them now fixes them in the current language if the
        // game has not used them yet, which is right.
        static bool StartedInAnotherLanguage()
        {
            try
            {
                string live = LocHelper.GetLocalizedMechanic("mech_condition_pure_water", new object[0]);
                return !string.IsNullOrEmpty(live) && WaterFeatureHelper.PURE_WATER_DISPLAY != live;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Tag + "could not compare the game's fixed text with the current language: " + e.Message);
                return false;
            }
        }

        // ---------------------------------------------------------------- in-game conversion

        static void OnGameLoadedEarly()
        {
            try
            {
                var store = PlayerStore.Instance;
                if (store == null) return;
                string fileLocale = store.startLocale;
                string current = LocHelper.GetCurrentLocaleCode();
                if (string.IsNullOrEmpty(fileLocale) || string.IsNullOrEmpty(current) || fileLocale == current) return;
                // Only slots this mod marked: a save the player chose to load as-is keeps its language.
                var preview = SaveFiles.IndexLoad<SavePreviewData>("preview_" + store.saveSlotId, null);
                if (preview == null || preview.startLocale != current) return;
                Convert(store, fileLocale, current);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "conversion after load failed, the save was loaded unchanged: " + e);
            }
        }

        static Translator BuildTranslator(string from, string to)
        {
            Locale src = FindLocale(from), dst = FindLocale(to);
            if (src == null || dst == null) throw new InvalidOperationException("locale not found: " + (src == null ? from : to));
            var tr = new Translator(from);
            var db = LocalizationSettings.StringDatabase;
            for (int i = 0; i < Tables.Length; i++)
            {
                StringTable s, d;
                try { s = db.GetTable(Tables[i], src); d = db.GetTable(Tables[i], dst); }
                catch (Exception e) { Debug.LogWarning(Tag + "table " + Tables[i] + " unavailable: " + e.Message); continue; }
                if (s == null || d == null) continue;
                foreach (var kv in s)
                {
                    StringTableEntry de;
                    if (kv.Value == null || !d.TryGetValue(kv.Key, out de) || de == null) continue;
                    tr.Add(Tables[i], i, kv.Value.Key, kv.Value.Value, de.Value);
                }
            }
            return tr;
        }

        static void Convert(PlayerStore store, string from, string to)
        {
            var watch = Stopwatch.StartNew();
            var tr = BuildTranslator(from, to);
            var policy = new TextPolicy(tr);
            var walker = new SaveWalker(policy, KnownTypes.All());
            walker.Walk(store, null, null);
            if (StoreStation.Instance != null) walker.Walk(StoreStation.Instance, null, null);
            store.startLocale = to;

            Debug.Log(Tag + "converted slot " + store.saveSlotId + " from " + from + " to " + to + ": " + policy.Changed +
                " strings in " + watch.ElapsedMilliseconds + " ms (" + tr.EntryCount + " entries, " + tr.TemplateCount + " templates)");
            if (policy.Untranslated.Count > 0)
                Debug.LogWarning(Tag + policy.Untranslated.Count + " text(s) kept as they were: " +
                    string.Join(" | ", policy.Untranslated.Keys.Take(20).Select(k => k.Length > 60 ? k.Substring(0, 60) + "..." : k).ToArray()));
            foreach (var err in walker.Errors.Take(20)) Debug.LogWarning(Tag + err);
        }
    }
}
