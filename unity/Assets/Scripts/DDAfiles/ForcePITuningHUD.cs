using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace DDA
{
    /// <summary>
    /// LIVE TUNING INSTRUMENT for the FOUR PER-LANE FORCE LOOPS of
    /// PIDifficultyController. The sibling of PITuningHUD, which owns loop 1 (the
    /// reflex/timing loop driving d). Same contract: draw-only and input-only, it
    /// never writes GameDifficulty itself, it only edits the controller's public
    /// tuning fields and renders its public read-outs. Deleting this file changes
    /// nothing about the control law.
    ///
    /// WHY A SEPARATE PANEL
    ///   The two loop families share no gains, no estimator and no setpoint, and are
    ///   tuned in separate passes. Stacking eight loops' worth of controls in one
    ///   scrolling window meant tuning by scrollbar. Splitting them also lets this
    ///   panel carry things that are meaningless to loop 1 — a lane selector, the
    ///   keyboard force simulator, the outcome gating.
    ///
    /// WHAT IT GIVES YOU
    ///   • A scrolling plot of force margin M_F,ℓ against the target, with τ_ℓ
    ///     overlaid on its own scale, for ONE selected lane at a time. Signed axis
    ///     with a zero line, because unlike errors/min, M_F is meaningfully negative:
    ///     below the zero line the player is failing the force requirement.
    ///   • A lane selector (index / middle / ring / pinky), because four overlaid
    ///     traces is unreadable and the loops are independent anyway.
    ///   • Live sliders for the shared gains, the target margin, the estimator τ and
    ///     the τ_ℓ saturation limits.
    ///   • THE KEYBOARD FORCE SIMULATOR. A keyboard key is binary, so without this
    ///     the whole force channel takes exactly two values and there is nothing to
    ///     tune. The force slider sets the mean press force and the noise slider sets
    ///     the press-to-press spread, which is what lets you tune the gains against a
    ///     realistically noisy measurement before the hardware exists. See
    ///     InputManagerScript for why the noise is sampled per press, not per frame.
    ///   • Per-lane state table: τ, M_F, e, saturation, staleness.
    ///   • CSV export of all four lanes for the memoria figures.
    ///
    /// SUGGESTED HAND-TUNING PROCEDURE
    ///   1. Enable the simulator. Set force ≈ 0.6, noise 0. Enable the force loops.
    ///   2. Play. Every lane's τ should converge so that M_F sits at the target. With
    ///      zero noise and force 0.6 and target 0.05, τ must land near 0.55 — that is
    ///      an ARITHMETIC check on the loop, not a judgement call, because the slope
    ///      of M_F on τ is exactly −1. If it settles anywhere else, something is wrong
    ///      upstream of the gains; fix that before tuning.
    ///   3. Step the TARGET MARGIN and watch the response. That is your closed-loop
    ///      step test. Too slow? Raise Kp_F. Hunts or overshoots? Lower Kp_F.
    ///      Steady-state offset? Lower Ti_F. Integrator ramping past? Raise Ti_F.
    ///   4. Step the SIMULATED FORCE instead (0.6 -> 0.4). This is the disturbance
    ///      the loop actually exists to reject: a player getting weaker, i.e. fatigue.
    ///      τ should track down to follow it. This is the more honest test of the two.
    ///   5. Now raise NOISE and repeat. If τ starts jittering while the mean force is
    ///      steady, Kp_F is too high for the measurement noise, or the margin τ is too
    ///      short. Prefer lowering Kp_F first — lengthening the estimator adds lag to
    ///      the plant and invalidates the gains you just set.
    ///   6. Set one lane's scale to 0.6 in InputManagerScript.simulatedLaneScale and
    ///      confirm ONLY that lane's τ moves. That is the independence claim of the
    ///      four-SISO-loops design, demonstrated in one screenshot.
    ///   7. Export the CSV. The force-step response is a Ch.4 figure.
    /// </summary>
    [DisallowMultipleComponent]
    public class ForcePITuningHUD : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("Leave empty to auto-find the controller in the scene.")]
        public PIDifficultyController controller;

        [Tooltip("Leave empty to auto-find. Only needed for the force simulator " +
                 "controls — the rest of the panel works without it.")]
        public InputManagerScript inputManager;

        [Header("Display")]
        public bool visible = true;
        [Tooltip("Key that toggles the whole panel.")]
        public KeyCode toggleKey = KeyCode.F2;
        [Tooltip("Margin in px from the RIGHT edge of the screen. The force panel is " +
                 "right-anchored so it sits opposite the reflex panel (left) instead of " +
                 "overlapping it — its x is computed from Screen.width at draw time, so " +
                 "it stays put across resolution and window-size changes.")]
        public float rightMargin = 10f;
        public bool showSimulatorPanel = true;
        public bool showGatingPanel = true;

        [Header("Plot")]
        [Tooltip("Seconds of history shown in the scrolling plot.")]
        public float plotWindowSeconds = 180f;
        [Tooltip("Samples per second stored for the plot and the CSV export.")]
        public float plotSampleRate = 4f;
        [Tooltip("Half-height of the margin axis. The plot auto-expands past this if " +
                 "the signal exceeds it. The axis is SIGNED: zero sits mid-plot.")]
        public float plotMarginRange = 0.3f;

        [Header("Plot colours")]
        public Color colMargin     = new Color(0.45f, 0.90f, 0.55f);
        public Color colTarget     = new Color(1.00f, 1.00f, 1.00f, 0.85f);
        public Color colTau        = new Color(1.00f, 0.72f, 0.25f);
        public Color colRawForce   = new Color(0.55f, 0.55f, 0.95f, 0.75f);
        public Color colZero       = new Color(1f, 1f, 1f, 0.28f);
        public Color colBackground = new Color(0.07f, 0.08f, 0.10f, 1f);
        public Color colGrid       = new Color(1f, 1f, 1f, 0.10f);

        static readonly string[] LaneNames = { "0 · index", "1 · middle", "2 · ring", "3 · pinky" };

        // ---------------- internals ----------------
        struct Sample
        {
            public float t, target;
            public float m0, m1, m2, m3;      // force margin per lane
            public float t0, t1, t2, t3;      // tau per lane
            public float e0, e1, e2, e3;      // loop error per lane
            public float rawForce;            // last simulated/device force, selected lane

            public float Margin(int l) => l == 0 ? m0 : l == 1 ? m1 : l == 2 ? m2 : m3;
            public float Tau(int l)    => l == 0 ? t0 : l == 1 ? t1 : l == 2 ? t2 : t3;
            public float Err(int l)    => l == 0 ? e0 : l == 1 ? e1 : l == 2 ? e2 : e3;
        }

        readonly List<Sample> _trace = new List<Sample>();   // full session, for CSV
        readonly Queue<Sample> _plot = new Queue<Sample>();  // windowed, for drawing

        Texture2D _plotTex;
        Color32[] _pixels;
        const int PlotW = 460, PlotH = 150;

        int  _selectedLane;
        bool _laneDropdownOpen;

        float _nextSampleAt;
        float _nextRedrawAt;
        Vector2 _scroll;
        string _status = "";
        float _statusUntil;

        static GUIStyle _rich, _small;

        // If the screen is too narrow to fit both 500px panels side by side, keep this
        // one from sliding left over the reflex panel — it clamps here and the user can
        // hide one with F1/F2. 520 = reflex panel x(10) + width(500) + 10 gap.
        const float panelXMinFallback = 520f;

        // ══════════════════════════════════════════════════════════════════

        void Awake()
        {
            if (controller   == null) controller   = FindFirstObjectByType<PIDifficultyController>();
            if (inputManager == null) inputManager = FindFirstObjectByType<InputManagerScript>();

            _plotTex = new Texture2D(PlotW, PlotH, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _pixels = new Color32[PlotW * PlotH];
        }

        void OnEnable()  => GameFlow.OnGameStarted += HandleNewSession;
        void OnDisable() => GameFlow.OnGameStarted -= HandleNewSession;

        /// <summary>
        /// Wipe the plot/trace at the START of every session, for the same reason
        /// PITuningHUD does: Time.time respects Time.timeScale, which is 0 in the menu
        /// and while paused, so time-based windowing alone never ages out the previous
        /// session's trailing samples however long the menu sits open.
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

            // The force loops' estimator, like the reflex one, keeps running even while
            // PI isn't authoritative — so these stay meaningful to watch either way,
            // they just aren't driving the game.
            var s = new Sample
            {
                t        = now,
                target   = c.targetForceMargin,
                m0 = c.ForceMargin(0), m1 = c.ForceMargin(1),
                m2 = c.ForceMargin(2), m3 = c.ForceMargin(3),
                t0 = c.Tau(0), t1 = c.Tau(1), t2 = c.Tau(2), t3 = c.Tau(3),
                e0 = LoopErr(0), e1 = LoopErr(1), e2 = LoopErr(2), e3 = LoopErr(3),
                rawForce = inputManager != null ? inputManager.LastForce(_selectedLane) : float.NaN
            };

            _trace.Add(s);
            _plot.Enqueue(s);
            while (_plot.Count > 0 && now - _plot.Peek().t > plotWindowSeconds) _plot.Dequeue();
        }

        float LoopErr(int lane)
        {
            var l = controller.ForceLoop(lane);
            return l != null ? l.Error : 0f;
        }

        // ══════════════════════════════════════════════════════════════════
        //  PLOT RENDERING (into a Texture2D — no scene wiring, no extra cameras)
        // ══════════════════════════════════════════════════════════════════

        void RedrawPlot()
        {
            if (_plotTex == null || _plot.Count < 2) { ClearPixels(); Blit(); return; }

            ClearPixels();

            // Signed y-scale for the margin axis, auto-expanding. Unlike errors/min,
            // M_F is meaningfully negative — the sign IS the information (below zero
            // the player is failing the force requirement) — so the axis is centred
            // on zero rather than starting there.
            float yAbs = plotMarginRange;
            foreach (var s in _plot)
            {
                float v = s.Margin(_selectedLane);
                if (!float.IsNaN(v)) yAbs = Mathf.Max(yAbs, Mathf.Abs(v) * 1.1f);
            }
            yAbs = Mathf.Max(yAbs, Mathf.Abs(controller.targetForceMargin) * 1.4f, 0.02f);

            // grid: horizontal quarters
            for (int g = 1; g < 4; g++)
            {
                int y = Mathf.RoundToInt(PlotH * g / 4f);
                for (int x = 0; x < PlotW; x++) SetPx(x, y, colGrid);
            }

            // zero line — solid, brighter than the grid
            int zeroY = MarginToY(0f, yAbs);
            for (int x = 0; x < PlotW; x++) SetPx(x, zeroY, colZero);

            float t0 = 0f, t1 = 0f;
            bool first = true;
            foreach (var s in _plot)
            {
                if (first) { t0 = s.t; first = false; }
                t1 = s.t;
            }
            float span = Mathf.Max(1e-3f, t1 - t0);

            // target line
            int tgY = MarginToY(controller.targetForceMargin, yAbs);
            for (int x = 0; x < PlotW; x += 4) { SetPx(x, tgY, colTarget); SetPx(x + 1, tgY, colTarget); }

            // series
            int prevXm = -1, prevYm = 0, prevXt = -1, prevYt = 0, prevXf = -1, prevYf = 0;
            float tauMin = controller.minTau, tauMax = controller.maxTau;
            float tauSpan = Mathf.Max(1e-4f, tauMax - tauMin);

            foreach (var s in _plot)
            {
                int x = Mathf.Clamp(Mathf.RoundToInt((s.t - t0) / span * (PlotW - 1)), 0, PlotW - 1);

                float m = s.Margin(_selectedLane);
                if (!float.IsNaN(m))
                {
                    int ym = MarginToY(m, yAbs);
                    if (prevXm >= 0) DrawLine(prevXm, prevYm, x, ym, colMargin);
                    prevXm = x; prevYm = ym;
                }

                // tau on its own normalised scale across [minTau, maxTau]
                float tau = s.Tau(_selectedLane);
                if (!float.IsNaN(tau))
                {
                    int yt = Mathf.Clamp(
                        Mathf.RoundToInt((tau - tauMin) / tauSpan * (PlotH - 1)), 0, PlotH - 1);
                    if (prevXt >= 0) DrawLine(prevXt, prevYt, x, yt, colTau);
                    prevXt = x; prevYt = yt;
                }

                // raw instantaneous force, also on [0,1] -> full height
                if (!float.IsNaN(s.rawForce))
                {
                    int yf = Mathf.Clamp(
                        Mathf.RoundToInt(s.rawForce * (PlotH - 1)), 0, PlotH - 1);
                    if (prevXf >= 0) DrawLine(prevXf, prevYf, x, yf, colRawForce);
                    prevXf = x; prevYf = yf;
                }
            }

            Blit();
        }

        /// <summary>Signed margin -> pixel row, with 0 at mid-plot.</summary>
        int MarginToY(float v, float yAbs)
        {
            float norm = 0.5f + 0.5f * (v / Mathf.Max(1e-4f, yAbs));
            return Mathf.Clamp(Mathf.RoundToInt(norm * (PlotH - 1)), 0, PlotH - 1);
        }

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
            // Right-anchored: recomputed every frame from Screen.width so the panel
            // tracks the right edge across resolution / window changes instead of
            // sitting at a fixed x that only happens to clear the reflex panel at one
            // size. Reflex HUD stays left, this one stays right.
            float x = Mathf.Max(panelXMinFallback, Screen.width - w - rightMargin);
            GUILayout.BeginArea(new Rect(x, 10, w, Screen.height - 20), GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            var c = controller;

            // ---------- header ----------
            GUILayout.Label($"<b>PI DDA — FORCE loops (τ per lane)</b>   (<i>{toggleKey} to hide</i>)", _rich);
            GUILayout.Label($"Authority: <b>{DifficultyAuthority.CurrentName}</b>   " +
                            $"input: <b>{InputManagerScript.ActiveSourceName}</b>", _rich);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(c.IsActive ? "PI: ACTIVE (click to release)" : "PI: inactive (click to take over)"))
            {
                if (c.IsActive) c.Deactivate(); else c.Activate();
            }
            if (GUILayout.Button("Reset loops", GUILayout.Width(100))) { c.ResetLoops(); Status("Loops reset"); }
            GUILayout.EndHorizontal();

            bool wasOn = c.enableForceLoops;
            c.enableForceLoops = GUILayout.Toggle(c.enableForceLoops,
                c.enableForceLoops ? " force loops ON" : " force loops OFF (τ left at last written values)");
            if (wasOn != c.enableForceLoops)
                Status(c.enableForceLoops ? "Force loops enabled" : "Force loops disabled");

            if (!c.enableForceLoops)
                GUILayout.Label("<color=#ffcc55><i>Loops are off — τ is not being written. Turn on to tune.</i></color>", _small);

            if (c.IsWarmingUp)
                GUILayout.Label($"<color=#ffcc55><b>WARM-UP</b> — observing only, {c.WarmupRemaining:0.0}s left</color>", _rich);

            GUILayout.Space(6);

            // ---------- lane selector (dropdown) ----------
            GUILayout.BeginHorizontal();
            GUILayout.Label("plot lane:", GUILayout.Width(70));
            if (GUILayout.Button(LaneNames[_selectedLane] + "  ▾", GUILayout.Width(120)))
                _laneDropdownOpen = !_laneDropdownOpen;
            GUILayout.EndHorizontal();

            if (_laneDropdownOpen)
            {
                for (int l = 0; l < PIDifficultyController.LaneCount; l++)
                {
                    if (GUILayout.Button((l == _selectedLane ? "● " : "   ") + LaneNames[l]))
                    {
                        _selectedLane = l;
                        _laneDropdownOpen = false;
                        Status($"Plotting lane {l}");
                    }
                }
            }

            // ---------- plot ----------
            var r = GUILayoutUtility.GetRect(PlotW, PlotH, GUILayout.ExpandWidth(false));
            if (_plotTex != null) GUI.DrawTexture(r, _plotTex, ScaleMode.StretchToFill);
            GUILayout.Label($"<color=#73e68c>■</color> M_F (signed, 0 = mid)   " +
                            $"<color=#ffb840>■</color> τ ({c.minTau:0.00}–{c.maxTau:0.00})   " +
                            $"<color=#8c8cf2>■</color> raw force   " +
                            $"<color=#ffffff>┄</color> target   [last {plotWindowSeconds:0}s]", _rich);

            GUILayout.Space(6);

            // ---------- live read-outs ----------
            GUILayout.Label("<b>Regulated signal — per lane</b>", _rich);
            for (int l = 0; l < PIDifficultyController.LaneCount; l++)
            {
                var loop = c.ForceLoop(l);
                string sel   = (l == _selectedLane) ? "<color=#73e68c>▶</color> " : "   ";
                string sat   = (loop != null && loop.Saturated) ? "  <color=#ff7755>SAT</color>" : "";
                GUILayout.Label(
                    $"{sel}lane {l}:  τ = <b>{c.Tau(l):0.000}</b>   M_F = {c.ForceMargin(l):+0.000;-0.000}   " +
                    $"e = {(loop != null ? loop.Error : 0f):+0.000;-0.000}   " +
                    $"I = {(loop != null ? loop.Integral : 0f):0.000}{sat}", _rich);
            }
            GUILayout.Label($"r = {c.targetForceMargin:0.000}   Ki = Kp/Ti = {c.forceLoopTemplate.Ki:0.000000}   " +
                            $"Δt {c.LastTickDt:0.00}s", _small);

            GUILayout.Space(6);

            // ---------- tuning knobs ----------
            GUILayout.Label("<b>Force loops — tuning</b> <i>(shared by all four lanes)</i>", _rich);
            c.targetForceMargin     = Slider("target margin r", c.targetForceMargin, -0.2f, 0.4f, "0.000");
            c.forceLoopTemplate.kp  = Slider("Kp_F (τ per unit)", c.forceLoopTemplate.kp, 0f, 1f, "0.0000");
            c.forceLoopTemplate.ti  = Slider("Ti_F (s)",          c.forceLoopTemplate.ti, 1f, 120f, "0.0");
            c.forceMarginTauSeconds = Slider("margin EMA τ (s)",  c.forceMarginTauSeconds, 1f, 60f, "0.0");

            GUILayout.BeginHorizontal();
            GUILayout.Label("τ limits:", GUILayout.Width(70));
            c.minTau = Mathf.Clamp(GUILayout.HorizontalSlider(c.minTau, 0f, 1f, GUILayout.Width(120)), 0f, c.maxTau);
            GUILayout.Label($"{c.minTau:0.00}", GUILayout.Width(38));
            c.maxTau = Mathf.Clamp(GUILayout.HorizontalSlider(c.maxTau, 0f, 1f, GUILayout.Width(120)), c.minTau, 1f);
            GUILayout.Label($"{c.maxTau:0.00}", GUILayout.Width(38));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("step target:", GUILayout.Width(90));
            if (GUILayout.Button("×2"))    { c.targetForceMargin *= 2f;   Status("Target stepped up"); }
            if (GUILayout.Button("÷2"))    { c.targetForceMargin *= 0.5f; Status("Target stepped down"); }
            if (GUILayout.Button("→0.05")) { c.targetForceMargin = 0.05f; Status("Target = 0.05"); }
            if (GUILayout.Button("→0"))    { c.targetForceMargin = 0f;    Status("Target = 0"); }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // ---------- estimator gating ----------
            if (showGatingPanel)
            {
                GUILayout.Label("<b>Which outcomes feed M_F</b>", _rich);
                GUILayout.Label("<i>Missed notes are excluded automatically — no press, so " +
                                "forceMargin is NaN. These control the outcomes that DO carry " +
                                "force data.</i>", _small);
                c.forceFromHits           = GUILayout.Toggle(c.forceFromHits,           " Hits");
                c.forceFromForceFailures  = GUILayout.Toggle(c.forceFromForceFailures,  " ForceInsufficient / UnderHeld  <i>(keep ON — survivor bias)</i>");
                c.forceFromTimingFailures = GUILayout.Toggle(c.forceFromTimingFailures, " EarlyPress / LatePress  <i>(couples reflex into force)</i>");
                c.forceFromWrongLane      = GUILayout.Toggle(c.forceFromWrongLane,      " WrongLane  <i>(press is from another lane)</i>");
                GUILayout.Space(6);
            }

            // ---------- keyboard force simulator ----------
            if (showSimulatorPanel)
            {
                GUILayout.Label("<b>Keyboard force simulator</b> <i>(dev tool)</i>", _rich);

                if (inputManager == null)
                {
                    GUILayout.Label("<color=#ff7755>No InputManagerScript found in scene.</color>", _rich);
                }
                else if (InputManagerScript.DeviceConnected)
                {
                    GUILayout.Label($"<color=#88dd88>Hardware source active " +
                                    $"({InputManagerScript.ActiveSourceName}) — simulator bypassed.</color>", _rich);
                }
                else
                {
                    var im = inputManager;
                    im.simulateAnalogForce = GUILayout.Toggle(im.simulateAnalogForce,
                        im.simulateAnalogForce
                            ? " ON — keys produce analog force"
                            : " OFF — keys produce binary 1.0 (nothing to tune)");

                    if (im.simulateAnalogForce)
                    {
                        im.simulatedForce = Slider("force (mean)", im.simulatedForce, 0f, 1f, "0.000");
                        im.simulatedForceNoise = Slider("noise σ", im.simulatedForceNoise, 0f, 0.4f, "0.000");
                        im.simulatedAttackSeconds = Slider("attack (s)", im.simulatedAttackSeconds, 0f, 0.5f, "0.000");
                        im.simulatedTremorFraction = Slider("tremor (×σ)", im.simulatedTremorFraction, 0f, 2f, "0.00");
                        im.simulatedTremorHz = Slider("tremor (Hz)", im.simulatedTremorHz, 0.1f, 12f, "0.0");

                        GUILayout.Label("<i>One force is drawn per PRESS from N(mean, σ²) and held — " +
                                        "not per frame, or fMax would climb with σ instead of the " +
                                        "spread widening.</i>", _small);

                        // Expected steady state is arithmetic here: slope of M_F on τ is
                        // exactly −1, so τ* = mean − target. Showing it turns "does it
                        // converge?" into a check rather than a judgement call.
                        float expectedTau = Mathf.Clamp(im.simulatedForce - c.targetForceMargin,
                                                        c.minTau, c.maxTau);
                        GUILayout.Label($"<i>expected steady state: τ* = force − target = " +
                                        $"<b>{expectedTau:0.000}</b> (all lanes at scale 1.0)</i>", _small);

                        GUILayout.BeginHorizontal();
                        GUILayout.Label("step force:", GUILayout.Width(90));
                        if (GUILayout.Button("0.8")) { im.simulatedForce = 0.8f; Status("Force = 0.80 (strong)"); }
                        if (GUILayout.Button("0.6")) { im.simulatedForce = 0.6f; Status("Force = 0.60"); }
                        if (GUILayout.Button("0.4")) { im.simulatedForce = 0.4f; Status("Force = 0.40 (fatigued)"); }
                        GUILayout.EndHorizontal();

                        // per-lane scale
                        GUILayout.Label("<i>per-lane scale — drop one lane to prove the four loops " +
                                        "are independent</i>", _small);
                        if (im.simulatedLaneScale != null &&
                            im.simulatedLaneScale.Length >= PIDifficultyController.LaneCount)
                        {
                            for (int l = 0; l < PIDifficultyController.LaneCount; l++)
                                im.simulatedLaneScale[l] =
                                    Slider($"  lane {l} ×", im.simulatedLaneScale[l], 0f, 1.5f, "0.00");
                        }

                        GUILayout.BeginHorizontal();
                        GUILayout.Label("live force:", GUILayout.Width(70));
                        for (int l = 0; l < PIDifficultyController.LaneCount; l++)
                            GUILayout.Label($"{l}: <b>{im.LastForce(l):0.00}</b>", _rich, GUILayout.Width(70));
                        GUILayout.EndHorizontal();
                    }
                }
                GUILayout.Space(6);
            }

            // ---------- export ----------
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Export force tuning CSV")) ExportCsv();
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
                var c  = controller;
                var im = inputManager;
                var sb = new StringBuilder();

                // All four lanes are exported regardless of which one is on screen —
                // the selector is a viewing convenience, not a recording filter.
                sb.AppendLine("t,target_margin," +
                              "M_F0,M_F1,M_F2,M_F3," +
                              "tau0,tau1,tau2,tau3," +
                              "e0,e1,e2,e3," +
                              "raw_force_selected,selected_lane," +
                              "Kp_F,Ti_F,tau_ema,tau_min,tau_max," +
                              "sim_on,sim_force,sim_noise");

                foreach (var s in _trace)
                {
                    sb.AppendLine(string.Join(",", new[]
                    {
                        s.t.ToString("F3", CultureInfo.InvariantCulture),
                        s.target.ToString("F4", CultureInfo.InvariantCulture),
                        s.m0.ToString("F5", CultureInfo.InvariantCulture),
                        s.m1.ToString("F5", CultureInfo.InvariantCulture),
                        s.m2.ToString("F5", CultureInfo.InvariantCulture),
                        s.m3.ToString("F5", CultureInfo.InvariantCulture),
                        s.t0.ToString("F5", CultureInfo.InvariantCulture),
                        s.t1.ToString("F5", CultureInfo.InvariantCulture),
                        s.t2.ToString("F5", CultureInfo.InvariantCulture),
                        s.t3.ToString("F5", CultureInfo.InvariantCulture),
                        s.e0.ToString("F5", CultureInfo.InvariantCulture),
                        s.e1.ToString("F5", CultureInfo.InvariantCulture),
                        s.e2.ToString("F5", CultureInfo.InvariantCulture),
                        s.e3.ToString("F5", CultureInfo.InvariantCulture),
                        s.rawForce.ToString("F4", CultureInfo.InvariantCulture),
                        _selectedLane.ToString(CultureInfo.InvariantCulture),
                        c.forceLoopTemplate.kp.ToString("F6", CultureInfo.InvariantCulture),
                        c.forceLoopTemplate.ti.ToString("F3", CultureInfo.InvariantCulture),
                        c.forceMarginTauSeconds.ToString("F2", CultureInfo.InvariantCulture),
                        c.minTau.ToString("F3", CultureInfo.InvariantCulture),
                        c.maxTau.ToString("F3", CultureInfo.InvariantCulture),
                        (im != null && im.simulateAnalogForce) ? "1" : "0",
                        im != null ? im.simulatedForce.ToString("F4", CultureInfo.InvariantCulture) : "NaN",
                        im != null ? im.simulatedForceNoise.ToString("F4", CultureInfo.InvariantCulture) : "NaN"
                    }));
                }

                string dir = Path.Combine(Application.persistentDataPath, "pi_tuning");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"pi_force_tuning_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                File.WriteAllText(file, sb.ToString());

                Debug.Log($"[ForcePITuningHUD] Wrote {_trace.Count} samples to {file}");
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
