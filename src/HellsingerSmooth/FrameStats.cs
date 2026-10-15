using System;

namespace HellsingerSmooth
{
    /// <summary>
    /// Measures real frame times between per-frame callbacks and periodically logs FPS,
    /// 1% low and the number of hitches, so the effect of the mod can be compared on/off.
    /// </summary>
    internal sealed class FrameStats
    {
        private readonly double[] _frames = new double[20000];
        private int _count;
        private double _last = -1, _windowStart = -1;

        public void OnFrame(double now)
        {
            int interval = Plugin.S.StatsIntervalSeconds.Value;
            if (interval <= 0) return;

            if (_last >= 0)
            {
                double dt = now - _last;
                // Gaps over 1 s are loading screens / alt-tab, not gameplay frames.
                if (dt < 1.0 && _count < _frames.Length) _frames[_count++] = dt;
            }
            _last = now;
            if (_windowStart < 0) _windowStart = now;

            if (now - _windowStart >= interval)
            {
                Report();
                _count = 0;
                _windowStart = now;
            }
        }

        private void Report()
        {
            if (_count < 30) return;
            var sorted = new double[_count];
            Array.Copy(_frames, sorted, _count);
            Array.Sort(sorted);

            double total = 0;
            for (int i = 0; i < _count; i++) total += sorted[i];
            double avgFps = _count / total;
            double median = sorted[_count / 2];

            // 1% low = average FPS of the slowest 1% of frames.
            int slowN = Math.Max(1, _count / 100);
            double slowSum = 0;
            for (int i = _count - slowN; i < _count; i++) slowSum += sorted[i];
            double lowFps = slowN / slowSum;

            // Hitch = frame at least twice the median and longer than 25 ms.
            double hitchAt = Math.Max(median * 2, 0.025);
            int hitches = 0;
            for (int i = 0; i < _count; i++) if (_frames[i] >= hitchAt) hitches++;

            var scale = Plugin.ScaleSummary();
            Plugin.L.LogInfo($"[Stats] avg {avgFps:F0} FPS | 1% low {lowFps:F0} FPS | worst {sorted[_count - 1] * 1000:F0} ms | hitches {hitches} in {_count} frames" + (scale != null ? " | " + scale : ""));
        }
    }
}
