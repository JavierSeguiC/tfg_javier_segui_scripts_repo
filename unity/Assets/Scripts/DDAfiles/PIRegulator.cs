using UnityEngine;

namespace DDA
{
    /// <summary>
    /// DISCRETE PI REGULATOR — one loop. Five instances of this run the DDA:
    /// one reflex/timing loop driving the scalar difficulty d, and four
    /// independent per-lane force loops driving τ_ℓ.
    ///
    /// CONTROL LAW (position form)
    ///     e[k] = plantSign · (r − y[k])
    ///     I[k] = I[k−1] + Ki·Ts·e[k]          with Ki = Kp / Ti
    ///     u[k] = clamp( Kp·e[k] + I[k] , uMin, uMax )
    ///
    /// WHY plantSign
    ///   The two loop families have opposite process gains. Raising d RAISES the
    ///   error rate (positive gain, plantSign = +1). Raising τ_ℓ LOWERS the force
    ///   margin (negative gain, plantSign = −1). Folding the sign into the error
    ///   lets both families share this one class with positive, physically
    ///   meaningful gains in the inspector, instead of asking the user to type a
    ///   negative Kp for the force loops and get it wrong.
    ///
    /// WHY NO DERIVATIVE TERM
    ///   The measured signals are a rate estimated from a sparse point process and
    ///   an EMA of noisy force samples. Near the 10 err/min setpoint only ~1.7
    ///   events land inside one EMA time constant, so fractional measurement noise
    ///   is on the order of ±75%. Differentiating that would inject pure noise into
    ///   the actuator. D is therefore absent by construction, not merely set to 0.
    ///
    /// WHY NO ANTI-WINDUP
    ///   The difficulty range is designed so the setpoint is strictly INTERIOR:
    ///   at d = 0 every player clears every note (y < r ⇒ e > 0, pushing up off the
    ///   floor) and at d = 1 the game is unplayable (y ≫ r ⇒ e < 0, pushing down off
    ///   the ceiling). At both limits the error already points back into the
    ///   interior, so the integrator unwinds by itself and can never charge against
    ///   a wall. Output saturation is still applied unconditionally — that is a
    ///   physical actuator constraint, a separate thing from anti-windup. The
    ///   integral term is therefore left free-running while saturated; conditional
    ///   integration was evaluated and removed (Aug 2026) since it was never
    ///   exercised in practice and added complexity for a failure mode that was
    ///   never observed.
    ///
    /// BUMPLESS TRANSFER
    ///   Reset() seeds the integrator so the very first output equals whatever the
    ///   actuator is already at: I = u₀ − Kp·e₀. Without this the loop jerks the
    ///   difficulty on its first tick, which both feels bad and corrupts the step
    ///   response you are trying to read while tuning.
    /// </summary>
    [System.Serializable]
    public class PIRegulator
    {
        [Header("Gains")]
        [Tooltip("Proportional gain Kp, in [output units] per [measurement unit]. " +
                 "For the reflex loop that is d per (error/min).")]
        public float kp = 0.00483f;

        [Tooltip("Integral time Ti in SECONDS (Ki = Kp/Ti). Larger Ti = weaker " +
                 "integral action. Ti = 0 disables the integrator (pure P).")]
        public float ti = 10f;

        [Header("Output saturation")]
        public float uMin = 0f;
        public float uMax = 1f;

        [Header("Structure")]
        [Tooltip("+1 when raising the output RAISES the measurement (reflex loop: " +
                 "more difficulty -> more errors). −1 when raising the output LOWERS " +
                 "the measurement (force loop: higher threshold -> lower margin).")]
        public float plantSign = 1f;

        // ---------------- live state (read-only for the HUD) ----------------
        public float Integral   { get; private set; }
        public float Error      { get; private set; }
        public float Proportional { get; private set; }
        public float Output     { get; private set; }
        public bool  Saturated  { get; private set; }
        public bool  Initialised { get; private set; }

        /// <summary>Effective integral gain Ki = Kp/Ti (0 when Ti is 0).</summary>
        public float Ki => (ti > 1e-6f) ? kp / ti : 0f;

        /// <summary>
        /// Bumpless (re)start. currentOutput is where the actuator physically IS
        /// right now; the integrator is back-solved so Step() returns that value
        /// on the first tick if nothing has changed.
        /// </summary>
        public void Reset(float currentOutput, float reference, float measurement)
        {
            currentOutput = Mathf.Clamp(currentOutput, uMin, uMax);
            Error         = plantSign * (reference - measurement);
            Proportional  = kp * Error;
            Integral      = currentOutput - Proportional;
            Output        = currentOutput;
            Saturated     = false;
            Initialised   = true;
        }

        /// <summary>Reset without a valid measurement yet (integrator holds the output).</summary>
        public void ResetOutputOnly(float currentOutput)
        {
            currentOutput = Mathf.Clamp(currentOutput, uMin, uMax);
            Error        = 0f;
            Proportional = 0f;
            Integral     = currentOutput;
            Output       = currentOutput;
            Saturated    = false;
            Initialised  = true;
        }

        /// <summary>
        /// One control tick. dt is the ACTUAL elapsed time since the previous tick
        /// (not the nominal period) so the integral stays correct if a frame hitches.
        /// </summary>
        public float Step(float reference, float measurement, float dt)
        {
            if (!Initialised) ResetOutputOnly(Output);
            if (dt <= 0f) return Output;

            Error        = plantSign * (reference - measurement);
            Proportional = kp * Error;

            // Integral is always updated (free-running) — no conditional
            // integration. See class summary, WHY NO ANTI-WINDUP: the
            // operating range is designed so the reference is strictly
            // interior, so the integrator discharges on its own at both
            // rails instead of charging against a wall.
            Integral = Integral + Ki * dt * Error;

            float uRaw     = Proportional + Integral;
            float uClamped = Mathf.Clamp(uRaw, uMin, uMax);

            Saturated = (uRaw > uMax) || (uRaw < uMin);
            Output    = uClamped;
            return Output;
        }
    }
}
