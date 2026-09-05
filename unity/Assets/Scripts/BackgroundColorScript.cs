using UnityEngine;

/// <summary>
/// Tints the background with a color set directly in the Inspector.
/// </summary>
public class BackgroundColorScript : MonoBehaviour
{
    private SpriteRenderer backgroundSpriteRenderer;

    [Tooltip("Background color, set freely in the Inspector.")]
    public Color backgroundColor = Color.blue;

    void Start()
    {
        backgroundSpriteRenderer = GetComponent<SpriteRenderer>();
    }

    void Update()
    {
        if (backgroundSpriteRenderer == null) return;
        backgroundSpriteRenderer.color = backgroundColor;
    }
}
