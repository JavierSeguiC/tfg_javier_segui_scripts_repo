using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Hardware adapter for the rehabilitation device — the ONE class the "when the
/// device arrives" comment in InputManagerScript pointed at.
///
/// Reads comma-separated normalised force values the Arduino firmware prints once
/// per line (e.g. "0.00,0.42,1.00,0.13\n"), on a background thread so a blocking
/// serial read never stalls the main Unity thread / frame rate.
///
/// DEVICE CHANNEL -> LANE MAPPING:
///   Default (channelsAreReversed = false), A0 = index:
///     A0 -> lane 0 (Index), A1 -> lane 1 (Middle), A2 -> lane 2 (Ring), A3 -> lane 3 (Pinky)
///   With channelsAreReversed = true, for a unit wired pinky-first:
///     A0 -> lane 3, A1 -> lane 2, A2 -> lane 1, A3 -> lane 0
///
/// The flag describes the HARDWARE, not a preference. Lane index means finger
/// identity throughout the pipeline — canonical order is fixed as
/// (Index, Middle, Ring, Pinky) = lane (0,1,2,3) — and load_recording.m passes
/// load_recording.m and every per-lane column downstream (f_lane0..3, tau0..3).
/// An earlier reversal here (index -> lane 3) is what made the analysis label the
/// pinky as the index finger. Left-hand play still needs no handling in this file:
/// that mirror is applied downstream in MATLAB from sessionMeta's playingHand.
///
/// Firmware note: the calibration sketch prints 8 fields per line (raw_index..
/// raw_pinky, norm_index..norm_pinky) for debugging. This adapter only needs the
/// normalised values, so it reads the LAST 4 fields — works against both the
/// 8-field debug sketch and a trimmed 4-field production sketch.
///
/// CONNECTION MODEL (deliberately manual, per design decision):
///   - Auto-detect runs once at startup, and again whenever the user presses the
///     button on the Device screen.
///   - If the device is lost mid-session the source simply goes stale and the game
///     falls back to keyboard/zero. Nothing silently reconnects on its own — in a
///     therapy session, force quietly resuming mid-hold would be worse than an
///     obvious disconnection the operator reconnects deliberately.
/// </summary>
public class SerialForceSource : MonoBehaviour, InputManagerScript.IForceSource
{
    public static SerialForceSource Instance { get; private set; }

    public enum ConnState { Disconnected, Searching, Connected }

    [Header("Serial")]
    [Tooltip("Must match Serial.begin() in the firmware.")]
    public int baudRate = 115200;

    [Tooltip("If no valid line arrives within this many seconds, IsAvailable reports " +
             "false and the game falls back to keyboard/zero.")]
    public float staleTimeoutSeconds = 0.5f;

    [Header("Detection")]
    [Tooltip("Run auto-detect once when the game starts.")]
    public bool autoDetectOnStart = true;

    [Tooltip("How long to listen on a candidate port before deciding it isn't the device.")]
    public float probeSeconds = 1.5f;

    [Tooltip("Valid lines a port must produce during the probe to be accepted. >1 so a " +
             "single lucky garbage line can't win.")]
    public int probeLinesRequired = 3;

    [Tooltip("NEVER open these ports during auto-detect. Bluetooth RFCOMM ports are " +
             "excluded automatically on Windows; add anything else that must not be " +
             "touched (a serial instrument, a debug probe).")]
    public string[] portsToNeverProbe = new string[0];

    [Tooltip("Auto-detect only ports Windows reports as USB serial / CDC devices. " +
             "Turn OFF only if your device genuinely isn't detected — probing every " +
             "port can disturb Bluetooth and other virtual serial devices.")]
    public bool onlyProbeUsbPorts = true;

    [Header("Wiring")]
    [Tooltip("Set TRUE if the device's analog channels run pinky-first (A0 = pinky) " +
             "instead of index-first (A0 = index). This describes a PHYSICAL FACT about " +
             "how the unit is wired — it is not a display preference. Verify it on the " +
             "Device screen: press one finger and check the bar with that finger's name " +
             "moves. Getting this wrong silently mislabels every finger in the analysis.")]
    public bool channelsAreReversed = false;

    // ---------------- live state (read by the UI) ----------------

    public ConnState State { get; private set; } = ConnState.Disconnected;

    /// <summary>Human-readable result of the last connect attempt — shown under the button.</summary>
    public string StatusMessage { get; private set; } = "Not connected";

    /// <summary>Port currently connected on, or "" when disconnected.</summary>
    public string ConnectedPort { get; private set; } = "";

    /// <summary>True while an auto-detect or manual connect is in progress.</summary>
    public bool Busy { get; private set; }

