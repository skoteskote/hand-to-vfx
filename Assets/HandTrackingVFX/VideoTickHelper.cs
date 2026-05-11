using UnityEngine;
using UnityEngine.Video;

namespace HandTrackingVFX
{
    /// <summary>
    /// Some Unity environments (notably unfocused Editor + Game-view not visible) refuse to advance
    /// VideoPlayer.Play() automatically. This helper steps the player by hand each frame so the
    /// hand-tracking pipeline always receives fresh frames during a demo run.
    /// </summary>
    public sealed class VideoTickHelper : MonoBehaviour
    {
        [SerializeField] bool _loop = true;

        VideoPlayer _vp;
        double _accum;

        void Update()
        {
            if (_vp == null) _vp = GetComponent<VideoPlayer>();
            if (_vp == null || !_vp.isPrepared || _vp.clip == null) return;
            if (_vp.isPlaying) return;

            _accum += Time.deltaTime;
            var step = 1.0 / _vp.frameRate;
            while (_accum >= step)
            {
                _accum -= step;
                if (_loop && _vp.frame >= (long)_vp.frameCount - 2) _vp.frame = 0;
                _vp.StepForward();
            }
        }
    }
}
