using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shown automatically when the device drops mid-session — see
/// UIManager.PauseForDeviceLoss(), triggered by UIManager's own watchdog in Update().
///
/// Deliberately the ONLY way out is a successful reconnect: silently resuming on a
/// flaky link would be worse for a therapy session than an obvious stop the
/// supervisor has to clear by hand. Esc and the normal pause menu are both blocked
/// while this is up (see the IsVisible check in UIManager.Update()), so there is no
/// way to dismiss this screen without the device actually coming back.
///
/// Reconnect uses BeginAutoDetect() — the exact same call the Device screen's
/// "Auto-detect device" button uses, and the one that already runs once at startup —
/// so "reset the board, press Reconnect" behaves identically to first-time connection.
/// The moment the link proves itself alive again, the game resumes automatically;
/// there's no separate "OK, now continue" step.
/// </summary>
public class DeviceLostScreen : UIScreen
{
    public override bool IsOverlay => true;

    TMP_Text _status;
    Button _reconnectBtn;
    ScreenTicker _ticker;

    protected override void Build()
    {
        var col = Panel("Device disconnected", 560f, 320f);

        UIFactory.Label("Reset the device, then press Reconnect.\nRecording will continue " +
                        "automatically once it's back.", col, 18,
                        TextAlignmentOptions.Left, UIFactory.TextDim)
                 .gameObject.AddComponent<LayoutElement>().minHeight = 64f;

        _reconnectBtn = UIFactory.Button("Reconnect", col, OnReconnect, UIFactory.Accent, 22, 54f);

        _status = UIFactory.Label("", col, 15, TextAlignmentOptions.Left, UIFactory.TextDim);
        _status.gameObject.AddComponent<LayoutElement>().minHeight = 40f;

        // UIScreen has no Update of its own; this tiny driver polls the reconnect
        // status while the screen is up and stops with it — see DeviceScreen.cs,
        // which defines ScreenTicker.
        _ticker = Root.gameObject.AddComponent<ScreenTicker>();
        _ticker.onTick = Refresh;
    }

    protected override void OnShow()
    {
        _status.text = SerialForceSource.Instance != null
            ? "Waiting - " + SerialForceSource.Instance.StatusMessage
            : "No SerialForceSource in the scene.";
    }

    void OnReconnect()
    {
        if (SerialForceSource.Instance == null) return;
        SerialForceSource.Instance.BeginAutoDetect();
    }

    void Refresh()
    {
        var src = SerialForceSource.Instance;
        if (src == null) return;

        _status.text = src.StatusMessage;
        _reconnectBtn.interactable = !src.Busy;

        if (src.IsAvailable)
            Manager.ResumeFromDeviceLoss();
    }
}
