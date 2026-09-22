using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// split into multiple cs files because ocd
// add new custom mneu support
//add mod blacklist option 
// improve search function to be less jittery
// possibly add a way to distinguish between internal and external? probably not thow 
// add custom saved loadout options 

namespace UnrestrictedWeapons
{
    [BepInPlugin("com.nikkorap.justj.UnrestrictedWeapons_1.6.0", "Unrestricted Weapons", "1.6.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }
        internal static new ManualLogSource Logger;
        private static Harmony _harmony;
        private static readonly AccessTools.FieldRef<WeaponSelector, TMP_Dropdown> WeaponSelectorDropdownRef =
            AccessTools.FieldRefAccess<WeaponSelector, TMP_Dropdown>("dropdown");
        private static readonly AccessTools.FieldRef<WeaponSelector, TextMeshProUGUI> WeaponSelectorDropdownTextRef =
            AccessTools.FieldRefAccess<WeaponSelector, TextMeshProUGUI>("dropdownText");

        private bool cached = false;
        public List<WeaponMount> originalMounts = new List<WeaponMount>();
        private readonly Dictionary<WeaponMount, string> _mountKey = new Dictionary<WeaponMount, string>();
        private List<string> _filterTokens = new List<string>();
        private bool _blueprinterRefreshDone;

        private ConfigEntry<bool> ModEnabled;
        private ConfigEntry<bool> BlockAI;
        private ConfigEntry<bool> ToggleWhitelist;
        private ConfigEntry<string> BlacklistCsv;

        private void Awake()
        {
            Instance = this;
            this.hideFlags = HideFlags.HideAndDontSave;
            Logger = base.Logger;
            ModEnabled = Config.Bind("General", "ModEnabled", true, "Enable the mod");
            BlockAI = Config.Bind("General", "BlockAI", true, "force AI to use normal loadouts");
            ModEnabled.SettingChanged += (_, __) => ToggleMod(ModEnabled.Value);

            ToggleWhitelist = Config.Bind("General", "ToggleWhitelist", false, "Use the blacklist as a whitelist instead");
            BlacklistCsv = Config.Bind("General", "Part Blacklist (comma-separated)", "flare, afv, lcv, hlt, container, hook, flex, Turret, 750", "Lowercase substrings to block mounts");
            BlacklistCsv.SettingChanged += (_, __) =>
            {
                UpdateFilterTokens();
                if (cached && ModEnabled.Value) { ToggleMod(false); ToggleMod(true); }
            };

            ToggleWhitelist.SettingChanged += (_, __) =>
            {
                Logger.LogInfo($"Filter mode set to {(ToggleWhitelist.Value ? "Whitelist" : "Blacklist")}");
                if (cached && ModEnabled.Value) { ToggleMod(false); ToggleMod(true); }
            };


            UpdateFilterTokens();
            _harmony = new Harmony("com.nikkorap.UnrestrictedWeapons_1.6.0");
            _harmony.PatchAll();
            InstallBlueprinterCompletionHook();
        }

        private void InstallBlueprinterCompletionHook()
        {
            Type blueprinterPluginType = AccessTools.TypeByName("Blueprinter.Plugin");
            MethodInfo runRoutine = blueprinterPluginType == null ? null : AccessTools.Method(blueprinterPluginType, "RunRoutine");
            MethodInfo completion = runRoutine == null ? null : AccessTools.EnumeratorMoveNext(runRoutine);

            if (completion == null)
            {
                Logger.LogWarning("Blueprinter completion hook unavailable; modded mounts will not receive the one-time refresh.");
                return;
            }

            _harmony.Patch(completion, postfix: new HarmonyMethod(typeof(Plugin), nameof(BlueprinterRunRoutinePostfix)));
            Logger.LogInfo("Waiting for Blueprinter bundle loading to complete before the final weapon-mount refresh.");
        }

        private static void BlueprinterRunRoutinePostfix(bool __result)
        {
            if (__result || Instance == null || Instance._blueprinterRefreshDone) return;

            Instance._blueprinterRefreshDone = true;
            Instance.RefreshWeaponMountCache();
            Logger.LogInfo("Blueprinter finished loading; refreshed the weapon-mount cache once.");
        }

        private void UpdateFilterTokens()
        {
            string raw = BlacklistCsv?.Value ?? string.Empty;
            _filterTokens = raw
                .Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length > 0)
                .Distinct().ToList();

            Logger.LogInfo($"Filter tokens: [{string.Join(", ", _filterTokens)}] (mode={(ToggleWhitelist.Value ? "whitelist" : "blacklist")})");
        }

        private bool IsAllowed(WeaponMount wm)
        {
            if (wm == null) return false;
            if (_filterTokens.Count == 0) return !ToggleWhitelist.Value;

            if (!_mountKey.TryGetValue(wm, out string key) || key == null)
                key = ((wm.mountName ?? wm.name) ?? string.Empty).ToLowerInvariant();

            bool matches = _filterTokens.Any(tok => key.Contains(tok));
            return ToggleWhitelist.Value ? matches : !matches;
        }


        // blueprinter fix for weapons after Encyclopedia.AfterLoad keep this for now as working
        private void RefreshWeaponMountCache()
        {
            List<WeaponMount> mounts = Resources.FindObjectsOfTypeAll<WeaponMount>()
                .Where(m => m != null)
                .ToList();

            if (mounts.Count == 0)
            {
                Logger.LogWarning("No WeaponMounts found; skipping refresh.");
                return;
            }

            int added = 0;
            foreach (WeaponMount m in mounts)
            {
                if (_mountKey.ContainsKey(m)) continue;

                string key = ((m.mountName ?? m.name) ?? string.Empty).ToLowerInvariant();
                _mountKey[m] = key;
                if (m.mountName is string s && !s.Contains(" [")) m.mountName = $"{s} [{m.name}]";
                originalMounts.Add(m);
                added++;
            }

            bool wasCached = cached;
            cached = originalMounts.Count > 0;
            if (!wasCached && cached) ToggleMod(ModEnabled.Value);

            if (added > 0)
                Logger.LogInfo($"Weapon mount cache refreshed: added {added}, total {originalMounts.Count}.");
        }

        private void ToggleMod(bool enable)
        {
            if (!cached) return;

            Logger.LogInfo($"ToggleMod {(enable ? "Enabled" : "Disabled")} on mounts={originalMounts.Count}. mode={(ToggleWhitelist.Value ? "whitelist" : "blacklist")}");
        }

        private static void EnsureWeaponSearchController(WeaponSelector selector)
        {
            if (selector == null) return;

            TMP_Dropdown dropdown = WeaponSelectorDropdownRef(selector);
            if (dropdown == null) return;

            TextMeshProUGUI dropdownText = WeaponSelectorDropdownTextRef(selector);
            WeaponDropdownSearchController controller = selector.GetComponent<WeaponDropdownSearchController>();
            if (controller == null)
            {
                controller = selector.gameObject.AddComponent<WeaponDropdownSearchController>();
            }

            controller.Configure(selector, dropdown, dropdownText);
        }

        public class PatchState
        {
            public HardpointSet Set;
            public List<WeaponMount> OriginalOptions;
        }

        [HarmonyPatch(typeof(WeaponSelector), "Awake")]
        static class WeaponSelectorAwakePatch
        {
            static void Postfix(WeaponSelector __instance)
            {
                EnsureWeaponSearchController(__instance);
            }
        }

        [HarmonyPatch(typeof(WeaponSelector), "Initialize", new Type[] { typeof(HardpointSet), typeof(NuclearOption.SavedMission.SavedLoadout.SelectedMount?) })]
        static class WeaponSelectorInitializePatch
        {
            static void Postfix(WeaponSelector __instance)
            {
                EnsureWeaponSearchController(__instance);
            }
        }

        [HarmonyPatch(typeof(WeaponSelector), "Initialize", new Type[] { typeof(Aircraft), typeof(HardpointSet), typeof(FactionHQ), typeof(Airbase) })]
        static class WeaponSelectorInitializeAirbasePatch
        {
            static void Postfix(WeaponSelector __instance)
            {
                EnsureWeaponSearchController(__instance);
            }
        }

        [HarmonyPatch(typeof(WeaponSelector), "DropdownChanged")]
        static class WeaponSelectorDropdownChangedPatch
        {
            static bool Prefix(WeaponSelector __instance, ref int index)
            {
                WeaponDropdownSearchController controller = __instance == null
                    ? null
                    : __instance.GetComponent<WeaponDropdownSearchController>();
                return controller == null || controller.TryRemapSelection(ref index);
            }
        }

        [HarmonyPatch(typeof(WeaponChecker), "GetAvailableWeaponsNonAlloc")]
        static class GetAvailableWeaponsNonAlloc_FeedOriginal
        {
            static void Prefix(System.Reflection.MethodBase __originalMethod, object[] __args, out PatchState __state)
            {
                __state = null;

                if (Instance == null || !Instance.ModEnabled.Value) return;


                HardpointSet hardpointSet = null;

                if (__args != null)
                {
                    for (int i = 0; i < __args.Length; i++)
                    {
                        if (__args[i] is HardpointSet hs)
                        {
                            hardpointSet = hs;
                            break;
                        }
                    }
                }

                if (hardpointSet == null) return;

                bool isUI = false;
                var trace = new System.Diagnostics.StackTrace();
                foreach (var frame in trace.GetFrames())
                {
                    var m = frame.GetMethod();
                    if (m != null && m.DeclaringType != null)
                    {
                        if (m.DeclaringType.Name.Contains("WeaponSelector"))
                        {
                            isUI = true;
                            break;
                        }
                    }
                }

                if (isUI || !Instance.BlockAI.Value)
                {
                    List<WeaponMount> original = hardpointSet.weaponOptions;
                    List<WeaponMount> expanded = new List<WeaponMount>(original);
                    HashSet<int> seen = new HashSet<int>(original.Where(x => x != null).Select(x => x.GetInstanceID()));

                    foreach (WeaponMount wm in Instance.originalMounts)
                    {
                        if (wm != null && Instance.IsAllowed(wm) && seen.Add(wm.GetInstanceID()))
                            expanded.Add(wm);
                    }

                    __state = new PatchState { Set = hardpointSet, OriginalOptions = original };
                    hardpointSet.weaponOptions = expanded;
                }
            }

            static void Postfix(PatchState __state)
            {
                if (__state != null && __state.Set != null && __state.OriginalOptions != null)
                {
                    __state.Set.weaponOptions = __state.OriginalOptions;
                }
            }
        }

        [HarmonyPatch(typeof(WeaponChecker), "VetLoadout")]
        static class VetLoadout_FeedOriginal
        {
            static void Prefix(AircraftDefinition definition, NuclearOption.Networking.Player player, out List<PatchState> __state)
            {
                __state = null;
                if (Instance == null || !Instance.ModEnabled.Value) return;


                if (definition == null || definition.unitPrefab == null) return;
                
                Aircraft aircraft = definition.unitPrefab.GetComponent<Aircraft>();
                if (aircraft == null || aircraft.weaponManager == null || aircraft.weaponManager.hardpointSets == null) return;

                bool allow = (player != null) || !Instance.BlockAI.Value;

                if (!allow) return;

                __state = new List<PatchState>();

                foreach (HardpointSet hs in aircraft.weaponManager.hardpointSets)
                {
                    if (hs == null) continue;

                    List<WeaponMount> original = hs.weaponOptions;
                    List<WeaponMount> expanded = new List<WeaponMount>(original);
                    HashSet<int> seen = new HashSet<int>(original.Where(x => x != null).Select(x => x.GetInstanceID()));

                    foreach (WeaponMount wm in Instance.originalMounts)
                    {
                        if (wm != null && Instance.IsAllowed(wm) && seen.Add(wm.GetInstanceID()))
                            expanded.Add(wm);
                    }

                    __state.Add(new PatchState { Set = hs, OriginalOptions = original });
                    hs.weaponOptions = expanded;
                }
            }

            static void Postfix(List<PatchState> __state)
            {
                if (__state != null)
                {
                    foreach (PatchState state in __state)
                    {
                        if (state != null && state.Set != null && state.OriginalOptions != null)
                        {
                            state.Set.weaponOptions = state.OriginalOptions;
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(Encyclopedia), "AfterLoad", new Type[] { })]
        public static class EncyclopediaAfterLoadPatch
        {
            static void Postfix() => Instance.RefreshWeaponMountCache();
        }
    }

    public class WeaponDropdownSearchController : MonoBehaviour
    {
        private const float SearchBarHeight = 30f;
        private const float SearchBarMargin = 8f;

        private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo DropdownInstanceField = typeof(TMP_Dropdown).GetField("m_Dropdown", InstanceFlags);

        private TMP_Dropdown _dropdown;
        private TextMeshProUGUI _styleSource;
        private GameObject _activeDropdownRoot;
        private GameObject _searchOverlayRoot;
        private TMP_InputField _searchField;
        private RectTransform _contentRect;
        private RectTransform _viewportRect;
        private Vector2 _originalViewportOffsetMin;
        private Vector2 _originalViewportOffsetMax;
        private float _originalViewportHeight;
        private readonly List<DropdownItemEntry> _items = new List<DropdownItemEntry>();
        private string _searchText = string.Empty;
        private bool _focusedThisOpen;
        private Vector2 _originalContentSizeDelta;
        private Vector2 _originalContentAnchoredPosition;
        private Vector2 _originalContentAnchorMin;
        private Vector2 _originalContentAnchorMax;
        private Vector2 _originalContentPivot;
        private bool _hasOriginalContentLayout;
        private float _originalDropdownHeight;
        private Coroutine _forceTopCoroutine;
        private WeaponSelector _selector;
        private readonly List<TMP_Dropdown.OptionData> _allOptions = new List<TMP_Dropdown.OptionData>();
        private readonly List<int> _visibleOptionIndices = new List<int>();
        private bool _optionsCaptured;
        private bool _isFiltered;
        private bool _rebuildInProgress;
        private Coroutine _rebuildCoroutine;
        private int _selectedOriginalIndex;

        public void Configure(WeaponSelector selector, TMP_Dropdown dropdown, TextMeshProUGUI styleSource)
        {
            _selector = selector;
            _dropdown = dropdown;
            _styleSource = styleSource;
        }

        private void Update()
        {
            if (_dropdown == null || DropdownInstanceField == null)
            {
                return;
            }

            GameObject liveDropdown = DropdownInstanceField.GetValue(_dropdown) as GameObject;
            if (liveDropdown != null && !liveDropdown.activeInHierarchy)
            {
                liveDropdown = null;
            }

            if (liveDropdown != _activeDropdownRoot)
            {
                HandleDropdownRootChanged(liveDropdown);
            }

            if (_activeDropdownRoot == null)
            {
                return;
            }

            if (_searchField == null)
            {
                BuildSearchField(_activeDropdownRoot);
                CacheDropdownItems(_activeDropdownRoot);
            }
            else
            {
                UpdateOverlayPlacement(_activeDropdownRoot);
            }

            KeepFilteredResultsAtTop();
        }

        private void HandleDropdownRootChanged(GameObject newRoot)
        {
            _activeDropdownRoot = newRoot;
            _searchField = null;
            _contentRect = null;
            _viewportRect = null;
            _originalViewportHeight = 0f;
            _items.Clear();
            _focusedThisOpen = false;
            StopForceTopRoutine();
            _hasOriginalContentLayout = false;
            _originalDropdownHeight = 0f;

            if (_searchOverlayRoot != null)
            {
                Destroy(_searchOverlayRoot);
                _searchOverlayRoot = null;
            }

            if (_activeDropdownRoot == null)
            {
                if (!_rebuildInProgress)
                {
                    RestoreFullOptions();
                    _searchText = string.Empty;
                }
                return;
            }

        }

        private void BuildSearchField(GameObject dropdownRoot)
        {
            RectTransform rootRect = dropdownRoot.transform as RectTransform;
            if (rootRect == null)
            {
                return;
            }

            RectTransform viewport = dropdownRoot.transform.Find("Viewport") as RectTransform;
            _viewportRect = viewport;
            _contentRect = viewport != null ? viewport.Find("Content") as RectTransform : null;
            if (_contentRect == null)
            {
                return;
            }

            if (_originalDropdownHeight <= 0f)
            {
                _originalDropdownHeight = rootRect.rect.height;
            }

            if (_viewportRect != null && _originalViewportHeight <= 0f)
            {
                _originalViewportHeight = _viewportRect.rect.height;
                _originalViewportOffsetMin = _viewportRect.offsetMin;
                _originalViewportOffsetMax = _viewportRect.offsetMax;
            }

            if (!_hasOriginalContentLayout)
            {
                _originalContentSizeDelta = _contentRect.sizeDelta;
                _originalContentAnchoredPosition = _contentRect.anchoredPosition;
                _originalContentAnchorMin = _contentRect.anchorMin;
                _originalContentAnchorMax = _contentRect.anchorMax;
                _originalContentPivot = _contentRect.pivot;
                _hasOriginalContentLayout = true;
            }

            if (_searchOverlayRoot == null)
            {
                _searchOverlayRoot = new GameObject("WeaponSearchOverlay", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
                _searchOverlayRoot.transform.SetParent(dropdownRoot.transform, false);
                _searchOverlayRoot.transform.SetAsLastSibling();

                RectTransform searchRect = _searchOverlayRoot.GetComponent<RectTransform>();
                searchRect.anchorMin = new Vector2(0f, 1f);
                searchRect.anchorMax = new Vector2(0f, 1f);
                searchRect.pivot = new Vector2(0f, 0f);

                Image background = _searchOverlayRoot.GetComponent<Image>();
                background.color = new Color(0f, 0f, 0f, 0.85f);
                background.raycastTarget = true;

                GameObject textArea = new GameObject("Text Area", typeof(RectTransform));
                textArea.transform.SetParent(_searchOverlayRoot.transform, false);
                RectTransform textAreaRect = textArea.GetComponent<RectTransform>();
                textAreaRect.anchorMin = Vector2.zero;
                textAreaRect.anchorMax = Vector2.one;
                textAreaRect.offsetMin = new Vector2(8f, 4f);
                textAreaRect.offsetMax = new Vector2(-8f, -4f);

                TextMeshProUGUI textComponent = CreateTextElement("Text", textArea.transform, string.Empty, new Color(0.85f, 1f, 0.85f, 1f));
                textComponent.alignment = TextAlignmentOptions.Left;

                TextMeshProUGUI placeholderComponent = CreateTextElement("Placeholder", textArea.transform, "Search", new Color(0.65f, 0.8f, 0.65f, 0.65f));
                placeholderComponent.fontStyle = FontStyles.Italic;
                placeholderComponent.alignment = TextAlignmentOptions.Left;

                _searchField = _searchOverlayRoot.GetComponent<TMP_InputField>();
                _searchField.textViewport = textAreaRect;
                _searchField.textComponent = textComponent;
                _searchField.placeholder = placeholderComponent;
                _searchField.lineType = TMP_InputField.LineType.SingleLine;
                _searchField.interactable = true;
                _searchField.enabled = true;
                _searchField.targetGraphic = background;
                _searchField.customCaretColor = true;
                _searchField.caretColor = new Color(0.85f, 1f, 0.85f, 1f);
                _searchField.caretWidth = 2;
                _searchField.caretBlinkRate = 0.85f;
                _searchField.onValueChanged.AddListener(OnSearchValueChanged);

                SearchFieldClickProxy clickProxy = _searchOverlayRoot.AddComponent<SearchFieldClickProxy>();
                clickProxy.Owner = this;
            }
            else
            {
                _searchOverlayRoot.transform.SetAsLastSibling();
            }

            UpdateOverlayPlacement(dropdownRoot);
            _searchField.SetTextWithoutNotify(_searchText);
            if (!_focusedThisOpen)
            {
                _focusedThisOpen = true;
                FocusSearchField();
            }

        }

        public void FocusSearchField()
        {
            if (_searchField == null)
            {
                return;
            }

            EventSystem.current?.SetSelectedGameObject(_searchField.gameObject);
            _searchField.ActivateInputField();
            _searchField.Select();
        }

        private void UpdateOverlayPlacement(GameObject dropdownRoot)
        {
            if (_searchOverlayRoot == null)
            {
                return;
            }

            RectTransform rootRect = dropdownRoot.transform as RectTransform;
            RectTransform searchRect = _searchOverlayRoot.transform as RectTransform;
            if (rootRect == null || searchRect == null)
            {
                return;
            }

            float width = Mathf.Max(60f, rootRect.rect.width - 26f);
            searchRect.anchoredPosition = new Vector2(6f, 4f);
            searchRect.sizeDelta = new Vector2(width, SearchBarHeight);
        }

        private TextMeshProUGUI CreateTextElement(string name, Transform parent, string text, Color color)
        {
            GameObject textObject = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(parent, false);

            RectTransform rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            TextMeshProUGUI textComponent = textObject.GetComponent<TextMeshProUGUI>();
            if (_styleSource != null)
            {
                textComponent.font = _styleSource.font;
                textComponent.fontSharedMaterial = _styleSource.fontSharedMaterial;
                textComponent.fontSize = _styleSource.fontSize;
                textComponent.enableWordWrapping = false;
            }
            else
            {
                textComponent.fontSize = 18f;
            }

            textComponent.text = text;
            textComponent.color = color;
            textComponent.raycastTarget = false;
            return textComponent;
        }

        private void CacheDropdownItems(GameObject dropdownRoot)
        {
            _items.Clear();

            foreach (Toggle toggle in dropdownRoot.GetComponentsInChildren<Toggle>(true))
            {
                TMP_Text label = toggle.GetComponentInChildren<TMP_Text>(true);
                if (label == null)
                {
                    continue;
                }

                _items.Add(new DropdownItemEntry
                {
                    Root = toggle.gameObject,
                    SearchText = Normalize(label.text),
                    Rect = toggle.transform as RectTransform,
                    OriginalSiblingIndex = toggle.transform.GetSiblingIndex(),
                    OriginalAnchoredPosition = (toggle.transform as RectTransform) != null
                        ? ((RectTransform)toggle.transform).anchoredPosition
                        : Vector2.zero,
                    OriginalAnchorMin = (toggle.transform as RectTransform) != null
                        ? ((RectTransform)toggle.transform).anchorMin
                        : Vector2.zero,
                    OriginalAnchorMax = (toggle.transform as RectTransform) != null
                        ? ((RectTransform)toggle.transform).anchorMax
                        : Vector2.one,
                    OriginalPivot = (toggle.transform as RectTransform) != null
                        ? ((RectTransform)toggle.transform).pivot
                        : new Vector2(0.5f, 0.5f),
                    OriginalSizeDelta = (toggle.transform as RectTransform) != null
                        ? ((RectTransform)toggle.transform).sizeDelta
                        : Vector2.zero
                });
            }
        }

        private void CaptureOriginalOptions()
        {
            if (_optionsCaptured || _dropdown == null) return;

            foreach (TMP_Dropdown.OptionData option in _dropdown.options)
            {
                _allOptions.Add(new TMP_Dropdown.OptionData(option.text, option.image));
            }

            _selectedOriginalIndex = _dropdown.value;
            _optionsCaptured = true;
        }

        private void QueueDropdownRebuild()
        {
            CaptureOriginalOptions();
            if (_rebuildCoroutine == null)
            {
                _rebuildCoroutine = StartCoroutine(RebuildDropdownNextFrame());
            }
        }

        private IEnumerator RebuildDropdownNextFrame()
        {
            yield return null;
            _rebuildCoroutine = null;
            if (_dropdown == null) yield break;

            CaptureOriginalOptions();
            string[] tokens = Normalize(_searchText).Split(new char[] { (char)32 }, StringSplitOptions.RemoveEmptyEntries);
            _visibleOptionIndices.Clear();
            for (int i = 0; i < _allOptions.Count; i++)
            {
                string text = Normalize(_allOptions[i].text);
                if (tokens.Length == 0 || tokens.All(token => text.Contains(token)))
                {
                    _visibleOptionIndices.Add(i);
                }
            }

            _rebuildInProgress = true;
            _dropdown.Hide();
            _dropdown.options.Clear();
            if (_visibleOptionIndices.Count == 0)
            {
                _dropdown.options.Add(new TMP_Dropdown.OptionData("No matches"));
                _visibleOptionIndices.Add(-1);
            }
            else
            {
                foreach (int originalIndex in _visibleOptionIndices)
                {
                    TMP_Dropdown.OptionData option = _allOptions[originalIndex];
                    _dropdown.options.Add(new TMP_Dropdown.OptionData(option.text, option.image));
                }
            }

            int displayedIndex = _visibleOptionIndices.IndexOf(_selectedOriginalIndex);
            _dropdown.SetValueWithoutNotify(displayedIndex >= 0 ? displayedIndex : 0);
            _dropdown.RefreshShownValue();
            _isFiltered = tokens.Length > 0;
            _dropdown.Show();
            _rebuildInProgress = false;
        }

        private void RestoreFullOptions()
        {
            if (!_optionsCaptured || !_isFiltered || _dropdown == null) return;

            _dropdown.options.Clear();
            foreach (TMP_Dropdown.OptionData option in _allOptions)
            {
                _dropdown.options.Add(new TMP_Dropdown.OptionData(option.text, option.image));
            }
            _dropdown.SetValueWithoutNotify(Mathf.Clamp(_selectedOriginalIndex, 0, _allOptions.Count - 1));
            _dropdown.RefreshShownValue();
            _visibleOptionIndices.Clear();
            _isFiltered = false;
        }

        public bool TryRemapSelection(ref int index)
        {
            if (!_isFiltered) return true;
            if (index < 0 || index >= _visibleOptionIndices.Count || _visibleOptionIndices[index] < 0) return false;

            _selectedOriginalIndex = _visibleOptionIndices[index];
            RestoreFullOptions();
            index = _selectedOriginalIndex;
            return true;
        }

        private void OnSearchValueChanged(string value)
        {
            _searchText = value ?? string.Empty;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (_items.Count == 0 || _contentRect == null) return;

            string[] tokens = Normalize(_searchText).Split(new char[] { (char)32 }, StringSplitOptions.RemoveEmptyEntries);
            bool hasSearch = tokens.Length > 0;
            List<DropdownItemEntry> matches = new List<DropdownItemEntry>();
            foreach (DropdownItemEntry item in _items)
            {
                bool visible = !hasSearch || tokens.All(token => item.SearchText.Contains(token));
                item.Root.SetActive(visible);
                if (visible) matches.Add(item);
            }

            float rowHeight = GetRowHeight();
            if (hasSearch)
            {
                for (int i = 0; i < matches.Count; i++)
                {
                    RectTransform row = matches[i].Rect;
                    if (row == null) continue;
                    row.anchorMin = new Vector2(0f, 1f);
                    row.anchorMax = new Vector2(1f, 1f);
                    row.pivot = new Vector2(0.5f, 1f);
                    row.sizeDelta = new Vector2(0f, rowHeight);
                    row.anchoredPosition = new Vector2(0f, -rowHeight * i);
                }

                _contentRect.anchorMin = new Vector2(0f, 1f);
                _contentRect.anchorMax = new Vector2(1f, 1f);
                _contentRect.pivot = new Vector2(0.5f, 1f);
                _contentRect.anchoredPosition = Vector2.zero;
                _contentRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(rowHeight, rowHeight * matches.Count));

                RectTransform popup = _activeDropdownRoot == null ? null : _activeDropdownRoot.transform as RectTransform;
                if (popup != null && _originalDropdownHeight > 0f)
                {
                    popup.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Min(_originalDropdownHeight, Mathf.Max(rowHeight, rowHeight * matches.Count)));
                }
            }
            else
            {
                foreach (DropdownItemEntry item in _items)
                {
                    RectTransform row = item.Rect;
                    if (row == null) continue;
                    row.anchorMin = item.OriginalAnchorMin;
                    row.anchorMax = item.OriginalAnchorMax;
                    row.pivot = item.OriginalPivot;
                    row.sizeDelta = item.OriginalSizeDelta;
                    row.anchoredPosition = item.OriginalAnchoredPosition;
                }

                _contentRect.anchorMin = _originalContentAnchorMin;
                _contentRect.anchorMax = _originalContentAnchorMax;
                _contentRect.pivot = _originalContentPivot;
                _contentRect.sizeDelta = _originalContentSizeDelta;
                _contentRect.anchoredPosition = _originalContentAnchoredPosition;
                RectTransform popup = _activeDropdownRoot == null ? null : _activeDropdownRoot.transform as RectTransform;
                if (popup != null && _originalDropdownHeight > 0f)
                {
                    popup.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, _originalDropdownHeight);
                }
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_contentRect);
            ScrollRect scrollRect = _contentRect.GetComponentInParent<ScrollRect>();
            if (scrollRect != null)
            {
                bool needsScroll = hasSearch && matches.Count * rowHeight > _originalDropdownHeight;
                scrollRect.vertical = needsScroll;
                if (scrollRect.verticalScrollbar != null) scrollRect.verticalScrollbar.gameObject.SetActive(needsScroll);
                scrollRect.verticalNormalizedPosition = 1f;
            }
        }

        private void KeepFilteredResultsAtTop()
        {
            if (_contentRect == null || string.IsNullOrWhiteSpace(_searchText))
            {
                return;
            }

            ScrollRect scrollRect = _contentRect.GetComponentInParent<ScrollRect>();
            if (scrollRect == null)
            {
                return;
            }

            PinScrollContentToTop(scrollRect);
        }

        private void StartForceTopRoutine()
        {
            if (_forceTopCoroutine != null)
            {
                return;
            }

            _forceTopCoroutine = StartCoroutine(ForceTopAtEndOfFrame());
        }

        private void StopForceTopRoutine()
        {
            if (_forceTopCoroutine == null)
            {
                return;
            }

            StopCoroutine(_forceTopCoroutine);
            _forceTopCoroutine = null;
        }

        private IEnumerator ForceTopAtEndOfFrame()
        {
            while (_activeDropdownRoot != null && !string.IsNullOrWhiteSpace(_searchText))
            {
                yield return new WaitForEndOfFrame();
                KeepFilteredResultsAtTop();
            }

            _forceTopCoroutine = null;
        }

        private void PinScrollContentToTop(ScrollRect scrollRect)
        {
            if (scrollRect == null || scrollRect.content == null)
            {
                return;
            }

            scrollRect.velocity = Vector2.zero;
            scrollRect.verticalNormalizedPosition = 1f;
        }

        private float GetRowHeight()
        {
            foreach (DropdownItemEntry item in _items)
            {
                RectTransform rowRect = item.Rect;
                if (rowRect != null && rowRect.rect.height > 0f)
                {
                    return rowRect.rect.height;
                }

                LayoutElement layout = item.Root.GetComponent<LayoutElement>();
                if (layout != null && layout.preferredHeight > 0f)
                {
                    return layout.preferredHeight;
                }
            }

            return 30f;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private sealed class DropdownItemEntry
        {
            public GameObject Root;
            public string SearchText;
            public RectTransform Rect;
            public int OriginalSiblingIndex;
            public Vector2 OriginalAnchoredPosition;
            public Vector2 OriginalAnchorMin;
            public Vector2 OriginalAnchorMax;
            public Vector2 OriginalPivot;
            public Vector2 OriginalSizeDelta;
        }
    }

    public class SearchFieldClickProxy : MonoBehaviour, IPointerClickHandler
    {
        public WeaponDropdownSearchController Owner;

        public void OnPointerClick(PointerEventData eventData)
        {
            Owner?.FocusSearchField();
        }
    }

}
