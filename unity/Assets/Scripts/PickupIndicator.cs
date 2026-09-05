using UnityEngine;

/// <summary>
/// Drives the pickup wire square's world size and color based on the force
/// currently applied to this lane.
///
/// SETUP
///   Attach to each lane child GameObject (same object as LanePickup).
///   Assign a wire-square sprite to the SpriteRenderer on this object.
///   The sprite's Pixels Per Unit determines its native size; this script
///   overrides that by setting lossyScale-compensated localScale every frame,
///   so any PPU value works as long as it is consistent.
///
/// SIZE BEHAVIOUR
///   No press  → sizeUnpressed (e.g. 75 wu)
///   Ramping   → shrinks linearly toward sizeAtThreshold as force → threshold
///   At/above  → locked to sizeAtThreshold, color snaps to colorThreshold
///
/// The parent pickup object has a large non-uniform scale. This script divides
/// the desired world size by the parent's lossyScale on each axis so the child
/// always appears as a square in world space.
///
/// COLLIDER SIZE (constant, independent of the shrinking sprite)
///   This GameObject's localScale changes every frame to drive the visual
///   shrink/grow of the sprite. A BoxCollider2D on the same object would
///   shrink along with it, which is wrong for hit-detection math that assumes
///   a fixed pickup size. To keep the collider's WORLD size fixed at
///   colliderWorldSize regardless of the current localScale, this script
///   actively counter-scales BoxCollider2D.size every frame:
///       collider.size = colliderWorldSize / localScale (per axis)
///   so that size * localScale == colliderWorldSize, constantly.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class PickupIndicator : MonoBehaviour
{
    [Header("Lane")]
    [Tooltip("Lane index — must match the LanePickup on this GameObject.")]
    public int laneIndex;

    [Header("Size (world units)")]
    [Tooltip("Square size when no force is applied.")]
    public float sizeUnpressed = 75f;

    [Tooltip("Square size when force meets the threshold (just above current note width of 40 wu).")]
    public float sizeAtThreshold = 45f;

    [Header("Collider (constant world size)")]
    [Tooltip("BoxCollider2D on this GameObject whose WORLD-space size should stay " +
             "fixed regardless of the sprite's shrink/grow animation. Auto-found on " +
             "this object if left empty.")]
    public BoxCollider2D pickupCollider;

    [Tooltip("Fixed world-space size (both axes) the collider should always have, " +
             "independent of localScale.")]
    public float colliderWorldSize = 45f;

    [Header("Colors")]
    public Color colorUnpressed   = new Color(0.7f, 0.7f, 0.7f, 1f);
    public Color colorApproaching = new Color(1f,   0.8f, 0.2f, 1f);
    public Color colorThreshold   = new Color(1f,   0.95f, 0f,  1f);

    [Header("Force threshold source")]
    [Tooltip("Read per-lane threshold from GameDifficulty. Disable to use fallback.")]
    public bool readFromGameDifficulty = true;
    [Range(0f, 1f)] public float fallbackThreshold = 0.4f;

    private SpriteRenderer sr;

    void Awake()
    {
        sr = GetComponent<SpriteRenderer>();
        if (pickupCollider == null)
            pickupCollider = GetComponent<BoxCollider2D>();
    }

    void Update()
    {
        float force = InputManagerScript.Instance != null
            ? InputManagerScript.Instance.GetForceForLane(laneIndex)
            : 0f;

        float threshold = readFromGameDifficulty && GameDifficulty.Instance != null
            ? GameDifficulty.Instance.GetRequiredForce(laneIndex)
            : fallbackThreshold;

        float t = threshold > 0f ? Mathf.Clamp01(force / threshold) : 1f;

        // --- World-space square size, compensated for parent non-uniform scale ---
        float worldSize = Mathf.Lerp(sizeUnpressed, sizeAtThreshold, t);

        Vector3 parentScale = transform.parent != null
            ? transform.parent.lossyScale
            : Vector3.one;

        float localX = Mathf.Abs(parentScale.x) > 0.0001f ? worldSize / parentScale.x : worldSize;
        float localY = Mathf.Abs(parentScale.y) > 0.0001f ? worldSize / parentScale.y : worldSize;
        transform.localScale = new Vector3(localX, localY, 1f);

        // --- Color ---
        sr.color = t >= 1f
            ? colorThreshold
            : Color.Lerp(colorUnpressed, colorApproaching, t);

        // --- Collider: counter-scale so its WORLD size stays fixed at colliderWorldSize,
        //     independent of the localScale change above (which only drives the sprite). ---
        if (pickupCollider != null)
        {
            float colliderLocalX = localX != 0f ? colliderWorldSize / localX : colliderWorldSize;
            float colliderLocalY = localY != 0f ? colliderWorldSize / localY : colliderWorldSize;
            pickupCollider.size = new Vector2(colliderLocalX, colliderLocalY);
        }
    }
}
