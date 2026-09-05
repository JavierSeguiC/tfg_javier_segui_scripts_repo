using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// THE EXPANDABILITY MECHANISM.
///
/// Settings are DATA, not UI. Anything that wants an adjustable knob registers a
/// SettingDefinition here; SettingsScreen builds its whole interface by iterating
/// this registry, so adding a setting is ONE Register() call and zero UI work and
/// zero scene work.
///
///     SettingsRegistry.RegisterFloat("game.holdChance", "Hold note chance",
///         "Gameplay", 0.5f, 0f, 1f, v =&gt; GameDifficulty.Instance.holdNoteChance = v);
///
/// Persistence: JSON in Application.persistentDataPath/settings.json.
///
/// SCHEMA VERSIONING — values are stored as an id→string list, not a fixed struct.
/// On load, an id present in the file but not in the registry is IGNORED (a setting
/// you removed), and an id in the registry but missing from the file falls back to
/// its DefaultValue (a setting you added since the file was written). So old save
/// files never break and never need migrating. SCHEMA_VERSION is bumped only if the
/// meaning of an existing id changes, which triggers a full reset to defaults.
/// </summary>
public enum SettingType { Bool, Float, Int, Enum }

public class SettingDefinition
{
    public string id;
    public string label;
    public string category;
    public string tooltip;
    public SettingType type;

    public object defaultValue;      // bool / float / int
    public float min, max;           // Float + Int only
    public string[] options;         // Enum only (value is the index)

    /// <summary>Pushes the value into the game. Called on load, on change, on reset.</summary>
    public Action<object> onApply;
}

public static class SettingsRegistry
{
    /// <summary>Bump ONLY when an existing id changes meaning — forces a reset to defaults.</summary>
    public const int SCHEMA_VERSION = 1;

    private const string FILE_NAME = "settings.json";

    private static readonly List<SettingDefinition> _defs = new List<SettingDefinition>();
    private static readonly Dictionary<string, SettingDefinition> _byId =
        new Dictionary<string, SettingDefinition>();
    private static readonly Dictionary<string, object> _values = new Dictionary<string, object>();

    private static bool _loaded;

    /// <summary>Fired when any value changes (id). UI listens to refresh "modified" styling.</summary>
    public static event Action<string> OnSettingChanged;

