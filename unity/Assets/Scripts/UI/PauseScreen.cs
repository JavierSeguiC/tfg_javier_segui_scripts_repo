using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Pause overlay: a translucent scrim over the frozen game with Resume, Settings
/// and Exit to main menu. Opened by the HUD pause button or Esc.
///
/// IsOverlay = true so the game stays visible behind it — the therapist can see
/// the board state while adjusting something.
/// </summary>
public class PauseScreen : UIScreen
{
    public override bool IsOverlay => true;

    protected override void Build()
    {
        var col = Panel("Paused", 460f, 420f);

        UIFactory.Label("The session and its recording are frozen.", col, 16,
                        TextAlignmentOptions.Left, UIFactory.TextDim)
                 .gameObject.AddComponent<LayoutElement>().minHeight = 26f;

        UIFactory.Spacer(col, 10f);

        UIFactory.Button("Resume",  col, () => Manager.ResumeGame(),  UIFactory.Accent, 22, 56f);
        UIFactory.Button("Settings",   col, () => Manager.ShowSettings(), UIFactory.PanelAlt, 20, 50f);

        UIFactory.Spacer(col, 10f);

        UIFactory.Button("Exit to main menu", col, () => Manager.RequestExitToMainMenu(),
                         UIFactory.Danger, 20, 50f);
    }
}

/// <summary>
/// The always-on gameplay HUD: just the pause button, top-left, as specified.
/// Shown only while Playing, so it never overlaps a menu.
///
/// This is a screen rather than a scene object so it needs no inspector wiring
/// and shares the canvas with everything else.
/// </summary>
public class GameHUDScreen : UIScreen
{
    public override bool IsOverlay => true;

    protected override void Build()
    {
        // The screen root is a transparent scrim by default; the HUD must not eat
        // clicks meant for the game, so drop its raycast target entirely.
        var img = Root.GetComponent<Image>();
        if (img != null) { img.color = new Color(0f, 0f, 0f, 0f); img.raycastTarget = false; }

        var btn = UIFactory.Button("II", Root, () => Manager.PauseGame(), UIFactory.PanelAlt, 22, 48f);
        var rt = (RectTransform)btn.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);      // top-left
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(16f, -16f);
        rt.sizeDelta = new Vector2(56f, 48f);
    }
}
