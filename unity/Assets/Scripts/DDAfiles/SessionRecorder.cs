using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace DDA
{
    /// <summary>
    /// SERVICE-PROVIDER session recorder.
    ///
    /// Other systems (Play button, pause menu, "exit to main menu → save?" flow)
    /// drive the lifetime of a recording through this API:
    ///
    ///     StartRecording(SessionUserInfo user)   begin a fresh session
    ///     PauseRecording() / ResumeRecording()   freeze / resume the session clock
    ///                                            and BOTH continuous sample streams
    ///     StopAndSave()                          end + write all 5 CSVs to one folder
    ///     DiscardRecording()                     end + write NOTHING (buffers dropped)
    ///
    /// Everything is buffered in memory and only touched to disk on StopAndSave(),
    /// so a discarded session leaves no files.
    ///
    /// OUTPUT — one folder per session:
    ///   Application.persistentDataPath / Recordings / recording_<stamp> /
    ///       noteOutcomes_<stamp>.csv     one row per resolved note (NoteOutcomeEvent),
    ///                                    incl. chord identity (chordId / chordSize /
    ///                                    chordOnsetIndex / chordStaggerEighths) so the
    ///                                    controller's 1/chordSize error weighting can be
    ///                                    reproduced — or varied — offline in MATLAB
    ///       sessionMeta_<stamp>.csv      one row: user info + counts + timing (NO difficulty)
    ///                                    + the PI loops' targetErrorsPerMinute /
    ///                                    targetForceMargin setpoints (Aug 2026), so
    ///                                    offline analysis reads the actual setpoint this
    ///                                    session was tuned against instead of assuming
    ///                                    the design defaults
    ///       inputProfiles_<stamp>.csv    one row per press (InputPressEvent summary)
    ///       rawInputs_<stamp>.csv        continuous 4-channel per-finger applied force
    ///       controlAction_<stamp>.csv    continuous difficulty (d,v,f_s,τ) + performance metrics
    ///
    ///   <stamp> = <userSlug>_<yyyyMMdd>_<HHmm>   (date + hour/minute, per spec)
    ///
    /// UNIFIED DIFFICULTY (d): d is read from whichever controller currently holds
    /// DifficultyAuthority (PIDifficultyController or RuleBasedDDAController). Both
    /// express d on the identical step-count scale through the shared DifficultyMapping,
    /// so a PI session and a rule-based session log directly comparable control action.
    /// v and f_s are taken as ground truth from GameDifficulty (what the player actually
    /// gets); under either controller v == mapping.NoteSpeed(d) by construction.
    ///
    /// CSV CONVENTIONS (MATLAB-friendly): UTF-8 no BOM, '.' decimal, CRLF, headers in
    /// row 1, missing floats = "NaN". Loads directly via readtable("rawInputs_XXX.csv").
    /// </summary>
    public class SessionRecorder : MonoBehaviour
    {
        // ================================================================
        // Public data types
        // ================================================================

        /// <summary>
        /// Patient/testing-profile metadata stamped into sessionMeta and used to name
        /// the session folder/files. Passed in by the caller (the future ProfileManager
        /// via the Play button). An empty name falls back to "user" in filenames.
        /// </summary>
        [Serializable]
        public class SessionUserInfo
        {
            public string profileId = "";      // stable GUID — the real foreign key
            public string name = "";            // display name (used in filenames)
            public string age = "";             // string: may be blank/unknown
            public string physicalState = "";   // e.g. "affected" / "control"
            public string notes = "";           // free-text clinical note

            // "Left"/"Right". PlayingHand drives the lane→finger remap done
            // centrally in load_recording.m (right = canonical lane==finger,
            // left mirrors finger=3-lane); DominantHand is purely descriptive
            // metadata for future dominant-vs-non-dominant analysis and does
            // NOT affect the remap.
            public string playingHand = "Right";
            public string dominantHand = "Right";

            public SessionUserInfo() { }
            public SessionUserInfo(string id, string name, string age,
                                   string physicalState, string notes,
                                   string playingHand = "Right", string dominantHand = "Right")
            {
                this.profileId = id; this.name = name; this.age = age;
                this.physicalState = physicalState; this.notes = notes;
                this.playingHand = playingHand; this.dominantHand = dominantHand;
            }
        }

        // ================================================================
        // Inspector configuration  (kept intentionally minimal)
        // ================================================================

        [Header("Continuous sampling rates")]
        [Tooltip("Samples/second for the raw per-finger force stream (rawInputs).")]
        [Min(1f)] public float rawSampleRateHz = 60f;
        [Tooltip("Samples/second for the difficulty + performance-metric stream " +
                 "(controlAction). These signals move slowly; 10 Hz is plenty.")]
        [Min(1f)] public float controlSampleRateHz = 10f;

        [Header("Output")]
        [Tooltip("Optional absolute path to mirror each completed session folder into " +
                 "(e.g. your repo's matlab/data). Leave empty to skip.")]
        public string mirrorPath = "";

        [Header("Optional manual-testing UI (leave empty in the real menu flow)")]
        [Tooltip("Idle → starts a recording tagged 'test'; Recording → StopAndSave. " +
                 "Convenience for testing the recorder without the menu system.")]
        public Button recordButton;
        [Tooltip("Optional live status / confirmation label.")]
        public TMP_Text statusText;

        // ================================================================
        // Constants
        // ================================================================

        private const int    LANE_COUNT       = 4;
        private const string OUTPUT_SUBFOLDER = "Recordings";

        // ================================================================
        // Internal state
        // ================================================================

        private enum State { Idle, Recording, Paused }
        private State _state = State.Idle;

        // Per-note outcome + the difficulty operating point captured LIVE at resolution
        // (kept for per-note context in noteOutcomes; the full trajectory lives in controlAction).
        private struct OutcomeSample
        {
            public NoteOutcomeEvent ev;
            public float noteSpeed, spawnInterval;
            public float tau0, tau1, tau2, tau3;
        }

        private struct RawSample
        {
            public float t;
            public float f0, f1, f2, f3;
        }

        private struct ControlSample
        {
            public float t;
            public float d, v, fs;
            public float tau0, tau1, tau2, tau3;
            public float mT, eDot, mF0, mF1, mF2, mF3;
        }

        private readonly List<OutcomeSample>   _outcomes = new List<OutcomeSample>(2048);
        private readonly List<InputPressEvent> _presses  = new List<InputPressEvent>(2048);
        private readonly List<RawSample>       _raw      = new List<RawSample>(1 << 16);
        private readonly List<ControlSample>   _control  = new List<ControlSample>(8192);

        // Session clock (pause-excluding). _elapsed advances only while Recording.
        private float _elapsed;
        private float _rawAccum, _controlAccum;
        private float _startWallTime;
        private DateTime _startDateTime;
        private int _pauseCount;
        private float _pausedSecondsWall;
        private float _pauseEnteredWall;

        private SessionUserInfo _user;
        private string _stamp;

        /// <summary>
        /// Every distinct controller (by AuthorityName) that held DifficultyAuthority at
        /// ANY point during this session, in first-seen order. Robust to mid-session
        /// switches: query it as "session used RuleBased" via a substring/contains check
        /// in MATLAB regardless of what else the session also used, or read the ordered
        /// list to see the sequence of handovers.
        /// </summary>
        private readonly List<string> _controllersUsed = new List<string>();

        // Difficulty sources resolved at StartRecording (may be null if not in scene).
        // NOTE (Aug 2026): was PerformanceMonitor, which computed its regulated
        // outputs with the retired note-indexed-EMA / hard-rolling-window estimator.
        // PerformanceMonitor has been retired; PIDifficultyController is the sole
        // state estimator now (time-indexed EMA throughout — see its own class
        // doc), and is also what both tuning HUDs read from, so this makes
        // SessionRecorder consistent with what the controller and HUDs actually see.
        private PIDifficultyController  _controller;

        // ================================================================
        // Lifecycle
        // ================================================================

        void Awake()
        {
            if (recordButton != null) recordButton.onClick.AddListener(OnRecordButtonClicked);
            SetIdleUI();
        }

        void OnEnable()
        {
            DDAEventBus.OnNoteOutcome += HandleNoteOutcome;
            DDAEventBus.OnInputPress  += HandleInputPress;
            // Subscribed for the recorder's whole lifetime (not just while recording) so
            // a switch is never missed regardless of exact event ordering; HandleAuthorityChanged
            // itself checks IsRecording before doing anything.
            DifficultyAuthority.OnAuthorityChanged += HandleAuthorityChanged;
        }

        void OnDisable()
        {
            DDAEventBus.OnNoteOutcome -= HandleNoteOutcome;
            DDAEventBus.OnInputPress  -= HandleInputPress;
            DifficultyAuthority.OnAuthorityChanged -= HandleAuthorityChanged;
        }

        void Update()
        {
            if (_state != State.Recording) return;

            float dt = Time.deltaTime;   // 0 at timeScale==0, so a timeScale pause is honoured too
            _elapsed      += dt;
            _rawAccum     += dt;
            _controlAccum += dt;

            float rawPeriod     = 1f / rawSampleRateHz;
            float controlPeriod = 1f / controlSampleRateHz;

            if (_rawAccum >= rawPeriod)
            {
                while (_rawAccum >= rawPeriod) { _rawAccum -= rawPeriod; SampleRaw(); }
            }
            if (_controlAccum >= controlPeriod)
            {
                while (_controlAccum >= controlPeriod) { _controlAccum -= controlPeriod; SampleControl(); }
            }

            if (statusText != null)
                statusText.text =
                    $"REC {_elapsed,6:0.0}s  notes:{_outcomes.Count} presses:{_presses.Count} " +
                    $"raw:{_raw.Count} ctrl:{_control.Count}";
        }

        // ================================================================
        // Continuous samplers
        // ================================================================

        private void SampleRaw()
        {
            var s = new RawSample { t = _elapsed };
            s.f0 = ReadForce(0);
            s.f1 = ReadForce(1);
            s.f2 = ReadForce(2);
            s.f3 = ReadForce(3);
            _raw.Add(s);
        }

        private static float ReadForce(int lane)
        {
            var im = InputManagerScript.Instance;
            if (im == null || lane < 0 || lane >= LANE_COUNT) return float.NaN;
            // Normalized [0,1] per lane. Currently keyboard-simulated (0/1) until the
            // rehab device replaces InputManagerScript.GetForceForLane's body.
            return im.GetForceForLane(lane);
        }

        private void SampleControl()
        {
            var d = GameDifficulty.Instance;
            var c = new ControlSample { t = _elapsed };

            float v = d != null ? d.noteSpeed     : float.NaN;
            float T = d != null ? d.spawnInterval : float.NaN;
            c.v  = v;
            c.fs = (T > 0f) ? 1f / T : float.NaN;    // f_s = 1 / spawnInterval
            c.d  = ReadDifficultyCommand();          // from the controller holding authority

            c.tau0 = ReadTau(d, 0);
            c.tau1 = ReadTau(d, 1);
            c.tau2 = ReadTau(d, 2);
            c.tau3 = ReadTau(d, 3);

            if (_controller != null)
            {
                c.mT   = _controller.TimingMarginDiagnostic;   // diagnostic-only, time-indexed EMA
                c.eDot = _controller.ErrorsPerMinute;          // regulated, time-indexed point-process rate
                c.mF0  = _controller.ForceMargin(0);           // regulated, time-indexed EMA
                c.mF1  = _controller.ForceMargin(1);
                c.mF2  = _controller.ForceMargin(2);
                c.mF3  = _controller.ForceMargin(3);
            }
            else
            {
                c.mT = c.eDot = c.mF0 = c.mF1 = c.mF2 = c.mF3 = float.NaN;
            }
            _control.Add(c);
        }

        /// <summary>
        /// The unified difficulty command d of whichever writer holds authority — PI,
        /// rule-based, OR the manual preset switcher (which now reports the d implied by
        /// its preset). NaN only if nobody is in charge.
        /// </summary>
        private float ReadDifficultyCommand() => DifficultyAuthority.CurrentDifficulty;

        private static float ReadTau(GameDifficulty d, int lane)
        {
            if (d == null || d.requiredForce == null ||
                lane < 0 || lane >= d.requiredForce.Length) return float.NaN;
            return d.requiredForce[lane];
        }

        // ================================================================
        // Event handlers (buffer only while Recording)
        // ================================================================

        private void HandleNoteOutcome(NoteOutcomeEvent ev)
        {
            if (_state != State.Recording) return;
            var d = GameDifficulty.Instance;
            _outcomes.Add(new OutcomeSample
            {
                ev            = ev,
                noteSpeed     = d != null ? d.noteSpeed     : float.NaN,
                spawnInterval = d != null ? d.spawnInterval : float.NaN,
                tau0          = ReadTau(d, 0),
                tau1          = ReadTau(d, 1),
                tau2          = ReadTau(d, 2),
                tau3          = ReadTau(d, 3),
            });
        }

        private void HandleInputPress(InputPressEvent ev)
        {
            if (_state != State.Recording) return;
            _presses.Add(ev);
        }

        /// <summary>
        /// Add the newly-authoritative controller to this session's used-controllers list
        /// (if a session is active and it isn't already there). Fires for switches at any
        /// point during Recording or Paused — a controller change made while paused (e.g.
        /// from the Settings menu, reachable from the pause screen) still counts as "used
        /// in this session" even though no notes are resolving at that instant.
        /// </summary>
        private void HandleAuthorityChanged(IDifficultyWriter writer)
        {
            if (!IsRecording) return;
            AddControllerUsed(writer);
        }

        private void AddControllerUsed(IDifficultyWriter writer)
        {
            string name = writer != null ? writer.AuthorityName : "(none)";
            if (!_controllersUsed.Contains(name)) _controllersUsed.Add(name);
        }

        // ================================================================
        // Public API — the service surface
        // ================================================================

        /// <summary>Fired after StopAndSave writes files. Passes the session folder path.
        /// Not fired on DiscardRecording.</summary>
        public event Action<string> OnRecordingSaved;

        public bool IsRecording => _state == State.Recording || _state == State.Paused;
        public bool IsPaused    => _state == State.Paused;

        /// <summary>Begin a new recording. Any in-progress recording is discarded first.
        /// Pass the selected patient/testing profile; null tags the session "test".</summary>
        public void StartRecording(SessionUserInfo user = null)
        {
            if (_state != State.Idle)
            {
                Debug.LogWarning("[SessionRecorder] StartRecording called while active — discarding previous session.");
                DiscardRecording();
            }

            _user = user ?? new SessionUserInfo("", "test", "", "", "");

            // Resolve data sources once per session (scene objects, no inspector wiring).
            _controller = FindFirstObjectByType<PIDifficultyController>();

            _outcomes.Clear();
            _presses.Clear();
            _raw.Clear();
            _control.Clear();

            // Seeded here rather than relying solely on OnAuthorityChanged: Claim() is a
            // no-op when a controller already holds authority, which is the common case
            // (same controller as last session, dropdown untouched) — so no event would
            // fire to record it otherwise.
            _controllersUsed.Clear();
            AddControllerUsed(DifficultyAuthority.Current);

            _elapsed = 0f;
            _rawAccum = 0f;
            _controlAccum = 0f;
            _pauseCount = 0;
            _pausedSecondsWall = 0f;
            _startWallTime = Time.time;
            _startDateTime = DateTime.Now;
            _stamp = BuildStamp(_user, _startDateTime);

            _state = State.Recording;
            SetRecordingUI();
            Debug.Log($"[SessionRecorder] Started session '{_stamp}'.");
        }

        /// <summary>Freeze the session clock and both continuous streams.</summary>
        public void PauseRecording()
        {
            if (_state != State.Recording) return;
            _state = State.Paused;
            _pauseCount++;
            _pauseEnteredWall = Time.time;
            SetPausedUI();
            Debug.Log($"[SessionRecorder] Paused at t={_elapsed:0.0}s.");
        }

        public void ResumeRecording()
        {
            if (_state != State.Paused) return;
            _pausedSecondsWall += Time.time - _pauseEnteredWall;
            _state = State.Recording;
            SetRecordingUI();
            Debug.Log($"[SessionRecorder] Resumed at t={_elapsed:0.0}s.");
        }

        /// <summary>End the recording and write all CSVs into one session folder.</summary>
        public void StopAndSave()
        {
            if (_state == State.Idle) { Debug.LogWarning("[SessionRecorder] StopAndSave with no active recording."); return; }
            if (_state == State.Paused) _pausedSecondsWall += Time.time - _pauseEnteredWall;
            _state = State.Idle;
            SetIdleUI();
            WriteSession();
        }

        /// <summary>End the recording and write NOTHING. All buffers dropped.</summary>
        public void DiscardRecording()
        {
            if (_state == State.Idle) return;
            int n = _outcomes.Count, p = _presses.Count, r = _raw.Count, c = _control.Count;
            _outcomes.Clear(); _presses.Clear(); _raw.Clear(); _control.Clear();
            _state = State.Idle;
            SetIdleUI();
            if (statusText != null)
                statusText.text = $"Discarded — {n} notes / {p} presses / {r} raw / {c} ctrl dropped.";
            Debug.Log($"[SessionRecorder] Discarded session '{_stamp}'. Nothing written.");
        }

        // ================================================================
        // Save
        // ================================================================

        private void WriteSession()
        {
            string root = Path.Combine(Application.persistentDataPath, OUTPUT_SUBFOLDER);
            Directory.CreateDirectory(root);

            string folderName = "recording_" + _stamp;
            string dir = Path.Combine(root, folderName);
            int guard = 2;
            while (Directory.Exists(dir))
                dir = Path.Combine(root, $"{folderName}_{guard++}");
            Directory.CreateDirectory(dir);

            int nNotes = WriteNoteOutcomesCsv (Path.Combine(dir, $"noteOutcomes_{_stamp}.csv"));
            int nPress = WriteInputProfilesCsv(Path.Combine(dir, $"inputProfiles_{_stamp}.csv"));
            int nRaw   = WriteRawInputsCsv    (Path.Combine(dir, $"rawInputs_{_stamp}.csv"));
            int nCtrl  = WriteControlActionCsv(Path.Combine(dir, $"controlAction_{_stamp}.csv"));
            WriteSessionMetaCsv               (Path.Combine(dir, $"sessionMeta_{_stamp}.csv"),
                                               nNotes, nPress, nRaw, nCtrl);

            bool mirrored = TryMirror(dir);

            string msg = $"Saved session '{_stamp}'\n" +
                         $"notes:{nNotes} presses:{nPress} raw:{nRaw} ctrl:{nCtrl}\n→ {dir}" +
                         (mirrored ? $"\n→ mirrored to {mirrorPath}" : "");
            if (statusText != null) statusText.text = msg;
            Debug.Log("[SessionRecorder] " + msg);

            OnRecordingSaved?.Invoke(dir);
        }

        private bool TryMirror(string sessionDir)
        {
            if (string.IsNullOrWhiteSpace(mirrorPath)) return false;
            try
            {
                string dest = Path.Combine(mirrorPath, Path.GetFileName(sessionDir));
                Directory.CreateDirectory(dest);
                foreach (var f in Directory.GetFiles(sessionDir))
                    File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SessionRecorder] Mirror to '{mirrorPath}' failed: {ex.Message}");
                return false;
            }
        }

        // ================================================================
        // CSV writers
        // ================================================================

        private static readonly CultureInfo CSV_CI = CultureInfo.InvariantCulture;

        private int WriteNoteOutcomesCsv(string path)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.AppendLine(
                "t_session,t_enter,t_exit,windowDuration,startWindowDuration," +
                "lane,noteType,outcome,timingError,M_t," +
                "requiredForce,forceAvg,forceMax,forceMargin," +
                "coverageObserved,coverageThreshold,coverageMargin," +
                "wasSimultaneous,correctLane," +
                "chordId,chordSize,chordOnsetIndex,chordStaggerEighths," +
                "noteSpeed,spawnInterval,tau_lane,tau0,tau1,tau2,tau3,noteId");

            foreach (var s in _outcomes)
            {
                var ev = s.ev;
                float Mt      = ev.startWindowDuration - ev.timingError;
                float tauLane = TauForLane(in s, ev.lane);

                sb.Append(F(SessionTimeOf(ev.tEnter))).Append(',');
                sb.Append(F(ev.tEnter)).Append(',');
                sb.Append(F(ev.tExit)).Append(',');
                sb.Append(F(ev.windowDuration)).Append(',');
                sb.Append(F(ev.startWindowDuration)).Append(',');
                sb.Append(ev.lane).Append(',');
                sb.Append(ev.type).Append(',');
                sb.Append(ev.outcome).Append(',');
                sb.Append(F(ev.timingError)).Append(',');
                sb.Append(F(Mt)).Append(',');
                sb.Append(F(ev.requiredForce)).Append(',');
                sb.Append(F(ev.forceAvg)).Append(',');
                sb.Append(F(ev.forceMax)).Append(',');
                sb.Append(F(ev.forceMargin)).Append(',');
                sb.Append(F(ev.gameObservedCoverage)).Append(',');
                sb.Append(F(ev.coverageThreshold)).Append(',');
                sb.Append(F(ev.coverageMargin)).Append(',');
                sb.Append(ev.wasSimultaneous ? 1 : 0).Append(',');
                sb.Append(ev.correctLane ? 1 : 0).Append(',');
                // Chord identity. The controller's error weight is deliberately NOT
                // written: it is exactly 1/chordSize, so it stays derivable offline
                // without this recorder having to mirror the controller's rule (and
                // without old recordings becoming unreadable if that rule changes).
                sb.Append(ev.chordId).Append(',');
                sb.Append(ev.chordSize).Append(',');
                sb.Append(ev.chordOnsetIndex).Append(',');
                sb.Append(ev.chordStaggerEighths).Append(',');
                sb.Append(F(s.noteSpeed)).Append(',');
                sb.Append(F(s.spawnInterval)).Append(',');
                sb.Append(F(tauLane)).Append(',');
                sb.Append(F(s.tau0)).Append(',');
                sb.Append(F(s.tau1)).Append(',');
                sb.Append(F(s.tau2)).Append(',');
                sb.Append(F(s.tau3)).Append(',');
                sb.Append(ev.noteId);
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return _outcomes.Count;
        }

        private int WriteInputProfilesCsv(string path)
        {
            var sb = new StringBuilder(32 * 1024);
            sb.AppendLine("t_session,tPress,tRelease,duration,lane,fMax,fAvg,fSustained80,eventId");

            foreach (var ev in _presses)
            {
                sb.Append(F(SessionTimeOf(ev.tPress))).Append(',');
                sb.Append(F(ev.tPress)).Append(',');
                sb.Append(F(ev.tRelease)).Append(',');
                sb.Append(F(ev.duration)).Append(',');
                sb.Append(ev.lane).Append(',');
                sb.Append(F(ev.fMax)).Append(',');
                sb.Append(F(ev.fAvg)).Append(',');
                sb.Append(F(ev.fSustained80)).Append(',');
                sb.Append(ev.eventId);
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return _presses.Count;
        }

        private int WriteRawInputsCsv(string path)
        {
            var sb = new StringBuilder(1 << 20);
            sb.AppendLine("t_session,f_lane0,f_lane1,f_lane2,f_lane3");
            foreach (var s in _raw)
            {
                sb.Append(F(s.t)).Append(',');
                sb.Append(F(s.f0)).Append(',');
                sb.Append(F(s.f1)).Append(',');
                sb.Append(F(s.f2)).Append(',');
                sb.Append(F(s.f3));
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return _raw.Count;
        }

        private int WriteControlActionCsv(string path)
        {
            var sb = new StringBuilder(128 * 1024);
            sb.AppendLine(
                "t_session,d,v,f_s,tau0,tau1,tau2,tau3," +
                "M_t,e_dot,M_F0,M_F1,M_F2,M_F3");
            foreach (var c in _control)
            {
                sb.Append(F(c.t)).Append(',');
                sb.Append(F(c.d)).Append(',');
                sb.Append(F(c.v)).Append(',');
                sb.Append(F(c.fs)).Append(',');
                sb.Append(F(c.tau0)).Append(',');
                sb.Append(F(c.tau1)).Append(',');
                sb.Append(F(c.tau2)).Append(',');
                sb.Append(F(c.tau3)).Append(',');
                sb.Append(F(c.mT)).Append(',');
                sb.Append(F(c.eDot)).Append(',');
                sb.Append(F(c.mF0)).Append(',');
                sb.Append(F(c.mF1)).Append(',');
                sb.Append(F(c.mF2)).Append(',');
                sb.Append(F(c.mF3));
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return _control.Count;
        }

        private void WriteSessionMetaCsv(string path, int nNotes, int nPress, int nRaw, int nCtrl)
        {
            var sb = new StringBuilder();
            // NO difficulty fields — difficulty is recorded over time in controlAction.
            sb.AppendLine(
                "sessionStamp,profileId,name,age,physicalState,notes," +
                "isoTimestamp,unityStartTime,activeDurationSeconds," +
                "pauseCount,pausedSecondsWall," +
                "rawSampleRateHz,controlSampleRateHz,laneCount," +
                "notesWritten,pressesWritten,rawSamplesWritten,controlSamplesWritten," +
                "controllersUsed,targetErrorsPerMinute,targetForceMargin," +
                "playingHand,dominantHand");

            sb.Append(Q(_stamp)).Append(',');
            sb.Append(Q(_user.profileId)).Append(',');
            sb.Append(Q(_user.name)).Append(',');
            sb.Append(Q(_user.age)).Append(',');
            sb.Append(Q(_user.physicalState)).Append(',');
            sb.Append(Q(_user.notes)).Append(',');
            sb.Append(_startDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CSV_CI)).Append(',');
            sb.Append(F(_startWallTime)).Append(',');
            sb.Append(F(_elapsed)).Append(',');
            sb.Append(_pauseCount).Append(',');
            sb.Append(F(_pausedSecondsWall)).Append(',');
            sb.Append(F(rawSampleRateHz)).Append(',');
            sb.Append(F(controlSampleRateHz)).Append(',');
            sb.Append(LANE_COUNT).Append(',');
            sb.Append(nNotes).Append(',');
            sb.Append(nPress).Append(',');
            sb.Append(nRaw).Append(',');
            sb.Append(nCtrl).Append(',');
            // Semicolon-joined (not comma) specifically so this field never needs CSV
            // quoting despite holding multiple entries; order = first-seen this session.
            sb.Append(Q(string.Join(";", _controllersUsed))).Append(',');
            // Setpoints the PI loops were tuned to for this session (Aug 2026). Read
            // directly off PIDifficultyController's own inspector fields rather than
            // hardcoding the design defaults, so a session recorded under a different
            // tuning is self-describing instead of silently mismatching whatever a
            // downstream MATLAB script assumes. NaN if the component wasn't present in
            // the scene at all (e.g. a controller-only test scene stripped of it) —
            // NOT written as NaN just because a different controller (e.g. Rule-based)
            // held authority for the session: PIDifficultyController's setpoints are
            // fixed inspector configuration independent of which writer currently holds
            // DifficultyAuthority, so they're still meaningful context even then.
            sb.Append(F(_controller != null ? _controller.targetErrorsPerMinute : float.NaN)).Append(',');
            sb.Append(F(_controller != null ? _controller.targetForceMargin    : float.NaN)).Append(',');
            sb.Append(Q(_user.playingHand)).Append(',');
            sb.Append(Q(_user.dominantHand));
            sb.AppendLine();

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // ================================================================
        // Helpers
        // ================================================================

        /// <summary>Map an absolute Time.time onto the pause-excluding session clock.
        /// Discrete events fire only while running, so subtracting the running time since
        /// the event keeps them consistent with the continuous streams. Clamped to >= 0.</summary>
        private float SessionTimeOf(float absoluteTime)
        {
            if (float.IsNaN(absoluteTime)) return float.NaN;
            float t = _elapsed - (Time.time - absoluteTime);
            return t < 0f ? 0f : t;
        }

        private static float TauForLane(in OutcomeSample s, int lane)
        {
            switch (lane)
            {
                case 0:  return s.tau0;
                case 1:  return s.tau1;
                case 2:  return s.tau2;
                case 3:  return s.tau3;
                default: return float.NaN;
            }
        }

        private static string F(float v)
        {
            if (float.IsNaN(v)) return "NaN";
            if (float.IsPositiveInfinity(v)) return "Inf";
            if (float.IsNegativeInfinity(v)) return "-Inf";
            return v.ToString("0.######", CSV_CI);
        }

        /// <summary>Quote a string field so commas/newlines in a clinical note don't
        /// break the CSV. Doubles embedded quotes (RFC 4180).</summary>
        private static string Q(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool needsQuote = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            return needsQuote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        private string BuildStamp(SessionUserInfo user, DateTime when)
        {
            string userSlug = Sanitize(string.IsNullOrWhiteSpace(user?.name) ? "user" : user.name);
            return $"{userSlug}_{when.ToString("yyyyMMdd", CSV_CI)}_{when.ToString("HHmm", CSV_CI)}";
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        // ================================================================
        // Optional manual-testing UI
        // ================================================================

        private void OnRecordButtonClicked()
        {
            if (_state == State.Idle) StartRecording(null);
            else                      StopAndSave();
        }

        private void SetIdleUI()      => SetButtonLabel("● Record", new Color(0.85f, 0.85f, 0.85f));
        private void SetRecordingUI() => SetButtonLabel("■ Save",   new Color(0.55f, 0.80f, 0.55f));
        private void SetPausedUI()    => SetButtonLabel("■ Save",   new Color(0.95f, 0.85f, 0.45f));

        private void SetButtonLabel(string label, Color normal)
        {
            if (recordButton == null) return;
            var txt = recordButton.GetComponentInChildren<TMP_Text>();
            if (txt != null) txt.text = label;
            var colours = recordButton.colors;
            colours.normalColor = normal;
            colours.highlightedColor = normal * 0.9f;
            recordButton.colors = colours;
        }
    }
}
