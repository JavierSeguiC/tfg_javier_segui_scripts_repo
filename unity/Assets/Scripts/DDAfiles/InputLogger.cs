using System.Collections.Generic;
using UnityEngine;

namespace DDA
{
    /// <summary>
    /// Polls force on each lane at a fixed rate, detects press/release threshold crossings,
    /// and emits InputPressEvents on release. Computes summary statistics per press:
    /// peak force, average force, and the duration during which force was sustained at
    /// >= 80% of the peak (sustained-80 metric).
    ///
    /// It reads force via InputManagerScript.Instance.
    /// </summary>
    public class InputLogger : MonoBehaviour
    {
        [Header("Lane configuration")]
        [Tooltip("Number of input lanes (fingers).")]
        public int laneCount = 4;

        [Header("Thresholds (force is assumed normalized to [0,1])")]
        [Tooltip("Force fraction at which a press is considered to have begun.")]
        public float pressThreshold = 0.05f;
        [Tooltip("Force fraction at which a press is considered to have ended. " +
                 "Slight hysteresis below pressThreshold to avoid chatter on noisy devices.")]
        public float releaseThreshold = 0.04f;

        [Header("Polling")]
        [Tooltip("Sampling rate in Hz for force readings.")]
        public float pollRateHz = 50f;
        [Tooltip("Maximum force samples kept per press. Caps memory if a press is very long.")]
        public int maxProfileSamples = 1000;
        [Tooltip("If false, the force profile is discarded after summary stats are computed " +
                 "(saves memory; profile won't be available in the InputPressEvent).")]
        public bool keepFullProfile = true;

        // ---- Internal state, per lane ----
        private bool[] isPressed;
        private float[] tStart;
        private float[] fMax;
        private float[] fSum;
        private int[] sampleCount;
        private List<ForceSample>[] profiles;
        private int[] pendingEventIds;   // event id assigned at BeginPress, used at EndPress

        private float pollAccumulator;
        private int nextEventId = 1;

        /// <summary>
        /// Full session log of every press event. Read by the diagnostics module
        /// at end of session.
        /// </summary>
        public readonly List<InputPressEvent> sessionLog = new List<InputPressEvent>();

        void Awake()
        {
            isPressed = new bool[laneCount];
            tStart = new float[laneCount];
            fMax = new float[laneCount];
            fSum = new float[laneCount];
            sampleCount = new int[laneCount];
            pendingEventIds = new int[laneCount];
            profiles = new List<ForceSample>[laneCount];
            for (int i = 0; i < laneCount; i++) profiles[i] = new List<ForceSample>();
        }

        void Update()
        {
            float dt = 1f / pollRateHz;
            pollAccumulator += Time.deltaTime;
            // Fixed-rate poll loop, decoupled from frame rate.
            while (pollAccumulator >= dt)
            {
                pollAccumulator -= dt;
                PollAllLanes(Time.time);
            }
        }

        void PollAllLanes(float t)
        {
            for (int lane = 0; lane < laneCount; lane++)
            {
                float f = ReadForce(lane);

                if (!isPressed[lane])
                {
                    if (f >= pressThreshold)
                        BeginPress(lane, t, f);
                }
                else
                {
                    AccumulateSample(lane, t, f);
                    if (f < releaseThreshold)
                        EndPress(lane, t);
                }
            }
        }

        float ReadForce(int lane)
        {
            // Hook into the existing input manager. Replace this when the real
            // rehabilitation device is wired in — the rest of the pipeline is agnostic.
            if (InputManagerScript.Instance != null)
                return InputManagerScript.Instance.GetForceForLane(lane);
            return 0f;
        }

        void BeginPress(int lane, float t, float f)
        {
            isPressed[lane] = true;
            tStart[lane] = t;
            fMax[lane] = f;
            fSum[lane] = f;
            sampleCount[lane] = 1;
            profiles[lane].Clear();
            profiles[lane].Add(new ForceSample(t, f));

            int id = nextEventId++;
            pendingEventIds[lane] = id;

            // Notify subscribers (chiefly: NoteResolver) so matching against active
            // notes can happen at press-begin time. The complete InputPressEvent
            // arrives later on release.
            DDAEventBus.RaisePressBegin(lane, t, id);
        }

        void AccumulateSample(int lane, float t, float f)
        {
            if (f > fMax[lane]) fMax[lane] = f;
            fSum[lane] += f;
            sampleCount[lane]++;
            if (profiles[lane].Count < maxProfileSamples)
                profiles[lane].Add(new ForceSample(t, f));
        }

        void EndPress(int lane, float t)
        {
            isPressed[lane] = false;

            float duration = t - tStart[lane];
            float fAvg = sampleCount[lane] > 0 ? fSum[lane] / sampleCount[lane] : 0f;
            float sustained80 = ComputeSustained80(profiles[lane], fMax[lane]);

            var evt = new InputPressEvent
            {
                eventId = pendingEventIds[lane],
                lane = lane,
                tPress = tStart[lane],
                tRelease = t,
                duration = duration,
                fMax = fMax[lane],
                fAvg = fAvg,
                fSustained80 = sustained80,
                profile = keepFullProfile ? new List<ForceSample>(profiles[lane]) : null
            };

            sessionLog.Add(evt);
            DDAEventBus.RaiseInputPress(evt);
        }

        /// <summary>
        /// Time during which the recorded force was at or above 80% of the peak.
        /// Conservative trapezoidal-style measure: a sample interval counts only if
        /// BOTH endpoints are above threshold.
        /// </summary>
        static float ComputeSustained80(List<ForceSample> profile, float fMax)
        {
            if (profile.Count < 2 || fMax <= 0f) return 0f;
            float threshold = 0.8f * fMax;
            float total = 0f;
            for (int i = 1; i < profile.Count; i++)
            {
                if (profile[i - 1].f >= threshold && profile[i].f >= threshold)
                    total += profile[i].t - profile[i - 1].t;
            }
            return total;
        }
    }
}
