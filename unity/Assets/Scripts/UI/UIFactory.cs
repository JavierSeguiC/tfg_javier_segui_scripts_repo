using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Builds every UI widget from code, so no menu is ever assembled by hand in the
/// scene. Screens call these helpers; adding a screen never means dragging
/// references in the inspector.
///
/// Deliberately plain: flat colours, no sprites, no prefabs, no external assets.
/// Aesthetics are explicitly a lesser concern for now — the palette below is the
/// single place to restyle everything later.
///
/// FONT: uses TMP_Settings.defaultFontAsset (set by "Window > TextMeshPro >
/// Import TMP Essential Resources"). Assign UIFactory.font to override.
/// </summary>
public static class UIFactory
{
    // ---------------- palette ----------------
    public static readonly Color Bg          = new Color(0.11f, 0.12f, 0.14f, 0.98f);
    public static readonly Color Panel       = new Color(0.17f, 0.18f, 0.21f, 1f);
    public static readonly Color PanelAlt    = new Color(0.22f, 0.23f, 0.27f, 1f);
    public static readonly Color Accent      = new Color(0.30f, 0.55f, 0.85f, 1f);
    public static readonly Color Danger      = new Color(0.75f, 0.30f, 0.30f, 1f);
    public static readonly Color Success     = new Color(0.30f, 0.65f, 0.40f, 1f);
    public static readonly Color TextMain    = new Color(0.92f, 0.93f, 0.95f, 1f);
    public static readonly Color TextDim     = new Color(0.62f, 0.64f, 0.68f, 1f);
    /// <summary>Colour for a setting whose value differs from its default.</summary>
    public static readonly Color TextModified = new Color(0.95f, 0.75f, 0.25f, 1f);

    public static TMP_FontAsset font;

    private static TMP_FontAsset Font
        => font != null ? font : TMP_Settings.defaultFontAsset;

    // ================================================================
    // Core
    // ================================================================

    public static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>Stretch a RectTransform to fill its parent, with optional padding.</summary>
    public static RectTransform Fill(RectTransform rt, float pad = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(pad, pad);
        rt.offsetMax = new Vector2(-pad, -pad);
        return rt;
    }

