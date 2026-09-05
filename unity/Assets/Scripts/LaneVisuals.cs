using UnityEngine;

/// <summary>
/// Lane highlight feedback. Lights the lane white while a note is over the
/// pickup, and a pressed-tint while the lane's finger is pressed. Put this on
/// the lane's pickup object (it can sit on the same GameObject as LanePickup;
/// both receive the trigger callbacks independently).
///
/// Note *outcome* coloring (green/red on the note sprite) is handled by
/// NoteFeedback; this script only tints the lane background.
///
/// Has NO dependency on the DDA folder. Polls InputManagerScript directly for
/// press state.
/// </summary>
public class LaneVisuals : MonoBehaviour
{
    [Tooltip("Lane index this object represents. Must match the LanePickup on this lane.")]
    public int laneIndex;

    [Tooltip("SpriteRenderer to tint. If left empty, uses this object's or its parent's renderer.")]
    public SpriteRenderer laneRenderer;

    [Header("Press detection")]
    [Tooltip("Force level at which the lane is considered pressed for the visual highlight.")]
    [Range(0f, 1f)] public float pressThreshold = 0.1f;

    [Header("Colors")]
    public Color baseColor = new Color(0.525f, 0.525f, 0.525f);
    public Color noteHereColor = Color.white;
    public Color pressedColor = new Color(0.85f, 0.85f, 1f);

    private int notesInZone = 0;
    private bool isPressed = false;

    void Start()
    {
        if (laneRenderer == null)
            laneRenderer = GetComponent<SpriteRenderer>() ?? GetComponentInParent<SpriteRenderer>();
        ApplyColor();
    }

    void Update()
    {
        bool nowPressed = false;
        if (InputManagerScript.Instance != null)
        {
            float f = InputManagerScript.Instance.GetForceForLane(laneIndex);
            nowPressed = f >= pressThreshold;
        }

        if (nowPressed != isPressed)
        {
            isPressed = nowPressed;
            ApplyColor();
        }
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other.GetComponent<NoteInfo>() == null) return;
        notesInZone++;
        ApplyColor();
    }

    void OnTriggerExit2D(Collider2D other)
    {
        if (other.GetComponent<NoteInfo>() == null) return;
        notesInZone = Mathf.Max(0, notesInZone - 1);
        ApplyColor();
    }

    void ApplyColor()
    {
        if (laneRenderer == null) return;
        if (isPressed)            laneRenderer.color = pressedColor;
        else if (notesInZone > 0) laneRenderer.color = noteHereColor;
        else                      laneRenderer.color = baseColor;
    }
}