    // Statics survive between Play sessions when "Enter Play Mode" has domain reload
    // DISABLED (the default fast-enter setting). Without an explicit reset, _byId still
    // holds last session's definitions, so a re-registering component (the DDA bridge)
    // hits the duplicate guard and the registry KEEPS the previous session's
    // SettingDefinition — whose onApply closure points at a now-destroyed component.
    // That dead closure is why controller selection silently stopped working and stayed
    // broken across reruns ("locked to manual"). DifficultyAuthority already does this;
    // the settings statics must too. Runs before any Awake, so the registry is always
    // rebuilt fresh each Play.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        _defs.Clear();
        _byId.Clear();
        _values.Clear();
        _loaded = false;
        _cachedFile = null;
        OnSettingChanged = null;
    }

    public static IReadOnlyList<SettingDefinition> Definitions => _defs;

    private static string FilePath => Path.Combine(Application.persistentDataPath, FILE_NAME);

    // ================================================================
    // Registration
    // ================================================================

    public static void Register(SettingDefinition def)
    {
        if (def == null || string.IsNullOrEmpty(def.id)) return;
        if (_byId.ContainsKey(def.id))
        {
            Debug.LogWarning($"[SettingsRegistry] Duplicate setting id '{def.id}' ignored.");
            return;
        }

        _defs.Add(def);
        _byId[def.id] = def;

        // If the file was already loaded (a late registrant, e.g. the DDA bridge),
        // adopt the stored value now; otherwise seed the default.
        if (!_values.ContainsKey(def.id))
            _values[def.id] = def.defaultValue;

        if (_loaded) LoadSingleFromDisk(def);

        Apply(def);
    }

    public static void RegisterBool(string id, string label, string category, bool def,
                                    Action<object> onApply, string tooltip = "")
        => Register(new SettingDefinition
        {
            id = id, label = label, category = category, tooltip = tooltip,
            type = SettingType.Bool, defaultValue = def, onApply = onApply
        });

    public static void RegisterFloat(string id, string label, string category, float def,
                                     float min, float max, Action<object> onApply,
                                     string tooltip = "")
        => Register(new SettingDefinition
        {
            id = id, label = label, category = category, tooltip = tooltip,
            type = SettingType.Float, defaultValue = def, min = min, max = max, onApply = onApply
        });

    public static void RegisterInt(string id, string label, string category, int def,
                                   int min, int max, Action<object> onApply,
                                   string tooltip = "")
        => Register(new SettingDefinition
        {
            id = id, label = label, category = category, tooltip = tooltip,
            type = SettingType.Int, defaultValue = def, min = min, max = max, onApply = onApply
        });

    public static void RegisterEnum(string id, string label, string category, int defIndex,
                                    string[] options, Action<object> onApply,
                                    string tooltip = "")
        => Register(new SettingDefinition
        {
            id = id, label = label, category = category, tooltip = tooltip,
            type = SettingType.Enum, defaultValue = defIndex, options = options, onApply = onApply
        });

    // ================================================================
    // Access
    // ================================================================

    public static SettingDefinition Get(string id)
        => _byId.TryGetValue(id, out var d) ? d : null;

    public static object GetValue(string id)
        => _values.TryGetValue(id, out var v) ? v : null;

    public static bool  GetBool (string id, bool  fallback = false)
        => GetValue(id) is bool b ? b : fallback;
    public static float GetFloat(string id, float fallback = 0f)
        => GetValue(id) is float f ? f : fallback;
    public static int   GetInt  (string id, int   fallback = 0)
        => GetValue(id) is int i ? i : fallback;

    /// <summary>True if this setting differs from its default (drives the "changed" colour).</summary>
    public static bool IsModified(string id)
    {
        var def = Get(id);
        if (def == null) return false;
        return !Equals(_values[id], def.defaultValue);
    }

    public static void SetValue(string id, object value, bool save = true)
    {
        var def = Get(id);
        if (def == null) return;

        _values[id] = Coerce(def, value);
        Apply(def);
        OnSettingChanged?.Invoke(id);
        if (save) Save();
    }

    /// <summary>Restore every registered setting to its default and persist.</summary>
    public static void ResetAllToDefaults()
    {
        foreach (var def in _defs)
        {
            _values[def.id] = def.defaultValue;
            Apply(def);
            OnSettingChanged?.Invoke(def.id);
        }
        Save();
        Debug.Log("[SettingsRegistry] All settings reset to defaults.");
    }

    private static object Coerce(SettingDefinition def, object v)
    {
        try
        {
            switch (def.type)
            {
                case SettingType.Bool:  return Convert.ToBoolean(v);
                case SettingType.Float: return Mathf.Clamp(Convert.ToSingle(v), def.min, def.max);
                case SettingType.Int:   return Mathf.Clamp(Convert.ToInt32(v),
                                                           Mathf.RoundToInt(def.min),
                                                           Mathf.RoundToInt(def.max));
                case SettingType.Enum:
                    int n = def.options != null ? def.options.Length : 1;
                    return Mathf.Clamp(Convert.ToInt32(v), 0, Mathf.Max(0, n - 1));
            }
        }
        catch { /* fall through to default */ }
        return def.defaultValue;
    }

    private static void Apply(SettingDefinition def)
    {
        try { def.onApply?.Invoke(_values[def.id]); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Re-push every setting into the game (call after scene systems exist).</summary>
    public static void ApplyAll()
    {
        foreach (var def in _defs) Apply(def);
    }

    // ================================================================
    // Persistence
    // ================================================================

    [Serializable] private class Entry { public string id; public string value; }
    [Serializable] private class SaveFile { public int version; public List<Entry> entries = new List<Entry>(); }

    private static SaveFile _cachedFile;

    public static void Load()
    {
        _loaded = true;
        _cachedFile = null;

        try
        {
            if (!File.Exists(FilePath)) { Save(); return; }

            var file = JsonUtility.FromJson<SaveFile>(File.ReadAllText(FilePath));
            if (file == null) return;

            if (file.version != SCHEMA_VERSION)
            {
                Debug.LogWarning($"[SettingsRegistry] Schema version {file.version} != " +
                                 $"{SCHEMA_VERSION}. Resetting settings to defaults.");
                ResetAllToDefaults();
                return;
            }

            _cachedFile = file;
            foreach (var def in _defs) LoadSingleFromDisk(def);
            ApplyAll();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SettingsRegistry] Load failed ({e.Message}) — using defaults.");
        }
    }

    /// <summary>Pull one setting out of the cached file. Missing id → keeps its default.</summary>
    private static void LoadSingleFromDisk(SettingDefinition def)
    {
        if (_cachedFile == null) return;

        foreach (var e in _cachedFile.entries)
        {
            if (e.id != def.id) continue;
            _values[def.id] = Parse(def, e.value);
            Apply(def);
            return;
        }
        // Not in the file: a setting added since it was written. Default stands.
    }

    private static object Parse(SettingDefinition def, string s)
    {
        try
        {
            switch (def.type)
            {
                case SettingType.Bool:  return bool.Parse(s);
                case SettingType.Float: return Coerce(def, float.Parse(s, CultureInfo.InvariantCulture));
                case SettingType.Int:
                case SettingType.Enum:  return Coerce(def, int.Parse(s, CultureInfo.InvariantCulture));
            }
        }
        catch { }
        return def.defaultValue;
    }

    public static void Save()
    {
        try
        {
            var file = new SaveFile { version = SCHEMA_VERSION };
            foreach (var def in _defs)
            {
                object v = _values[def.id];
                string s = v is float f
                    ? f.ToString("R", CultureInfo.InvariantCulture)
                    : Convert.ToString(v, CultureInfo.InvariantCulture);
                file.entries.Add(new Entry { id = def.id, value = s });
            }
            File.WriteAllText(FilePath, JsonUtility.ToJson(file, true));
            _cachedFile = file;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SettingsRegistry] Save failed: {e.Message}");
        }
    }
}
