using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Generated ENTIRELY from SettingsRegistry — this file contains no knowledge of
/// any individual setting. Add a Register*() call anywhere and a correctly-typed
/// row appears here automatically, grouped under its category.
///
/// A setting whose value differs from its default is drawn in
/// UIFactory.TextModified so changes are visible at a glance.
///
/// Built once, then only the value widgets and label colours are refreshed. The
/// screen is reachable from both the main menu and the pause menu; a back button
/// returns to whichever opened it.
/// </summary>
public class SettingsScreen : UIScreen
{
    private RectTransform _list;
    private readonly Dictionary<string, TMP_Text> _labels = new Dictionary<string, TMP_Text>();
    private readonly Dictionary<string, TMP_Text> _valueLabels = new Dictionary<string, TMP_Text>();
    // Per-setting closures that push the registry value back into the control widget,
    // so opening Settings always shows the true current value (incl. external changes).
    private readonly Dictionary<string, System.Action> _syncers = new Dictionary<string, System.Action>();
    private bool _built;

    protected override void Build()
    {
        var col = Panel("Settings", 720f, 820f);

        UIFactory.Label("Modified settings are shown in amber.", col, 15,
                        TextAlignmentOptions.Left, UIFactory.TextDim)
                 .gameObject.AddComponent<LayoutElement>().minHeight = 22f;

        var scrollHost = UIFactory.Rect("SettingsScrollHost", col);
        var le = scrollHost.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 520f;
        le.flexibleHeight = 1f;
        _list = UIFactory.ScrollColumn(scrollHost, 6f);

        UIFactory.Spacer(col, 6f);

        var row = UIFactory.Row(col, 10f, 50f);
        UIFactory.Button("Reset all to defaults", row.transform, ConfirmReset, UIFactory.Danger);
        UIFactory.Button("Back", row.transform, () => Manager.CloseSettings(), UIFactory.PanelAlt);
    }

    protected override void OnShow()
    {
        // Deferred until first show so late registrants (the DDA bridge, which
        // registers in its own Awake) are already present.
        if (!_built) { BuildRows(); _built = true; }
        foreach (var kv in _syncers) kv.Value();   // pull widgets up to current values
        RefreshAll();
    }

    private void BuildRows()
    {
        foreach (Transform child in _list) Object.Destroy(child.gameObject);
        _labels.Clear();
        _valueLabels.Clear();
        _syncers.Clear();

        string lastCategory = null;

        foreach (var def in SettingsRegistry.Definitions)
        {
            if (def.category != lastCategory)
            {
                lastCategory = def.category;
                UIFactory.Spacer(_list, 8f);
                var header = UIFactory.Label(def.category.ToUpper(), _list, 15,
                                             TextAlignmentOptions.Left, UIFactory.Accent);
                header.gameObject.AddComponent<LayoutElement>().minHeight = 24f;
            }

            BuildRow(def);
        }
    }

    private void BuildRow(SettingDefinition def)
    {
        // CardColumn sizes itself from its children, so a row with a tooltip is
        // taller than one without and nothing gets clipped.
        var card = UIFactory.CardColumn("Setting_" + def.id, _list, UIFactory.PanelAlt, 4f);

        // Header line: label on the left, live value on the right.
        var headRow = UIFactory.Row(card, 6f, 26f);
        var label = UIFactory.Label(def.label, headRow.transform, 18, TextAlignmentOptions.Left);
        var valueLabel = UIFactory.Label("", headRow.transform, 18, TextAlignmentOptions.Right,
                                         UIFactory.TextDim);
        _labels[def.id] = label;
        _valueLabels[def.id] = valueLabel;

        switch (def.type)
        {
            case SettingType.Bool:
            {
                var t = UIFactory.Toggle(card, SettingsRegistry.GetBool(def.id),
                                         v => Commit(def.id, v));
                // Sync the widget FROM the registry (e.g. an external change) without notifying.
                _syncers[def.id] = () =>
                {
                    bool on = SettingsRegistry.GetBool(def.id);
                    t.SetIsOnWithoutNotify(on);
                    var chk = t.graphic; if (chk != null) chk.canvasRenderer.SetAlpha(on ? 1f : 0f);
                    var lbl = t.GetComponentInChildren<TMPro.TMP_Text>();
                    if (lbl != null) lbl.text = on ? "On" : "Off";
                };
                break;
            }

            case SettingType.Float:
            {
                var s = UIFactory.Slider(card, def.min, def.max,
                                         SettingsRegistry.GetFloat(def.id),
                                         v => Commit(def.id, v));
                _syncers[def.id] = () => s.SetValueWithoutNotify(SettingsRegistry.GetFloat(def.id));
                break;
            }

            case SettingType.Int:
            {
                var s = UIFactory.Slider(card, def.min, def.max,
                                         SettingsRegistry.GetInt(def.id),
                                         v => Commit(def.id, Mathf.RoundToInt(v)), true);
                _syncers[def.id] = () => s.SetValueWithoutNotify(SettingsRegistry.GetInt(def.id));
                break;
            }

            case SettingType.Enum:
            {
                var d = UIFactory.Dropdown(card, def.options,
                                           SettingsRegistry.GetInt(def.id),
                                           v => Commit(def.id, v));
                _syncers[def.id] = () =>
                {
                    d.SetValueWithoutNotify(SettingsRegistry.GetInt(def.id));
                    d.RefreshShownValue();
                };
                break;
            }
        }

        if (!string.IsNullOrEmpty(def.tooltip))
        {
            var tip = UIFactory.Label(def.tooltip, card, 13,
                                      TextAlignmentOptions.TopLeft, UIFactory.TextDim);
            tip.gameObject.AddComponent<LayoutElement>().minHeight = 18f;
        }
    }

    private void Commit(string id, object value)
    {
        SettingsRegistry.SetValue(id, value);   // applies + persists immediately
        RefreshRow(id);
    }

    private void RefreshAll()
    {
        foreach (var def in SettingsRegistry.Definitions) RefreshRow(def.id);
    }

    private void RefreshRow(string id)
    {
        var def = SettingsRegistry.Get(id);
        if (def == null) return;

        bool modified = SettingsRegistry.IsModified(id);

        if (_labels.TryGetValue(id, out var label) && label != null)
            label.color = modified ? UIFactory.TextModified : UIFactory.TextMain;

        if (_valueLabels.TryGetValue(id, out var vl) && vl != null)
        {
            vl.text = Format(def);
            vl.color = modified ? UIFactory.TextModified : UIFactory.TextDim;
        }
    }

    private static string Format(SettingDefinition def)
    {
        switch (def.type)
        {
            case SettingType.Bool:  return SettingsRegistry.GetBool(def.id) ? "on" : "off";
            case SettingType.Float: return SettingsRegistry.GetFloat(def.id).ToString("0.###");
            case SettingType.Int:   return SettingsRegistry.GetInt(def.id).ToString();
            case SettingType.Enum:
                int i = SettingsRegistry.GetInt(def.id);
                return (def.options != null && i >= 0 && i < def.options.Length)
                     ? def.options[i] : i.ToString();
        }
        return "";
    }

    private void ConfirmReset()
    {
        Manager.Confirm(
            "Reset all settings?",
            "Every setting returns to its default value. This cannot be undone.",
            "Reset", "Cancel",
            yes =>
            {
                if (!yes) return;
                SettingsRegistry.ResetAllToDefaults();
                BuildRows();      // rebuild so every widget shows its default
                RefreshAll();
            });
    }
}
