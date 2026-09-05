using UnityEngine;

/// <summary>
/// Plays the beat the instant a ghost note's leading edge reaches the pickup line.
///
/// Ghost notes are spawned on every beat (see NoteSpawner) with the same speed and the
/// same leading-edge-on-grid placement as real notes, so their arrivals are a steady,
/// physically-exact pulse that coincides with on-beat notes reaching the real pickups.
/// The arrival IS the beat — no prediction, no scheduling.
///
/// PLACEMENT
///   Put this on a trigger collider whose RIGHT (entering) edge sits at the same x as
///   the real pickups' entering edge, so a ghost's tEnter equals a real note's tEnter
///   (the ideal hit moment). The y/row is irrelevant to timing — only x and speed matter.
///
/// ISOLATION (important)
///   Put ghost notes AND this pickup on their own 2D physics layer that the real per-lane
///   pickups do NOT collide with (Project Settings > Physics 2D > Layer Collision Matrix),
///   so ghosts never enter the real hit pipeline (LanePickup / NoteHitDetector / broadcaster).
///   A Rigidbody2D must be present on the ghost note or on this pickup for 2D triggers to fire.
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class GhostPickup : MonoBehaviour
{
    [Header("Beat sound")]
    public AudioClip beatClip;
    [Tooltip("Optional downbeat accent every beatsPerBar. Leave empty for a flat, unaccented pulse.")]
    public AudioClip accentClip;
    [Range(0f, 1f)] public float volume = 0.8f;
    public float pitch = 1f;
    [Tooltip("Accent every N beats when accentClip is set. 0 or 1 = no accent.")]
    public int beatsPerBar = 4;

    [Header("Filtering (optional)")]
    [Tooltip("If set, only colliders with this tag fire the beat. Leave empty when ghosts are " +
             "already isolated on their own physics layer (recommended).")]
    public string ghostTag = "";

    int _beat;

    void Reset()
    {
        // Convenience: make the collider a trigger when the component is first added.
        var col = GetComponent<Collider2D>();
        if (col != null) col.isTrigger = true;
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (!string.IsNullOrEmpty(ghostTag) && !other.CompareTag(ghostTag)) return;

        var ss = SoundSystem.Instance;
        if (ss == null) return;

        bool accent = accentClip != null && beatsPerBar > 1 && (_beat % beatsPerBar == 0);
        ss.PlaySfx(accent ? accentClip : beatClip, pitch, volume);
        _beat++;
    }
}
