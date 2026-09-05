using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Device connection + calibration-sanity screen, reached from the main menu.
///
/// Two jobs:
///   1. CONNECT — auto-detect (also run once at startup by SerialForceSource) or
///      pick a port by hand. This is also the reconnection path: losing the device
///      mid-session is deliberately NOT auto-healed, so coming here and pressing
///      the button is how you get it back.
///   2. VERIFY — four live bars, one per finger, so a new device unit can be sanity
///      checked (all four lanes respond, rest sits at 0, full press reaches 1)
///      without opening the Arduino Serial Monitor. That matters because the
///      firmware's per-finger raw thresholds are hand-tuned per unit.
///
/// Bars are labelled by FINGER under the canonical order (lane 0 = Index ... lane 3
/// = Pinky), so they double as the wiring check: press one finger and the bar with
/// that finger's name must be the one that moves. If it isn't, flip
/// SerialForceSource.channelsAreReversed — the analysis reads lane as finger, so a
/// wrong mapping mislabels every finger silently rather than failing loudly. Values come straight from SerialForceSource, NOT from
/// InputManagerScript.GetForceForLane — this screen must show what the DEVICE is
/// reporting, uncontaminated by the keyboard path that would otherwise override it.
/// </summary>
public class DeviceScreen : UIScreen
{
    // Display order is finger order; lane is derived, not assumed.
    private static readonly string[] FingerNames = { "Index", "Middle", "Ring", "Pinky" };

    private TMP_Text _stateLabel;
    private TMP_Text _resultLabel;
    private TMP_Dropdown _portDropdown;
    private Button _autoBtn, _manualBtn;

    private readonly Image[] _bars = new Image[InputManagerScript.LaneCount];
    private readonly TMP_Text[] _barValues = new TMP_Text[InputManagerScript.LaneCount];

    private ScreenTicker _ticker;
    private string[] _ports = new string[0];

    protected override void Build()
    {
        var col = Panel("Device", 720f, 820f);

        // ---- connection state ----
        _stateLabel = UIFactory.Label("", col, 22, TextAlignmentOptions.Left, UIFactory.TextDim);
        _stateLabel.gameObject.AddComponent<LayoutElement>().minHeight = 32f;

        UIFactory.Spacer(col, 4f);

        // ---- auto-detect ----
        var autoCard = UIFactory.CardColumn("AutoConnect", col, UIFactory.PanelAlt, 6f);
        _autoBtn = UIFactory.Button("Auto-detect device", autoCard, OnAutoDetect, UIFactory.Accent);
        _resultLabel = UIFactory.Label("", autoCard, 15, TextAlignmentOptions.Left, UIFactory.TextDim);
        _resultLabel.gameObject.AddComponent<LayoutElement>().minHeight = 20f;

        UIFactory.Spacer(col, 4f);

        // ---- manual connect ----
        var manualCard = UIFactory.CardColumn("ManualConnect", col, UIFactory.PanelAlt, 6f);
        UIFactory.Label("Connect manually", manualCard, 15,
                        TextAlignmentOptions.Left, UIFactory.TextDim)
                 .gameObject.AddComponent<LayoutElement>().minHeight = 20f;

        _portDropdown = UIFactory.Dropdown(manualCard, new string[0], 0, null);

        var manualRow = UIFactory.Row(manualCard, 8f, 44f);
        UIFactory.Button("Refresh ports", manualRow.transform, RefreshPorts, UIFactory.PanelAlt, 17);
        _manualBtn = UIFactory.Button("Connect", manualRow.transform, OnManualConnect,
                                      UIFactory.PanelAlt, 17);

        UIFactory.Spacer(col, 8f);

        // ---- live calibration bars ----
        UIFactory.Label("Live values", col, 18, TextAlignmentOptions.Left, UIFactory.Accent)
                 .gameObject.AddComponent<LayoutElement>().minHeight = 24f;
        UIFactory.Label("Rest should read 0.00 and a full press 1.00 on every finger. " +
                        "Press one finger at a time: the bar with that finger's name must " +
                        "be the one that moves, or the wiring flag is wrong.",
                        col, 13, TextAlignmentOptions.TopLeft, UIFactory.TextDim)
                 .gameObject.AddComponent<LayoutElement>().minHeight = 46f;

        for (int i = 0; i < InputManagerScript.LaneCount; i++)
            BuildBar(col, i);

        UIFactory.Spacer(col, 8f);

        var navRow = UIFactory.Row(col, 10f, 50f);
        UIFactory.Button("Disconnect", navRow.transform, OnDisconnect, UIFactory.Danger);
        UIFactory.Button("Back", navRow.transform, () => Manager.CloseDevice(), UIFactory.PanelAlt);

        // Screens are plain classes with no Update, so a tiny MonoBehaviour on the
        // screen root drives the per-frame refresh. It lives and dies with the root
        // and only ticks while the screen is active.
        _ticker = Root.gameObject.AddComponent<ScreenTicker>();
        _ticker.onTick = Refresh;
    }

