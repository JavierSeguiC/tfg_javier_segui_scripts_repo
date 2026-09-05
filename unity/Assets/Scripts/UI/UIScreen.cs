using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Base class for every screen. A screen owns one RectTransform subtree under the
/// shared canvas, builds it lazily on first Show(), and is then just toggled
/// active/inactive — rebuilding on every open would throw away scroll positions
/// and churn garbage.
///
/// Override Build() to construct the layout, and OnShow() for anything that must
/// refresh each time the screen opens (profile lists, session lists, etc.).
/// </summary>
public abstract class UIScreen
{
    protected UIManager Manager { get; private set; }
    public RectTransform Root { get; protected set; }
    public bool IsVisible => Root != null && Root.gameObject.activeSelf;

    /// <summary>Screens with this true are drawn over the game rather than replacing it.</summary>
    public virtual bool IsOverlay => false;

    public void Init(UIManager manager, Transform parent)
    {
        Manager = manager;
        Root = UIFactory.ScreenRoot(GetType().Name, parent, !IsOverlay);
        Build();
        Root.gameObject.SetActive(false);
    }

    protected abstract void Build();
    protected virtual void OnShow() { }
    protected virtual void OnHide() { }

    public void Show()
    {
        if (Root == null) return;
        Root.SetAsLastSibling();      // newest screen draws on top
        Root.gameObject.SetActive(true);
        OnShow();
    }

    public void Hide()
    {
        if (Root == null) return;
        OnHide();
        Root.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------
    // Shared layout helper: a centred panel with a title
    // ------------------------------------------------------------

    /// <summary>
    /// Builds a centred card and returns the column you should add content to.
    /// </summary>
    protected RectTransform Panel(string title, float width = 620f, float height = 720f)
    {
        var panel = UIFactory.Box("Panel", Root, UIFactory.Panel);
        var rt = (RectTransform)panel.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, height);

        var col = UIFactory.Column(rt, 10f, new RectOffset(24, 24, 20, 20));
        UIFactory.Fill((RectTransform)col.transform);

        var t = UIFactory.Label(title, col.transform, 30, TextAlignmentOptions.Left);
        t.gameObject.AddComponent<LayoutElement>().minHeight = 42f;

        return (RectTransform)col.transform;
    }
}

/// <summary>
/// Reusable yes/no modal. Used for "keep this recording?", profile deletion and
/// settings reset, so those flows never need their own bespoke screen.
/// </summary>
public class ConfirmDialog : UIScreen
{
    public override bool IsOverlay => true;

    private TMP_Text _title, _body;
    private Button _yes, _no;
    private Action<bool> _callback;

    protected override void Build()
    {
        var col = Panel("", 520f, 260f);

        // Panel() adds a title label first; grab it so we can retarget the text.
        _title = col.GetComponentInChildren<TMP_Text>();
        _title.fontSize = 24;

        _body = UIFactory.Label("", col, 18, TextAlignmentOptions.TopLeft, UIFactory.TextDim);
        _body.gameObject.AddComponent<LayoutElement>().minHeight = 90f;

        var row = UIFactory.Row(col, 12f);
        _no  = UIFactory.Button("No",  row.transform, () => Answer(false), UIFactory.PanelAlt);
        _yes = UIFactory.Button("Yes", row.transform, () => Answer(true),  UIFactory.Accent);
    }

    public void Ask(string title, string body, string yesText, string noText, Action<bool> callback)
    {
        _callback = callback;
        _title.text = title;
        _body.text = body;
        UIFactory.SetButtonText(_yes, yesText);
        UIFactory.SetButtonText(_no, noText);
        Show();
    }

    private void Answer(bool result)
    {
        Hide();
        var cb = _callback;
        _callback = null;              // clear before invoking: the callback may open another dialog
        cb?.Invoke(result);
    }
}
