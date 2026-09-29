using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ParallelWorld.FireGraph
{
    /// <summary>
    /// E6: frame-rate independence. Plays the same scenario at several target frame
    /// rates and records the fire state at fixed simulated times. The viewer samples
    /// exact ignition times, so every row for a given probe time must be identical
    /// whatever the frame rate. The old grid CA changed by +50% between 30 and 144 fps
    /// (audit in Documentation/Research/ResearchDirections.md section 2).
    ///
    /// Add next to a FireGraphViewer and press Play; results go to outputCsv.
    /// </summary>
    [RequireComponent(typeof(FireGraphViewer))]
    public class FrameRateProbe : MonoBehaviour
    {
        public int[] frameRates = { 30, 60, 144 };
        [Tooltip("Simulated seconds at which the state is recorded.")]
        public float[] probeTimes = { 1800f, 3600f, 7200f, 14400f };
        [Tooltip("Relative to the project folder unless absolute.")]
        public string outputCsv = "Documentation/Research/results/unity/e6_framerate.csv";

        IEnumerator Start()
        {
            var viewer = GetComponent<FireGraphViewer>();
            while (viewer.IgnitionTimes == null) yield return null;   // viewer solves in its Start
            QualitySettings.vSyncCount = 0;
            var csv = new StringBuilder("target_fps,probe_sim_s,frames,mean_frame_s,unburned,incubating,burning,burnt_out\n");
            foreach (int fps in frameRates)
            {
                Application.targetFrameRate = fps;
                viewer.simTime = 0f;
                foreach (float t in probeTimes)
                {
                    viewer.pauseAtSimTime = t;
                    viewer.playing = true;
                    int frames = 0;
                    float wall = 0f;
                    while (viewer.playing) { yield return null; frames++; wall += Time.unscaledDeltaTime; }
                    int[] c = viewer.StateCounts(viewer.simTime);
                    csv.Append(fps).Append(',').Append(t.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                       .Append(frames).Append(',').Append((frames > 0 ? wall / frames : 0f).ToString("F4", CultureInfo.InvariantCulture))
                       .Append(',').Append(c[0]).Append(',').Append(c[1]).Append(',').Append(c[2]).Append(',').Append(c[3]).Append('\n');
                }
            }
            viewer.pauseAtSimTime = float.PositiveInfinity;
            string path = Path.IsPathRooted(outputCsv) ? outputCsv
                : Path.Combine(Directory.GetParent(Application.dataPath).FullName, outputCsv);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, csv.ToString());
            Debug.Log($"[FireGraph] E6 frame-rate probe written to {path}\n{csv}");
        }
    }
}