    /// <summary>One finger row: name, a fill bar, and the number.</summary>
    private void BuildBar(Transform parent, int fingerIndex)
    {
        // Bars are indexed by LANE, and lane means finger canonically, so this is an
        // identity regardless of how the device is wired — the wiring flag lives in
        // SerialForceSource and is what these bars are used to verify.
        int lane = fingerIndex;

        var card = UIFactory.CardColumn("Bar_" + FingerNames[fingerIndex], parent,
                                        UIFactory.PanelAlt, 4f);

        var head = UIFactory.Row(card, 6f, 22f);
        UIFactory.Label(FingerNames[fingerIndex], head.transform, 17, TextAlignmentOptions.Left);
        _barValues[lane] = UIFactory.Label("0.00", head.transform, 17,
                                           TextAlignmentOptions.Right, UIFactory.TextDim);

        // Plain container (no layout group inside) so the fill's anchors are ours to drive.
        var track = UIFactory.Box("Track", card, UIFactory.Bg);
        var trackLE = track.gameObject.AddComponent<LayoutElement>();
        trackLE.minHeight = 18f;
        trackLE.preferredHeight = 18f;

        var fill = UIFactory.Box("Fill", track.transform, UIFactory.Success);
        var fillRT = (RectTransform)fill.transform;
        fillRT.anchorMin = new Vector2(0f, 0f);
        fillRT.anchorMax = new Vector2(0f, 1f);      // width driven per frame via anchorMax.x
        fillRT.pivot = new Vector2(0f, 0.5f);
        fillRT.offsetMin = Vector2.zero;
        fillRT.offsetMax = Vector2.zero;
        fill.raycastTarget = false;

        _bars[lane] = fill;
    }

    protected override void OnShow()
    {
        RefreshPorts();
        Refresh();
    }

    // ================================================================
    // Actions
    // ================================================================

    private void OnAutoDetect()
    {
        if (SerialForceSource.Instance == null) return;
        SerialForceSource.Instance.BeginAutoDetect();
    }

    private void OnManualConnect()
    {
        var src = SerialForceSource.Instance;
        if (src == null || _ports.Length == 0) return;

        int i = Mathf.Clamp(_portDropdown.value, 0, _ports.Length - 1);
        src.BeginManualConnect(_ports[i]);
    }

    private void OnDisconnect()
    {
        if (SerialForceSource.Instance == null) return;
        SerialForceSource.Instance.Disconnect();
    }

    /// <summary>Re-enumerate ports, keeping the current selection if it still exists.</summary>
    private void RefreshPorts()
    {
        string previous = (_ports.Length > 0 && _portDropdown.value < _ports.Length)
                        ? _ports[_portDropdown.value] : null;

        _ports = SerialForceSource.AvailablePorts();

        _portDropdown.ClearOptions();
        _portDropdown.AddOptions(_ports.Length > 0
            ? new System.Collections.Generic.List<string>(_ports)
            : new System.Collections.Generic.List<string> { "(no ports found)" });

        int restore = 0;
        for (int i = 0; i < _ports.Length; i++)
            if (_ports[i] == previous) { restore = i; break; }

        _portDropdown.SetValueWithoutNotify(restore);
        _portDropdown.RefreshShownValue();
        _portDropdown.interactable = _ports.Length > 0;
    }

    // ================================================================
    // Per-frame refresh
    // ================================================================

    private void Refresh()
    {
        var src = SerialForceSource.Instance;

        if (src == null)
        {
            _stateLabel.text = "No SerialForceSource in the scene";
            _stateLabel.color = UIFactory.Danger;
            _resultLabel.text = "Add the SerialForceSource component to a GameObject.";
            for (int lane = 0; lane < InputManagerScript.LaneCount; lane++) SetBar(lane, 0f);
            return;
        }

        // Connection headline. Uses IsAvailable, not just the state enum, so a device
        // that stopped sending (unplugged, board reset) reads as disconnected here the
        // same moment the game stops trusting it.
        bool live = src.IsAvailable;
        if (live)
        {
            _stateLabel.text = "Connected  —  " + src.ConnectedPort;
            _stateLabel.color = UIFactory.Success;
        }
        else if (src.State == SerialForceSource.ConnState.Searching)
        {
            _stateLabel.text = "Searching...";
            _stateLabel.color = UIFactory.TextModified;
        }
        else
        {
            _stateLabel.text = "Disconnected";
            _stateLabel.color = UIFactory.Danger;
        }

        _resultLabel.text = src.StatusMessage;

        // Buttons off while a probe is running, so a second press can't restart it mid-scan.
        _autoBtn.interactable = !src.Busy;
        _manualBtn.interactable = !src.Busy && _ports.Length > 0;

        for (int lane = 0; lane < InputManagerScript.LaneCount; lane++)
            SetBar(lane, live ? src.GetForce(lane) : 0f);
    }

    private void SetBar(int lane, float value)
    {
        value = Mathf.Clamp01(value);

        var fill = _bars[lane];
        if (fill != null)
        {
            var rt = (RectTransform)fill.transform;
            var max = rt.anchorMax;
            max.x = value;
            rt.anchorMax = max;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        var label = _barValues[lane];
        if (label != null) label.text = value.ToString("0.00");
    }
}

/// <summary>
/// Minimal per-frame callback for a UIScreen. UIScreen is a plain class with no
/// Unity lifecycle, so any screen needing live values attaches one of these to its
/// root: it's disabled with the root, so it costs nothing while the screen is closed.
/// </summary>
public class ScreenTicker : MonoBehaviour
{
    public System.Action onTick;
    void Update() => onTick?.Invoke();
}
