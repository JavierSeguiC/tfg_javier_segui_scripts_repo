using TMPro;
using UnityEngine;

/// <summary>
/// Drives one free-standing TMP text object with the device connection state.
/// Deliberately NOT part of the code-built menu system: it hangs off its own
/// GameObject in the scene so it can be positioned by hand wherever it reads best
/// during play, without a screen owning it.
///
/// SETUP: put this on a GameObject that has a TextMeshProUGUI (under any Canvas)
/// or a TextMeshPro (world-space). Leave 'target' empty to use the text component
/// on the same object.
///
/// Reads InputManagerScript.DeviceConnected rather than SerialForceSource directly,
/// so it reports what the GAME actually believes about its input source — including
/// the stale-data timeout, and including any future non-serial adapter.
/// </summary>
public class DeviceStatusText : MonoBehaviour
{
    [Tooltip("Text to drive. Leave empty to use the TMP_Text on this GameObject.")]
    public TMP_Text target;

    [Header("Text")]
    public string connectedText = "Device connected";
    public string disconnectedText = "Device disconnected";

    [Tooltip("Append the port name when connected, e.g. 'Device connected (COM8)'.")]
    public bool showPortName = false;

    [Header("Colours")]
    public Color connectedColor = new Color(0.30f, 0.65f, 0.40f, 1f);
    public Color disconnectedColor = new Color(0.75f, 0.30f, 0.30f, 1f);

    [Header("Visibility")]
    [Tooltip("Hide the text entirely while the game is not being played (menus).")]
    public bool onlyWhilePlaying = false;

    // Cache so we only touch the text component when something actually changed —
    // assigning TMP_Text.text every frame forces a mesh rebuild even when identical.
    bool _lastConnected;
    string _lastPort = "";
    bool _initialised;

    void Awake()
    {
        if (target == null) target = GetComponent<TMP_Text>();
        if (target == null)
            Debug.LogWarning("[DeviceStatusText] No TMP_Text found — assign 'target'.");
    }

    void Update()
    {
        if (target == null) return;

        if (onlyWhilePlaying)
        {
            bool playing = GameFlow.State == GameFlowState.Playing;
            if (target.gameObject.activeSelf != playing) target.gameObject.SetActive(playing);
            if (!playing) return;
        }

        bool connected = InputManagerScript.DeviceConnected;
        string port = SerialForceSource.Instance != null
                    ? SerialForceSource.Instance.ConnectedPort : "";

        if (_initialised && connected == _lastConnected && port == _lastPort) return;

        _initialised = true;
        _lastConnected = connected;
        _lastPort = port;

        if (connected)
        {
            target.text = (showPortName && !string.IsNullOrEmpty(port))
                        ? connectedText + " (" + port + ")"
                        : connectedText;
            target.color = connectedColor;
        }
        else
        {
            target.text = disconnectedText;
            target.color = disconnectedColor;
        }
    }
}
