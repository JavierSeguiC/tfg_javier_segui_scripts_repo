using System;
using System.Collections.Generic;
using UnityEngine;

namespace DDA
{
    /// <summary>
    /// MUTUAL-EXCLUSION ARBITER for everything that writes GameDifficulty.Instance.
    ///
    /// THE PROBLEM
    ///   Three scripts can now write difficulty: PIDifficultyController (the real
    ///   controller), RuleBasedDDAController (the prototype baseline) and
    ///   DifficultyPresetSwitcher (debug / manual operating points). If two of them
    ///   write on the same frame the game silently gets whichever wrote last, and
    ///   any closed-loop experiment is invalid — the "plant" is being poked by an
    ///   unmodelled second hand.
    ///
    /// THE RULE
    ///   Exactly ONE registered writer holds authority at a time. A writer must call
    ///   Claim(this) before it may touch GameDifficulty, and must check
    ///   HasAuthority(this) on every write. Claiming automatically revokes everyone
    ///   else (they get OnAuthorityRevoked() and are expected to go quiet, not to
    ///   fight back).
    ///
    /// This is deliberately a plain static class, not a MonoBehaviour: it holds no
    /// state the scene cares about, and it must exist before any writer's Awake().
    /// The invariant "delete the DDA folder and the game still compiles" is
    /// preserved — nothing game-side references this.
    /// </summary>
    public static class DifficultyAuthority
    {
        static readonly List<IDifficultyWriter> _registered = new List<IDifficultyWriter>();

        /// <summary>The writer currently allowed to write GameDifficulty (null = nobody).</summary>
        public static IDifficultyWriter Current { get; private set; }

        /// <summary>Fired whenever authority changes hands (argument may be null).</summary>
        public static event Action<IDifficultyWriter> OnAuthorityChanged;

        /// <summary>Every writer that has registered itself, for UI listings.</summary>
        public static IReadOnlyList<IDifficultyWriter> Registered => _registered;

        // Domain reload can be disabled in the editor, which would leak statics
        // between play sessions. Reset explicitly on every play.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _registered.Clear();
            Current = null;
            OnAuthorityChanged = null;
        }

        public static void Register(IDifficultyWriter w)
        {
            if (w == null || _registered.Contains(w)) return;
            _registered.Add(w);
        }

        public static void Unregister(IDifficultyWriter w)
        {
            if (w == null) return;
            _registered.Remove(w);
            if (ReferenceEquals(Current, w))
            {
                Current = null;
                OnAuthorityChanged?.Invoke(null);
            }
        }

        /// <summary>True if this writer is allowed to write GameDifficulty right now.</summary>
        public static bool HasAuthority(IDifficultyWriter w) => ReferenceEquals(Current, w);

        /// <summary>
        /// Take exclusive control. Everyone else is revoked first, so there is never
        /// a frame with two owners.
        /// </summary>
        public static void Claim(IDifficultyWriter w)
        {
            if (w == null || ReferenceEquals(Current, w)) return;

            Register(w);
            Current = w;

            // Revoke everyone else. Iterate over a copy: a revoked writer is allowed
            // to unregister itself from inside its own callback.
            var snapshot = _registered.ToArray();
            foreach (var other in snapshot)
            {
                if (ReferenceEquals(other, w)) continue;
                try { other.OnAuthorityRevoked(); }
                catch (Exception e) { Debug.LogException(e); }
            }

            try { w.OnAuthorityGranted(); }
            catch (Exception e) { Debug.LogException(e); }

            Debug.Log($"[DifficultyAuthority] '{w.AuthorityName}' now controls GameDifficulty.");
            OnAuthorityChanged?.Invoke(w);
        }

        /// <summary>Give up control voluntarily. No-op if this writer doesn't hold it.</summary>
        public static void Release(IDifficultyWriter w)
        {
            if (w == null || !ReferenceEquals(Current, w)) return;
            Current = null;
            try { w.OnAuthorityRevoked(); }
            catch (Exception e) { Debug.LogException(e); }

            Debug.Log($"[DifficultyAuthority] '{w.AuthorityName}' released control.");
            OnAuthorityChanged?.Invoke(null);
        }

        public static string CurrentName => Current != null ? Current.AuthorityName : "(none)";

        /// <summary>
        /// The current difficulty command d of whichever writer holds authority, in the
        /// shared step-count units, or NaN if nobody does. This is the controller-agnostic
        /// source the tuning HUD and SessionRecorder read so difficulty can be plotted /
        /// logged identically under PI, rule-based, or manual-preset control.
        /// </summary>
        public static float CurrentDifficulty => Current != null ? Current.Difficulty : float.NaN;
    }

    /// <summary>
    /// Implemented by any component that writes GameDifficulty.Instance.
    /// </summary>
    public interface IDifficultyWriter
    {
        /// <summary>Human-readable name shown in logs and the tuning HUD.</summary>
        string AuthorityName { get; }

        /// <summary>
        /// The writer's current difficulty command d, in the shared step-count units
        /// (v = speedStep·d). Exposed on the interface so the HUD and recorder can read
        /// difficulty without knowing which concrete controller is active. Closed-loop
        /// controllers return their live d; the manual preset switcher returns the d
        /// implied by its active preset.
        /// </summary>
        float Difficulty { get; }

        /// <summary>Called when this writer has just been given exclusive control.</summary>
        void OnAuthorityGranted();

        /// <summary>
        /// Called when another writer has taken control. The implementer MUST stop
        /// writing GameDifficulty; it must not attempt to reclaim authority here.
        /// </summary>
        void OnAuthorityRevoked();
    }
}
