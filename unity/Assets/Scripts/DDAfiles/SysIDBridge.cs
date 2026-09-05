using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DDA
{
    /// <summary>
    /// Flat, JsonUtility-friendly mirror of what sysid_fit.py writes out.
    /// Field names MUST match the JSON keys exactly (case-sensitive) — kept
    /// flat (no nested matrix/dict) because JsonUtility supports neither.
    /// </summary>
    [Serializable]
    public class SysIDResult
    {
        public float a0, a1, a2, sigma_t;
        public float v_star, fs_star;
        // Jacobian K (2x2), row-major: [[dMt/dv, dMt/dfs], [derate/dv, derate/dfs]]
        public float k11, k12, k21, k22;
        public float M_t_star;     // predicted M_t at u*, seconds
        public float erate_star;   // predicted errors/min at u*
        public int n_samples_Mt;
        public int n_samples_erate;
        public string timestamp;
        public string csv_source;
    }

    /// <summary>
    /// Runs sysid_fit.py as an external process and reads back its JSON result.
    /// Polls HasExited once per frame (never blocks the Unity main thread) and
    /// reads stdout/stderr asynchronously via BeginOutputReadLine/BeginErrorReadLine
    /// to avoid the classic Process deadlock where the child fills the OS pipe
    /// buffer before exiting and nobody is draining it.
    /// </summary>
    public class SysIDBridge : MonoBehaviour
    {
        [Header("Python")]
        public string pythonExecutable = "python";
        [Tooltip("Path to sysid_fit.py, absolute or relative to the project's working directory.")]
        public string pythonScriptPath = "python/sysid_fit.py";
        public float maxWaitSeconds = 120f;

        public IEnumerator RunPythonFit(
            string csvPath,
            string outputJsonPath,
            float vStar,
            float fsStar,
            float lEff,
            Action<SysIDResult> onComplete,
            Action<string> onError)
        {
            if (!File.Exists(pythonScriptPath))
            {
                onError?.Invoke($"sysid_fit.py not found at: {pythonScriptPath}");
                yield break;
            }

            string args = string.Join(" ", new[]
            {
                $"\"{pythonScriptPath}\"",
                "--csv", $"\"{csvPath}\"",
                "--out", $"\"{outputJsonPath}\"",
                "--v-star", vStar.ToString(CultureInfo.InvariantCulture),
                "--fs-star", fsStar.ToString(CultureInfo.InvariantCulture),
                "--l-eff", lEff.ToString(CultureInfo.InvariantCulture),
            });

            var psi = new ProcessStartInfo(pythonExecutable, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            Process proc;
            try
            {
                proc = new Process { StartInfo = psi, EnableRaisingEvents = false };
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived  += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                onError?.Invoke($"Failed to start python process ('{pythonExecutable}'): {e.Message}\n" +
                                 "Check that Python is installed and on PATH, or set pythonExecutable to a full path.");
                yield break;
            }

            float waited = 0f;
            while (!proc.HasExited)
            {
                waited += Time.unscaledDeltaTime;
                if (waited > maxWaitSeconds)
                {
                    try { proc.Kill(); } catch { /* already gone */ }
                    onError?.Invoke($"sysid_fit.py timed out after {maxWaitSeconds:F0}s and was killed.\n" +
                                     $"Output so far:\n{stdout}");
                    yield break;
                }
                yield return null;
            }
            proc.WaitForExit(); // flush any pending async output-read events

            Debug.Log("[SysIDBridge] sysid_fit.py stdout:\n" + stdout);
            if (stderr.Length > 0) Debug.LogWarning("[SysIDBridge] sysid_fit.py stderr:\n" + stderr);

            if (proc.ExitCode != 0)
            {
                onError?.Invoke($"sysid_fit.py exited with code {proc.ExitCode}.\n{stderr}");
                yield break;
            }

            if (!File.Exists(outputJsonPath))
            {
                onError?.Invoke("sysid_fit.py exited cleanly but did not produce the expected output JSON:\n" +
                                 outputJsonPath);
                yield break;
            }

            SysIDResult result;
            try
            {
                string json = File.ReadAllText(outputJsonPath);
                result = JsonUtility.FromJson<SysIDResult>(json);
            }
            catch (Exception e)
            {
                onError?.Invoke($"Failed to parse sysid_fit.py output JSON: {e.Message}");
                yield break;
            }

            onComplete?.Invoke(result);
        }
    }
}