    public static Image Box(string name, Transform parent, Color color)
    {
        var rt = Rect(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        return img;
    }

    /// <summary>Full-screen background panel — the root of a screen.</summary>
    public static RectTransform ScreenRoot(string name, Transform parent, bool opaque = true)
    {
        var img = Box(name, parent, opaque ? Bg : new Color(0f, 0f, 0f, 0.6f));
        return Fill((RectTransform)img.transform);
    }

    public static TMP_Text Label(string text, Transform parent, int size = 20,
                                 TextAlignmentOptions align = TextAlignmentOptions.Left,
                                 Color? color = null)
    {
        var rt = Rect("Label", parent);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (Font != null) t.font = Font;
        t.text = text;
        t.fontSize = size;
        t.alignment = align;
        t.color = color ?? TextMain;
        t.richText = true;
        return t;
    }

    // NOTE: this method's name is the same as UnityEngine.UI.Button. That shadows
    // the type for every UNQUALIFIED "Button" reference anywhere else in this
    // class too (not just inside this method) — hence the fully-qualified
    // UnityEngine.UI.Button below and at every other call site in this file.
    public static UnityEngine.UI.Button Button(string text, Transform parent, Action onClick,
                                Color? color = null, int fontSize = 20, float height = 44f)
    {
        var img = Box("Button", parent, color ?? PanelAlt);
        var btn = img.gameObject.AddComponent<UnityEngine.UI.Button>();
        btn.targetGraphic = img;

        var le = img.gameObject.AddComponent<LayoutElement>();
        le.minHeight = height;
        le.preferredHeight = height;

        var label = Label(text, img.transform, fontSize, TextAlignmentOptions.Center);
        Fill((RectTransform)label.transform, 6f);

        if (onClick != null) btn.onClick.AddListener(() => onClick());
        return btn;
    }

    /// <summary>Change a button's label without holding a reference to the text.</summary>
    public static void SetButtonText(UnityEngine.UI.Button b, string text)
    {
        if (b == null) return;
        var t = b.GetComponentInChildren<TMP_Text>();
        if (t != null) t.text = text;
    }

    // ================================================================
    // Layout containers
    // ================================================================

    public static VerticalLayoutGroup Column(Transform parent, float spacing = 8f,
                                             RectOffset padding = null)
    {
        var rt = Rect("Column", parent);
        var v = rt.gameObject.AddComponent<VerticalLayoutGroup>();
        v.spacing = spacing;
        v.padding = padding ?? new RectOffset(0, 0, 0, 0);
        v.childControlWidth = true;
        v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;
        return v;
    }

    public static HorizontalLayoutGroup Row(Transform parent, float spacing = 8f,
                                            float height = 44f)
    {
        var rt = Rect("Row", parent);
        var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = spacing;
        h.childControlWidth = true;
        h.childControlHeight = true;
        h.childForceExpandWidth = true;
        h.childForceExpandHeight = false;

        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.minHeight = height;
        le.preferredHeight = height;
        return h;
    }

    /// <summary>
    /// A vertically scrolling area. Returns the CONTENT transform to parent items
    /// to; it auto-sizes to its children.
    /// </summary>
    public static RectTransform ScrollColumn(Transform parent, float spacing = 8f)
    {
        var viewportImg = Box("Scroll", parent, new Color(0f, 0f, 0f, 0f));
        var scrollRT = (RectTransform)viewportImg.transform;

        // CRITICAL: a fresh RectTransform defaults to zero size. Without this the
        // whole scroll area is 0x0 and every row inside it is invisible — which is
        // exactly what made the profile, settings and diagnostics lists look empty.
        Fill(scrollRT);

        var scroll = scrollRT.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 25f;

        var viewport = Rect("Viewport", scrollRT);
        Fill(viewport);
        // Transparent (alpha 0) but raycastable, so drags over empty space still scroll.
        var vpImg = viewport.gameObject.AddComponent<Image>();
        vpImg.color = new Color(0f, 0f, 0f, 0f);
        // RectMask2D, NOT Mask: a plain Mask clips via the stencil buffer, which a
        // near-transparent mask graphic fails to write under URP (children get culled
        // and turn invisible while still taking clicks). RectMask2D clips by rectangle,
        // needs no mask graphic, and also clips raycasts.
        viewport.gameObject.AddComponent<RectMask2D>();
        scroll.viewport = viewport;

        var content = Rect("Content", viewport);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = new Vector2(0f, 0f);

        var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = spacing;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.content = content;
        return content;
    }

    /// <summary>Fixed-height spacer for breathing room inside a column.</summary>
    public static void Spacer(Transform parent, float height = 12f)
    {
        var rt = Rect("Spacer", parent);
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.minHeight = height;
        le.preferredHeight = height;
    }

    // ================================================================
    // Cards — list rows that size themselves
    // ================================================================
    //
    // Column()/Row() attach their layout group to a NEW CHILD object. That is fine
    // for filling a fixed-size panel, but useless for a list row: the row's own
    // RectTransform then reports no preferred height, so a parent VerticalLayoutGroup
    // can't size it and you must guess a minHeight by hand (which clips content).
    //
    // A Card puts the layout group ON the box itself. The box then implements
    // ILayoutElement, so the parent list asks it for its preferred height and gets
    // the true sum of its children. Rows auto-fit their content, always.

    /// <summary>A coloured list row whose height follows its stacked children.</summary>
    public static RectTransform CardColumn(string name, Transform parent, Color color,
                                           float spacing = 4f, RectOffset pad = null)
    {
        var img = Box(name, parent, color);
        var rt = (RectTransform)img.transform;

        var v = rt.gameObject.AddComponent<VerticalLayoutGroup>();
        v.spacing = spacing;
        v.padding = pad ?? new RectOffset(12, 12, 8, 8);
        v.childControlWidth = true;
        v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;
        return rt;
    }

    /// <summary>A coloured list row laying its children out left-to-right.</summary>
    public static RectTransform CardRow(string name, Transform parent, Color color,
                                        float height, float spacing = 6f, RectOffset pad = null)
    {
        var img = Box(name, parent, color);
        var rt = (RectTransform)img.transform;

        var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = spacing;
        h.padding = pad ?? new RectOffset(10, 10, 6, 6);
        h.childControlWidth = true;
        h.childControlHeight = true;
        h.childForceExpandWidth = true;
        h.childForceExpandHeight = false;
        h.childAlignment = TextAnchor.MiddleLeft;

        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.minHeight = height;
        le.preferredHeight = height;
        return rt;
    }

    // ================================================================
    // Controls
    // ================================================================

    // Same shadowing note as Button() above: fully-qualified UnityEngine.UI.Slider.
    public static UnityEngine.UI.Slider Slider(Transform parent, float min, float max, float value,
                                Action<float> onChanged, bool wholeNumbers = false)
    {
        var rt = Rect("Slider", parent);
        var slider = rt.gameObject.AddComponent<UnityEngine.UI.Slider>();

        var bg = Box("Background", rt, PanelAlt);
        var bgRT = Fill((RectTransform)bg.transform);
        bgRT.anchorMin = new Vector2(0f, 0.35f);
        bgRT.anchorMax = new Vector2(1f, 0.65f);
        bgRT.offsetMin = bgRT.offsetMax = Vector2.zero;

        var fillArea = Rect("Fill Area", rt);
        fillArea.anchorMin = new Vector2(0f, 0.35f);
        fillArea.anchorMax = new Vector2(1f, 0.65f);
        fillArea.offsetMin = fillArea.offsetMax = Vector2.zero;

        var fill = Box("Fill", fillArea, Accent);
        var fillRT = (RectTransform)fill.transform;
        fillRT.anchorMin = Vector2.zero;
        fillRT.anchorMax = Vector2.one;
        fillRT.offsetMin = fillRT.offsetMax = Vector2.zero;

        var handleArea = Rect("Handle Slide Area", rt);
        Fill(handleArea);
        var handle = Box("Handle", handleArea, TextMain);
        var handleRT = (RectTransform)handle.transform;
        // Slider drives the handle's X anchors only; Y must be stretched explicitly
        // or the handle ends up zero-height and invisible.
        handleRT.anchorMin = new Vector2(0f, 0f);
        handleRT.anchorMax = new Vector2(0f, 1f);
        handleRT.pivot = new Vector2(0.5f, 0.5f);
        handleRT.sizeDelta = new Vector2(16f, 0f);
        handleRT.anchoredPosition = Vector2.zero;

        slider.fillRect = fillRT;
        slider.handleRect = handleRT;
        slider.targetGraphic = handle;
        slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
        slider.minValue = min;
        slider.maxValue = max;
        slider.wholeNumbers = wholeNumbers;
        slider.SetValueWithoutNotify(value);

        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 28f;
        le.preferredHeight = 28f;

        if (onChanged != null) slider.onValueChanged.AddListener(v => onChanged(v));
        return slider;
    }

    // Same shadowing note as Button() above: fully-qualified UnityEngine.UI.Toggle.
    //
    // The WHOLE bar is the click target, not just the checkbox. The previous version
    // only made a 26px box raycastable, so clicking anywhere else on the row did
    // nothing — which read as "the toggle can't be switched on".
    public static UnityEngine.UI.Toggle Toggle(Transform parent, bool value, Action<bool> onChanged)
    {
        // Full-width bar = the toggle's own graphic and click surface.
        var bar = Box("Toggle", parent, PanelAlt);
        var rt = (RectTransform)bar.transform;
        var toggle = rt.gameObject.AddComponent<UnityEngine.UI.Toggle>();
        toggle.targetGraphic = bar;          // raycastable across the entire bar
        toggle.transition = Selectable.Transition.ColorTint;

        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 34f;
        le.preferredHeight = 34f;

        // Checkbox on the left.
        var boxImg = Box("Box", rt, Bg);
        var boxRT = (RectTransform)boxImg.transform;
        boxRT.anchorMin = new Vector2(0f, 0.5f);
        boxRT.anchorMax = new Vector2(0f, 0.5f);
        boxRT.pivot = new Vector2(0f, 0.5f);
        boxRT.sizeDelta = new Vector2(24f, 24f);
        boxRT.anchoredPosition = new Vector2(6f, 0f);
        boxImg.raycastTarget = false;

        var check = Box("Checkmark", boxRT, Success);
        var checkRT = Fill((RectTransform)check.transform);
        checkRT.offsetMin = new Vector2(4f, 4f);
        checkRT.offsetMax = new Vector2(-4f, -4f);
        check.raycastTarget = false;

        // "On / Off" text to the right of the box, so state is legible on the bar itself.
        var stateLabel = Label(value ? "On" : "Off", rt, 16, TextAlignmentOptions.Left);
        var slRT = (RectTransform)stateLabel.transform;
        slRT.anchorMin = new Vector2(0f, 0f);
        slRT.anchorMax = new Vector2(1f, 1f);
        slRT.offsetMin = new Vector2(38f, 0f);
        slRT.offsetMax = new Vector2(-8f, 0f);
        stateLabel.raycastTarget = false;

        toggle.graphic = check;
        toggle.toggleTransition = UnityEngine.UI.Toggle.ToggleTransition.None;   // instant, no fade ambiguity
        toggle.SetIsOnWithoutNotify(value);
        // SetIsOnWithoutNotify is a NO-OP when value already matches the Toggle's own
        // internal default (false) — meaning PlayEffect never runs and the checkmark
        // is left at its Image component's default alpha (1 = visible), showing
        // "checked" even when value is false. Set the alpha ourselves so the initial
        // visual is always correct regardless of that quirk.
        check.canvasRenderer.SetAlpha(value ? 1f : 0f);

        toggle.onValueChanged.AddListener(v =>
        {
            stateLabel.text = v ? "On" : "Off";
            onChanged?.Invoke(v);
        });
        return toggle;
    }

    public static TMP_Dropdown Dropdown(Transform parent, string[] options, int value,
                                        Action<int> onChanged)
    {
        var img = Box("Dropdown", parent, PanelAlt);
        var rt = (RectTransform)img.transform;
        var dd = img.gameObject.AddComponent<TMP_Dropdown>();
        dd.targetGraphic = img;

        var labelT = Label(" ", rt, 18, TextAlignmentOptions.Left);
        var labelRT = Fill((RectTransform)labelT.transform, 8f);
        labelRT.offsetMax = new Vector2(-25f, -6f);
        dd.captionText = labelT;

        // Template (built once, cloned by TMP_Dropdown at runtime).
        var template = Box("Template", rt, Panel);
        var templateRT = (RectTransform)template.transform;
        templateRT.anchorMin = new Vector2(0f, 0f);
        templateRT.anchorMax = new Vector2(1f, 0f);
        templateRT.pivot = new Vector2(0.5f, 1f);
        templateRT.anchoredPosition = new Vector2(0f, 2f);
        templateRT.sizeDelta = new Vector2(0f, 150f);

        var tScroll = templateRT.gameObject.AddComponent<ScrollRect>();
        var tViewport = Rect("Viewport", templateRT);
        Fill(tViewport);
        var tvpImg = tViewport.gameObject.AddComponent<Image>();
        tvpImg.color = new Color(0f, 0f, 0f, 0f);
        tViewport.gameObject.AddComponent<RectMask2D>();   // RectMask2D, not Mask (see ScrollColumn)

        var tContent = Rect("Content", tViewport);
        tContent.anchorMin = new Vector2(0f, 1f);
        tContent.anchorMax = new Vector2(1f, 1f);
        tContent.pivot = new Vector2(0.5f, 1f);
        tContent.sizeDelta = new Vector2(0f, 32f);

        var tItem = Rect("Item", tContent);
        tItem.anchorMin = new Vector2(0f, 0.5f);
        tItem.anchorMax = new Vector2(1f, 0.5f);
        tItem.sizeDelta = new Vector2(0f, 32f);
        var itemToggle = tItem.gameObject.AddComponent<UnityEngine.UI.Toggle>();

        var itemBg = Box("Item Background", tItem, PanelAlt);
        Fill((RectTransform)itemBg.transform);
        var itemChecked = Box("Item Checkmark", tItem, Accent);
        Fill((RectTransform)itemChecked.transform);
        var itemLabel = Label("Option", tItem, 18, TextAlignmentOptions.Left);
        Fill((RectTransform)itemLabel.transform, 8f);

        itemToggle.targetGraphic = itemBg;
        itemToggle.graphic = itemChecked;

        tScroll.content = tContent;
        tScroll.viewport = tViewport;
        tScroll.horizontal = false;
        tScroll.movementType = ScrollRect.MovementType.Clamped;

        dd.template = templateRT;
        dd.itemText = itemLabel;
        template.gameObject.SetActive(false);

        dd.ClearOptions();
        if (options != null) dd.AddOptions(new System.Collections.Generic.List<string>(options));
        dd.SetValueWithoutNotify(value);
        dd.RefreshShownValue();

        var le = img.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 34f;
        le.preferredHeight = 34f;

        if (onChanged != null) dd.onValueChanged.AddListener(v => onChanged(v));
        return dd;
    }

    public static TMP_InputField InputField(Transform parent, string placeholder,
                                            string value = "", float height = 36f)
    {
        var img = Box("InputField", parent, PanelAlt);
        var rt = (RectTransform)img.transform;
        var field = img.gameObject.AddComponent<TMP_InputField>();
        field.targetGraphic = img;

        var area = Rect("Text Area", rt);
        Fill(area, 8f);
        area.gameObject.AddComponent<RectMask2D>();

        var ph = Label(placeholder, area, 18, TextAlignmentOptions.Left, TextDim);
        Fill((RectTransform)ph.transform);

        var txt = Label("", area, 18, TextAlignmentOptions.Left);
        Fill((RectTransform)txt.transform);

        field.textViewport = area;
        field.textComponent = txt;
        field.placeholder = ph;
        field.text = value;

        var le = img.gameObject.AddComponent<LayoutElement>();
        le.minHeight = height;
        le.preferredHeight = height;
        return field;
    }
}
