using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace DDA
{
    /// <summary>
    /// LIVE TUNING INSTRUMENT for PIDifficultyController. Draw-only + input-only:
    /// it never writes GameDifficulty itself, it only edits the controller's public
    /// tuning fields and renders its public read-outs. Deleting this file changes
    /// nothing about the control law — which is the point of keeping it separate.
    ///
    /// WHAT IT GIVES YOU
    ///   • A scrolling plot of errors/min against the setpoint, with the difficulty
    ///     d overlaid on its own scale. This is the single most useful thing while
    ///     hand-tuning: you can see whether the loop is sluggish (d crawls), hot
    ///     (d oscillates around the setpoint) or noise-driven (d jitters while the
    ///     error estimate spikes).
    ///   • Live sliders for the gains, the setpoint and the estimator time constant,
    ///     so a tuning pass is one continuous session instead of stop/edit/replay.
    ///   • Numeric read-outs of e, P, I and the saturation flag. Watching I is how
    ///     you confirm the integrator is settling rather than drifting.
    ///   • Authority switching, so you can A/B against RuleBasedDDAController
    ///     without leaving play mode.
    ///   • CSV export of the whole trace for the memoria figures.
    ///
    /// SCOPE: this panel is LOOP 1 ONLY (reflex/timing, d). The four per-lane force
    /// loops have their own instrument in ForcePITuningHUD — same features, plus a
    /// lane selector and the keyboard force simulator. The two loop families share no
    /// gains and are tuned in separate passes, so they get separate panels.
    ///
    /// SUGGESTED HAND-TUNING PROCEDURE
    ///   0. Force loops OFF for keyboard sessions (toggle lives in ForcePITuningHUD).
    ///   1. Play with the controller ACTIVE and watch for one or two minutes.
    ///      Confirm d settles somewhere in the interior of [0,1] and errors/min
    ///      hovers around the setpoint. If d parks at a limit, the range design or
    ///      the setpoint is wrong — fix that before touching gains.
    ///   2. Step the SETPOINT (e.g. 10 -> 20 -> 10) and watch the response. This is
    ///      your closed-loop step test and it needs no extra tooling.
    ///   3. Too slow to follow the step? Raise Kp. Overshoots or hunts? Lower Kp.
    ///      Steady-state offset that never closes? Lower Ti (stronger integral).
    ///      Integrator ramping and overshooting? Raise Ti.
    ///   4. d visibly jittery while the player is steady? Kp is too high for the
    ///      measurement noise, or the estimator tau is too short. Prefer lowering Kp
    ///      first — raising tau slows the plant and invalidates the gains.
    ///   5. Export the CSV and put the setpoint-step response in the memoria.
    /// </summary>
    [DisallowMultipleComponent]
    public class PITuningHUD : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("Leave empty to auto-find the controller in the scene.")]
        public PIDifficultyController controller;

        [Header("Display")]
        public bool visible = true;
        [Tooltip("Key that toggles the whole panel.")]
        public KeyCode toggleKey = KeyCode.F1;

        [Tooltip("Optional. Only used to show the correct hotkey in the pointer to the " +
                 "force panel — auto-found if left empty, and harmless if absent.")]
        public ForcePITuningHUD forceHud;

        [Header("Plot")]
        [Tooltip("Seconds of history shown in the scrolling plot.")]
        public float plotWindowSeconds = 180f;
        [Tooltip("Samples per second stored for the plot and the CSV export.")]
        public float plotSampleRate = 4f;
        [Tooltip("Upper limit of the errors/min axis. The plot auto-expands past this " +
                 "if the signal exceeds it.")]
        public float plotMaxErrorsPerMin = 40f;

        [Header("Plot colours")]
        public Color colErrors    = new Color(0.30f, 0.65f, 0.95f);
        public Color colSetpoint  = new Color(1.00f, 1.00f, 1.00f, 0.85f);
        public Color colDifficulty= new Color(1.00f, 0.72f, 0.25f);
        public Color colBackground= new Color(0.07f, 0.08f, 0.10f, 1f);
        public Color colGrid      = new Color(1f, 1f, 1f, 0.10f);

        // ---------------- internals ----------------
        struct Sample
        {
            public float t, errorsPerMin, setpoint, d, dNorm, e, p, i, noteSpeed, spawnInterval;
        }

        readonly List<Sample> _trace = new List<Sample>();   // full session, for CSV
        readonly Queue<Sample> _plot = new Queue<Sample>();  // windowed, for drawing

        Texture2D _plotTex;
        Color32[] _pixels;
        const int PlotW = 460, PlotH = 150;

        float _nextSampleAt;
        float _nextRedrawAt;
        Vector2 _scroll;
        string _status = "";
        float _statusUntil;

        static GUIStyle _rich, _small;

        // ══════════════════════════════════════════════════════════════════

        void Awake()
        {
            if (controller == null) controller = FindFirstObjectByType<PIDifficultyController>();
            if (forceHud   == null) forceHud   = FindFirstObjectByType<ForcePITuningHUD>();
            _plotTex = new Texture2D(PlotW, PlotH, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _pixels = new Color32[PlotW * PlotH];
        }

        void OnEnable()  => GameFlow.OnGameStarted += HandleNewSession;
        void OnDisable() => GameFlow.OnGameStarted -= HandleNewSession;

        /// <summary>
        /// Wipe the plot/trace at the START of every session (Play), not just rely on
        /// time-based windowing. Time.time respects Time.timeScale, which is 0 at the
        /// main menu and while paused — so it doesn't advance during that whole dwell,
        /// and the windowing check (now - sample.t > plotWindowSeconds) never ages out
        /// the previous session's trailing samples no matter how long the menu sits open.
        /// That's what made the last session's final errors/min look like it "carried
        /// over" into the new one — the underlying estimator HAD reset, only the HUD's
        /// own history buffer hadn't.
        /// </summary>
        void HandleNewSession()
        {
            _trace.Clear();
            _plot.Clear();
            _nextSampleAt = Time.time;
            _nextRedrawAt = Time.time;
            Status("New session — trace cleared");
        }

        void OnDestroy()
        {
            if (_plotTex != null) Destroy(_plotTex);
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey)) visible = !visible;
            if (controller == null) return;

            float now = Time.time;
            if (now >= _nextSampleAt)
            {
                _nextSampleAt = now + 1f / Mathf.Max(0.5f, plotSampleRate);
                CaptureSample(now);
            }

            if (visible && now >= _nextRedrawAt)
            {
                _nextRedrawAt = now + 0.1f;
                RedrawPlot();
            }
        }

        void CaptureSample(float now)
        {
            var c = controller;

            // d/dNorm come from whichever controller currently holds authority — PI or
            // RuleBased — via the shared mapping, so the plotted difficulty line follows
            // the game's ACTUAL operating point regardless of which one is driving.
            // (The preset switcher reports NaN by design — see DifficultyPresetSwitcher.)
            // errors/min, setpoint, e, P, I remain PI's own internal state: they keep
            // updating in the background even while PI isn't authoritative (intentional
            // pre-convergence), so they're still meaningful to watch, just not "live" in
            // the sense of driving the game right now.
            float dRaw  = DifficultyAuthority.CurrentDifficulty;
            float dNorm = float.IsNaN(dRaw) ? 0f : Mathf.Clamp01(c.mapping.Normalised(dRaw));

            var s = new Sample
            {
                t             = now,
                errorsPerMin  = c.ErrorsPerMinute,
                setpoint      = c.Setpoint,
                d             = dRaw,
                dNorm         = dNorm,
                e             = c.reflexLoop.Error,
                p             = c.reflexLoop.Proportional,
                i             = c.reflexLoop.Integral,
                noteSpeed     = c.NoteSpeed,
                spawnInterval = c.SpawnInterval
            };

            _trace.Add(s);
            _plot.Enqueue(s);
            while (_plot.Count > 0 && now - _plot.Peek().t > plotWindowSeconds) _plot.Dequeue();
        }

        // ══════════════════════════════════════════════════════════════════
        //  PLOT RENDERING (into a Texture2D — no scene wiring, no extra cameras)
        // ══════════════════════════════════════════════════════════════════

        void RedrawPlot()
        {
            if (_plotTex == null || _plot.Count < 2) { ClearPixels(); Blit(); return; }

            ClearPixels();

            // y-scale for the error axis, auto-expanding
            float yMax = plotMaxErrorsPerMin;
            foreach (var s in _plot) yMax = Mathf.Max(yMax, s.errorsPerMin * 1.1f);
            yMax = Mathf.Max(yMax, controller.Setpoint * 2f);

            // grid: horizontal quarters
            for (int g = 1; g < 4; g++)
            {
                int y = Mathf.RoundToInt(PlotH * g / 4f);
                for (int x = 0; x < PlotW; x++) SetPx(x, y, colGrid);
            }

            float t0 = 0f, t1 = 0f;
            bool first = true;
            foreach (var s in _plot)
            {
                if (first) { t0 = s.t; first = false; }
                t1 = s.t;
            }
            float span = Mathf.Max(1e-3f, t1 - t0);

            // setpoint line
            int spY = ValueToY(controller.Setpoint, yMax);
            for (int x = 0; x < PlotW; x += 4) { SetPx(x, spY, colSetpoint); SetPx(x + 1, spY, colSetpoint); }

            // series
            int prevXe = -1, prevYe = 0, prevXd = -1, prevYd = 0;
            foreach (var s in _plot)
            {
                int x  = Mathf.Clamp(Mathf.RoundToInt((s.t - t0) / span * (PlotW - 1)), 0, PlotW - 1);
                int ye = ValueToY(s.errorsPerMin, yMax);
                int yd = Mathf.Clamp(Mathf.RoundToInt(s.dNorm * (PlotH - 1)), 0, PlotH - 1);

                if (prevXe >= 0) DrawLine(prevXe, prevYe, x, ye, colErrors);
                if (prevXd >= 0) DrawLine(prevXd, prevYd, x, yd, colDifficulty);
                prevXe = x; prevYe = ye; prevXd = x; prevYd = yd;
            }

            Blit();
        }

        int ValueToY(float v, float yMax)
            => Mathf.Clamp(Mathf.RoundToInt(v / Mathf.Max(1e-3f, yMax) * (PlotH - 1)), 0, PlotH - 1);

        void ClearPixels()
        {
            Color32 bg = colBackground;
            for (int i = 0; i < _pixels.Length; i++) _pixels[i] = bg;
        }

        void Blit()
        {
            _plotTex.SetPixels32(_pixels);
            _plotTex.Apply(false);
        }

        void SetPx(int x, int y, Color c)
        {
            if (x < 0 || x >= PlotW || y < 0 || y >= PlotH) return;
            _pixels[y * PlotW + x] = c;
        }

        void DrawLine(int x0, int y0, int x1, int y1, Color c)
        {
            int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            for (int guard = 0; guard < 4096; guard++)
            {
                SetPx(x0, y0, c);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  IMGUI
        // ══════════════════════════════════════════════════════════════════

        void OnGUI()
        {
            if (!visible || controller == null) return;
            EnsureStyles();

            const int w = 500;
            GUILayout.BeginArea(new Rect(10, 10, w, Screen.height - 20), GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            var c = controller;

            // ---------- header / authority ----------
            GUILayout.Label($"<b>PI DDA — tuning</b>   (<i>{toggleKey} to hide</i>)", _rich);
            GUILayout.Label($"Authority: <b>{DifficultyAuthority.CurrentName}</b>", _rich);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(c.IsActive ? "PI: ACTIVE (click to release)" : "PI: inactive (click to take over)"))
            {
                if (c.IsActive) c.Deactivate(); else c.Activate();
            }
            if (GUILayout.Button("Reset loops", GUILayout.Width(100))) { c.ResetLoops(); Status("Loops reset"); }
            GUILayout.EndHorizontal();

            foreach (var w2 in DifficultyAuthority.Registered)
            {
                if (ReferenceEquals(w2, c)) continue;
                if (GUILayout.Button($"Hand control to: {w2.AuthorityName}"))
                    DifficultyAuthority.Claim(w2);
            }

            if (c.IsWarmingUp)
                GUILayout.Label($"<color=#ffcc55><b>WARM-UP</b> — observing only, {c.WarmupRemaining:0.0}s left</color>", _rich);

            GUILayout.Space(6);

            // ---------- plot ----------
            var r = GUILayoutUtility.GetRect(PlotW, PlotH, GUILayout.ExpandWidth(false));
            if (_plotTex != null) GUI.DrawTexture(r, _plotTex, ScaleMode.StretchToFill);
            GUILayout.Label($"<color=#4da6f2>■</color> errors/min   " +
                            $"<color=#ffb840>■</color> difficulty d ({c.mapping.minDifficulty:0}–{c.mapping.maxDifficulty:0})   " +
                            $"<color=#ffffff>┄</color> setpoint   " +
                            $"[last {plotWindowSeconds:0}s]", _rich);

            if (!DifficultyAuthority.HasAuthority(c))
            {
                GUILayout.Label(
                    $"<i>The d curve tracks {DifficultyAuthority.CurrentName} (whoever is " +
                    "actually driving). errors/min, e, P and I below are still PI's own " +
                    "estimator, updating in the background so it's pre-converged if you " +
                    "switch back to it.</i>", _small);
            }

            GUILayout.Space(6);

            // ---------- live read-outs ----------
            GUILayout.Label("<b>Regulated signal</b>", _rich);
            GUILayout.Label($"errors/min  y = <b>{c.ErrorsPerMinute:0.00}</b>    " +
                            $"r = {c.Setpoint:0.0}    e = {c.reflexLoop.Error:+0.00;-0.00}", _rich);
            string satTag = c.reflexLoop.Saturated ? "<color=#ff7755>SATURATED</color>" : "";
            GUILayout.Label($"P = {c.reflexLoop.Proportional:0.0000}   " +
                            $"I = {c.reflexLoop.Integral:0.0000}   {satTag}", _rich);
            string pinned = c.IntervalSaturated ? "  <color=#ffcc55>[T pinned — speed only]</color>" : "";
            GUILayout.Label($"d = <b>{c.Difficulty:0.0}</b>   →   " +
                            $"v = {c.NoteSpeed:0} wu/s    interval = {c.SpawnInterval:0.000}s " +
                            $"(f_s = {c.SpawnFrequency:0.00} Hz){pinned}", _rich);
            GUILayout.Label($"<i>mapping: {c.mapping.Describe()}</i>", _small);
            GUILayout.Label($"notes seen {c.TotalNotesSeen}   errors counted {c.TotalErrorsCounted}   " +
                            $"Δt {c.LastTickDt:0.00}s", _small);
            GUILayout.Label($"<i>diagnostic (not regulated): M_t = {c.TimingMarginDiagnostic:0.000}s</i>", _small);

            GUILayout.Space(6);

            // ---------- tuning knobs ----------
            GUILayout.Label("<b>Reflex loop — tuning</b>", _rich);
            c.targetErrorsPerMinute = Slider("setpoint r (err/min)", c.targetErrorsPerMinute, 0f, 40f, "0.0");
            c.reflexLoop.kp         = Slider("Kp  (d per err/min)",  c.reflexLoop.kp, 0f, 2f, "0.0000");
            c.reflexLoop.ti         = Slider("Ti  (s)",              c.reflexLoop.ti, 0.5f, 120f, "0.0");
            GUILayout.Label($"   → Ki = Kp/Ti = {c.reflexLoop.Ki:0.000000}", _small);
            c.errorRateTauSeconds   = Slider("estimator τ (s)",      c.errorRateTauSeconds, 1f, 60f, "0.0");
            c.controlPeriod         = Slider("control period Ts (s)",c.controlPeriod, 0.1f, 5f, "0.00");

            GUILayout.BeginHorizontal();
            GUILayout.Label("step setpoint:", GUILayout.Width(90));
            if (GUILayout.Button("×2")) { c.targetErrorsPerMinute *= 2f; Status("Setpoint stepped up"); }
            if (GUILayout.Button("÷2")) { c.targetErrorsPerMinute *= 0.5f; Status("Setpoint stepped down"); }
            if (GUILayout.Button("→10")) { c.targetErrorsPerMinute = 10f; Status("Setpoint = 10"); }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // ---------- force loops: MIGRATED OUT ----------
            // The four force loops now have their own instrument, ForcePITuningHUD,
            // with a per-lane plot, the keyboard force simulator and the outcome
            // gating toggles. Splitting them keeps each panel small enough to tune
            // without scrolling, and the two loop families are tuned in separate
            // passes anyway (reflex first, force after — they share no gains).
            GUILayout.Label(
                $"<i>Force loops (τ per lane) are tuned in ForcePITuningHUD " +
                $"[{(forceHud != null ? forceHud.toggleKey.ToString() : "F2")}]. " +
                $"Currently {(c.enableForceLoops ? "ON" : "OFF")}.</i>", _small);

            GUILayout.Space(6);

            // ---------- export ----------
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Export tuning CSV")) ExportCsv();
            if (GUILayout.Button("Clear trace", GUILayout.Width(100))) { _trace.Clear(); _plot.Clear(); Status("Trace cleared"); }
            GUILayout.EndHorizontal();
            GUILayout.Label($"trace: {_trace.Count} samples", _small);

            if (Time.time < _statusUntil)
                GUILayout.Label($"<color=#88dd88>{_status}</color>", _rich);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        float Slider(string label, float value, float min, float max, string fmt)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(150));
            float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(190));
            GUILayout.Label(v.ToString(fmt, CultureInfo.InvariantCulture), GUILayout.Width(80));
            GUILayout.EndHorizontal();
            return v;
        }

        void Status(string s) { _status = s; _statusUntil = Time.time + 3f; }

        void ExportCsv()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("t,errors_per_min,setpoint,e,P,I,d,d_norm,noteSpeed,spawnInterval,Kp,Ti,tau_ema,Ts");
                var c = controller;
                foreach (var s in _trace)
                {
                    sb.AppendLine(string.Join(",", new[]
                    {
                        s.t.ToString("F3", CultureInfo.InvariantCulture),
                        s.errorsPerMin.ToString("F4", CultureInfo.InvariantCulture),
                        s.setpoint.ToString("F3", CultureInfo.InvariantCulture),
                        s.e.ToString("F4", CultureInfo.InvariantCulture),
                        s.p.ToString("F6", CultureInfo.InvariantCulture),
                        s.i.ToString("F6", CultureInfo.InvariantCulture),
                        s.d.ToString("F4", CultureInfo.InvariantCulture),
                        s.dNorm.ToString("F5", CultureInfo.InvariantCulture),
                        s.noteSpeed.ToString("F2", CultureInfo.InvariantCulture),
                        s.spawnInterval.ToString("F4", CultureInfo.InvariantCulture),
                        c.reflexLoop.kp.ToString("F6", CultureInfo.InvariantCulture),
                        c.reflexLoop.ti.ToString("F3", CultureInfo.InvariantCulture),
                        c.errorRateTauSeconds.ToString("F2", CultureInfo.InvariantCulture),
                        c.controlPeriod.ToString("F3", CultureInfo.InvariantCulture)
                    }));
                }

                string dir  = Path.Combine(Application.persistentDataPath, "pi_tuning");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"pi_tuning_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                File.WriteAllText(file, sb.ToString());

                Debug.Log($"[PITuningHUD] Wrote {_trace.Count} samples to {file}");
                Status($"Saved: {file}");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Status("Export FAILED — see console");
            }
        }

        static void EnsureStyles()
        {
            if (_rich == null)
                _rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = false };
            if (_small == null)
                _small = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 10 };
        }
    }
}