    /// <summary>
    /// Why the link last failed, for diagnosis. Distinguishes the two very different
    /// causes that both surface as "disconnected": the reader thread dying on an
    /// exception (a driver/port problem — the port is still open, nothing is coming
    /// out of it) versus data simply stopping (board reset, cable pulled, USB hub
    /// power event). Empty when nothing has gone wrong.
    /// </summary>
    public string LastError { get; private set; } = "";

    /// <summary>False once the reader thread has exited — the port is open but dead.</summary>
    public bool ReaderAlive => _readThread != null && _readThread.IsAlive;

    const string PREF_LAST_PORT = "device.lastGoodPort";

    // ---------------- internals ----------------

    SerialPort _port;
    Thread _readThread;
    volatile bool _running;
    volatile int _validLines;

    readonly float[] _laneForce = new float[InputManagerScript.LaneCount];
    readonly object _lock = new object();

    // Stopwatch, not Time.*: Time is main-thread-only and the reader is a background
    // thread. Also immune to Time.timeScale = 0, which every menu sets.
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    volatile float _lastLineSeconds = -999f;

    public bool IsAvailable =>
        State == ConnState.Connected && _port != null && _port.IsOpen &&
        ((float)_clock.Elapsed.TotalSeconds - _lastLineSeconds) < staleTimeoutSeconds;

    public string SourceName =>
        string.IsNullOrEmpty(ConnectedPort) ? "Device (serial)" : "Device (" + ConnectedPort + ")";

    // ================================================================
    // Lifecycle
    // ================================================================

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        InputManagerScript.RegisterForceSource(this);
    }

    void Start()
    {
        if (autoDetectOnStart) BeginAutoDetect();
    }

    bool _wasAvailable;

    /// <summary>
    /// Diagnosis only. Catches the exact frame the link drops and logs WHICH failure
    /// it was, because "it disconnected" on its own is not actionable:
    ///   - reader thread dead  -> a read/driver-level failure on the port
    ///   - thread alive, port open, data stopped -> the board stopped sending
    ///     (reset, cable, USB hub power event)
    ///   - port closed         -> the port vanished from the OS entirely, which is
    ///     what a USB disconnect or hub reset looks like
    /// </summary>
    void Update()
    {
        bool now = IsAvailable;

        if (_wasAvailable && !now && State == ConnState.Connected)
        {
            float silent = (float)_clock.Elapsed.TotalSeconds - _lastLineSeconds;
            bool portOpen = _port != null && _port.IsOpen;

            string cause = !portOpen        ? "PORT CLOSED/VANISHED (USB-level disconnect)"
                         : !ReaderAlive     ? "READER THREAD DEAD (" + LastError + ")"
                                            : "DATA STOPPED (board reset, cable, or hub power)";

            Debug.LogWarning($"[SerialForceSource] Link lost on {ConnectedPort} after " +
                             $"{silent:0.00}s of silence. Cause: {cause}");
        }

        _wasAvailable = now;
    }

    void OnDestroy()
    {
        ClosePort();
        if (Instance == this)
        {
            Instance = null;
            InputManagerScript.ClearForceSource();
        }
    }

    // ================================================================
    // Public API (the Device screen drives these)
    // ================================================================

    /// <summary>
    /// Every port on the machine — the MANUAL dropdown shows all of them, because a
    /// deliberate user choice is allowed to target anything. Auto-detect uses the
    /// filtered list below instead.
    /// </summary>
    public static string[] AvailablePorts()
    {
        try
        {
            var names = SerialPort.GetPortNames();
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[SerialForceSource] Could not enumerate ports: " + e.Message);
            return new string[0];
        }
    }

    /// <summary>
    /// Ports auto-detect is allowed to OPEN.
    ///
    /// WHY THIS FILTER EXISTS: SerialPort.GetPortNames() returns Bluetooth RFCOMM
    /// virtual ports alongside real USB ones. Opening a Bluetooth port makes Windows
    /// attempt an RFCOMM link on the adapter, which can disturb or wedge the whole
    /// Bluetooth stack — observed in practice as a paired mouse dropping and then
    /// refusing to re-pair. Auto-detect must never touch those: it is a blind scan,
    /// and a blind scan has no business opening devices it can't identify.
    /// </summary>
    public List<string> ProbeCandidates()
    {
        var all = AvailablePorts();
        var deviceMap = PortDeviceMap();
        var result = new List<string>();

        foreach (var p in all)
        {
            bool skip = false;

            foreach (var banned in portsToNeverProbe)
                if (string.Equals(banned, p, StringComparison.OrdinalIgnoreCase)) skip = true;

            if (!skip && deviceMap.TryGetValue(p, out string driver))
            {
                // Driver path e.g. "\Device\BthModem0" (Bluetooth) or "\Device\USBSER000".
                string d = driver.ToLowerInvariant();
                if (d.Contains("bthmodem") || d.Contains("bluetooth")) skip = true;
                else if (onlyProbeUsbPorts && !d.Contains("usbser") && !d.Contains("vcp")) skip = true;
            }

            if (skip) Debug.Log("[SerialForceSource] Auto-detect skipping " + p + ".");
            else result.Add(p);
        }

        // Last known good port first: normally makes detection a single ~1.5 s probe
        // instead of a scan, which also minimises how many ports get opened at all.
        string last = PlayerPrefs.GetString(PREF_LAST_PORT, "");
        if (!string.IsNullOrEmpty(last) && result.Remove(last)) result.Insert(0, last);

        return result;
    }

    /// <summary>
    /// COM name -> driver device path, from the registry. This is what lets us tell a
    /// USB CDC port from a Bluetooth one before opening it. Windows only; on other
    /// platforms the map is empty and no filtering by driver happens.
    /// </summary>
    static Dictionary<string, string> PortDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine
                       .OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
            {
                if (key == null) return map;
                foreach (var devicePath in key.GetValueNames())
                {
                    string com = key.GetValue(devicePath) as string;
                    if (!string.IsNullOrEmpty(com)) map[com] = devicePath;
                }
            }
        }
        catch (Exception e)
        {
            Debug.Log("[SerialForceSource] Could not read serial device map: " + e.Message);
        }
#endif
        return map;
    }

    /// <summary>Try every serial port in turn until one is producing valid device lines.</summary>
    public void BeginAutoDetect()
    {
        if (Busy) return;
        StopAllCoroutines();
        StartCoroutine(AutoDetectRoutine());
    }

    /// <summary>Connect to one specific port chosen by the user.</summary>
    public void BeginManualConnect(string portName)
    {
        if (Busy || string.IsNullOrEmpty(portName)) return;
        StopAllCoroutines();
        StartCoroutine(ManualConnectRoutine(portName));
    }

    public void Disconnect()
    {
        StopAllCoroutines();
        Busy = false;
        ClosePort();
        State = ConnState.Disconnected;
        StatusMessage = "Disconnected";
    }

    // ================================================================
    // Detection routines
    //
    // Coroutines, not blocking loops: probing several ports at ~1.5 s each would
    // freeze the menu for seconds if done inline. All waits are REALTIME because
    // every menu runs at Time.timeScale = 0.
    // ================================================================

    IEnumerator AutoDetectRoutine()
    {
        Busy = true;
        ClosePort();
        State = ConnState.Searching;
        StatusMessage = "Searching for device...";

        var ports = ProbeCandidates();
        if (ports.Count == 0)
        {
            State = ConnState.Disconnected;
            StatusMessage = "Device not found - no suitable serial ports " +
                            "(Bluetooth ports are never probed; use Connect manually)";
            Busy = false;
            yield break;
        }

        foreach (var p in ports)
        {
            StatusMessage = "Searching... trying " + p;
            bool ok = false;
            yield return ProbePort(p, r => ok = r);
            if (ok)
            {
                StatusMessage = "Device found - connected on " + p;
                Busy = false;
                yield break;
            }
        }

        State = ConnState.Disconnected;
        StatusMessage = "Device not found (checked " + ports.Count + " port" +
                        (ports.Count == 1 ? "" : "s") + ")";
        Busy = false;
    }

    IEnumerator ManualConnectRoutine(string portName)
    {
        Busy = true;
        ClosePort();
        State = ConnState.Searching;
        StatusMessage = "Connecting to " + portName + "...";

        bool ok = false;
        yield return ProbePort(portName, r => ok = r);

        StatusMessage = ok
            ? "Device found - connected on " + portName
            : "No device data on " + portName;

        if (!ok) State = ConnState.Disconnected;
        Busy = false;
    }

    /// <summary>
    /// Open one port and listen: if it produces enough well-formed lines inside
    /// probeSeconds it's our device and we stay connected, otherwise close and report
    /// failure. This is what makes auto-detect safe to point at arbitrary ports — a
    /// Bluetooth or modem port simply never sends four in-range floats.
    /// </summary>
    IEnumerator ProbePort(string portName, Action<bool> result)
    {
        if (!OpenPort(portName)) { result(false); yield break; }

        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < probeSeconds)
        {
            if (_validLines >= probeLinesRequired)
            {
                State = ConnState.Connected;
                ConnectedPort = portName;
                LastError = "";
                PlayerPrefs.SetString(PREF_LAST_PORT, portName);
                PlayerPrefs.Save();
                Debug.Log("[SerialForceSource] Connected on " + portName + ".");
                result(true);
                yield break;
            }
            yield return null;
        }

        ClosePort();
        result(false);
    }

    // ================================================================
    // Port plumbing
    // ================================================================

    bool OpenPort(string portName)
    {
        try
        {
            _validLines = 0;
            _lastLineSeconds = -999f;

            _port = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 200,      // so ReadLine() can't block the thread forever
                NewLine = "\n",
                DtrEnable = true        // some boards need DTR asserted before they print
            };
            _port.Open();

            _running = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();
            return true;
        }
        catch (Exception e)
        {
            // Expected and harmless during auto-detect: ports already in use by
            // another app (the Arduino Serial Monitor, notably) or not openable.
            Debug.Log("[SerialForceSource] " + portName + " unavailable: " + e.Message);
            _port = null;
            return false;
        }
    }

    void ClosePort()
    {
        _running = false;
        try { _readThread?.Join(300); } catch { /* ignore on shutdown */ }
        _readThread = null;

        try { if (_port != null && _port.IsOpen) _port.Close(); } catch { /* ignore */ }
        _port = null;

        ConnectedPort = "";
        _validLines = 0;
        _lastLineSeconds = -999f;

        lock (_lock)
            for (int i = 0; i < InputManagerScript.LaneCount; i++) _laneForce[i] = 0f;
    }

    void ReadLoop()
    {
        var port = _port;               // local copy: ClosePort nulls the field
        while (_running && port != null)
        {
            try
            {
                string line = port.ReadLine();
                if (TryParseLastFour(line, out float[] values))
                {
                    lock (_lock)
                    {
                        // THE ONE PLACE channel order becomes lane order.
                        //
                        // lane index IS finger identity, canonically and irreversibly:
                        // lane 0 = Index ... lane 3 = Pinky. load_recording.m passes
                        // right-hand sessions through unchanged on exactly that
                        // assumption, and the keyboard path (Q..R = lane 0..3) already
                        // obeys it. So this maps the DEVICE'S WIRING onto that fixed
                        // order — it must never be flipped to make the on-screen lane
                        // positions feel better, because that silently renames every
                        // finger in the recorded data.
                        for (int channel = 0; channel < InputManagerScript.LaneCount; channel++)
                        {
                            int lane = channelsAreReversed
                                     ? (InputManagerScript.LaneCount - 1) - channel
                                     : channel;
                            _laneForce[lane] = values[channel];
                        }
                    }
                    _lastLineSeconds = (float)_clock.Elapsed.TotalSeconds;
                    if (_validLines < 1000) _validLines++;
                }
            }
            catch (TimeoutException)
            {
                // Normal when the board is momentarily idle, or when the port isn't
                // the device at all — keep waiting until the probe gives up.
            }
            catch (Exception e)
            {
                // The reader thread is about to die, which permanently kills the link
                // even though the port object still looks open. Previously this broke
                // SILENTLY, so a driver-level read failure was indistinguishable from
                // the board simply going quiet. Record it — the distinction is the
                // whole diagnosis.
                if (_running)
                {
                    LastError = e.GetType().Name + ": " + e.Message;
                    Debug.LogWarning("[SerialForceSource] Reader thread stopped on " +
                                     ConnectedPort + " — " + LastError);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Parses a CSV line and returns the LAST 4 fields as floats — tolerant of both
    /// the 4-field production format and the 8-field debug format (raw+norm).
    /// The [0,1] range check doubles as the device fingerprint during auto-detect.
    /// </summary>
    static bool TryParseLastFour(string line, out float[] values)
    {
        values = null;
        if (string.IsNullOrEmpty(line)) return false;

        var parts = line.Trim().Split(',');
        if (parts.Length < InputManagerScript.LaneCount) return false;

        var parsed = new float[InputManagerScript.LaneCount];
        int offset = parts.Length - InputManagerScript.LaneCount;

        for (int i = 0; i < InputManagerScript.LaneCount; i++)
        {
            if (!float.TryParse(parts[offset + i], NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out parsed[i]))
                return false;

            if (parsed[i] < 0f || parsed[i] > 1f) return false;
        }

        values = parsed;
        return true;
    }

    // ================================================================
    // IForceSource
    // ================================================================

    public float GetForce(int laneIndex)
    {
        if (laneIndex < 0 || laneIndex >= InputManagerScript.LaneCount) return 0f;
        lock (_lock) { return _laneForce[laneIndex]; }
    }
}
