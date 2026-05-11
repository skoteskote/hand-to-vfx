using System.Collections.Generic;
using Klak.TestTools;
using MediaPipe.HandPose;
using UnityEngine;
using UnityEngine.VFX;

namespace HandTrackingVFX
{
    public sealed class HandToVFX : MonoBehaviour
    {
        public const int JointCount = HandPipeline.KeyPointCount;

        [Header("Input")]
        [SerializeField] ImageSource _source;
        [SerializeField] ResourceSet _resources;
        [SerializeField] bool _useAsyncReadback = true;

        [Header("Hand Space")]
        [Tooltip("Joints are spawned as children of this transform. Move/scale/rotate it to place the hand in the world.")]
        [SerializeField] Transform _handRoot;
        [Tooltip("Flip the hand horizontally (useful for front-facing webcams).")]
        [SerializeField] bool _flipX = true;
        [Tooltip("Extra Z amplification beyond MediaPipe's relatively shallow depth.")]
        [SerializeField, Range(0.1f, 10f)] float _depthScale = 1.0f;

        [Header("Joints")]
        [Tooltip("If set, this prefab is instantiated 21 times, one per joint. The script will move each instance to its joint's world position every frame.")]
        [SerializeField] GameObject _jointPrefab;
        [Tooltip("If true, joints whose pipeline position has not yet been initialised are hidden.")]
        [SerializeField] bool _hideUntilDetected = true;

        [Header("Gizmos")]
        [Tooltip("Draw a gizmo per joint in the scene view.")]
        [SerializeField] bool _drawJointGizmos = true;
        [Tooltip("Radius of each joint gizmo, in world units.")]
        [SerializeField, Range(0.0001f, 0.1f)] float _gizmoSize = 0.01f;
        [Tooltip("Show joint index labels next to each gizmo.")]
        [SerializeField] bool _drawJointLabels;
        [Tooltip("Draw lines connecting joints along each finger.")]
        [SerializeField] bool _drawBones = true;

        [Header("VFX target (optional)")]
        [Tooltip("A VisualEffect that will receive the hand data via exposed properties.")]
        [SerializeField] VisualEffect _targetVFX;
        [Tooltip("Name of an exposed Texture2D on the target VFX to receive a 32x1 RGBA32F position map.")]
        [SerializeField] string _positionMapProperty = "PositionMap";
        [Tooltip("Name of an exposed uint/int on the target VFX to receive the joint count (21).")]
        [SerializeField] string _jointCountProperty = "JointCount";

        public Texture PositionMap => _positionMap;
        public Transform[] Joints => _joints;
        public bool IsTracking => _hasReadback;

        public Vector3 GetLocalJointPosition(int index) => _cache[index];
        public Vector3 GetWorldJointPosition(int index)
            => _handRoot != null ? _handRoot.TransformPoint(_cache[index]) : _cache[index];
        public float GetJointSpeed(int index) => _speed[index];

        HandPipeline _pipeline;
        Texture2D _positionMap;
        Transform[] _joints;
        readonly Vector3[] _cache = new Vector3[JointCount];
        readonly Vector3[] _prevWorld = new Vector3[JointCount];
        readonly float[] _speed = new float[JointCount];
        readonly Color[] _pixelBuffer = new Color[32];
        bool _hasReadback;

        void Awake()
        {
            if (_handRoot == null) _handRoot = transform;

            _positionMap = new Texture2D(32, 1, TextureFormat.RGBAFloat, false, true)
            {
                name = "HandPositionMap",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };

            _joints = new Transform[JointCount];
            for (var i = 0; i < JointCount; i++)
            {
                GameObject go;
                if (_jointPrefab != null)
                {
                    go = Instantiate(_jointPrefab, _handRoot);
                }
                else
                {
                    go = new GameObject();
                    go.transform.SetParent(_handRoot, false);
                }
                go.name = $"Joint_{i:00}";
                if (_hideUntilDetected) go.SetActive(false);
                _joints[i] = go.transform;
            }
        }

        void Start()
        {
            _pipeline = new HandPipeline(_resources) { UseAsyncReadback = _useAsyncReadback };
        }

        void OnDestroy()
        {
            _pipeline?.Dispose();
            _pipeline = null;
            if (_positionMap != null) Destroy(_positionMap);
        }

        void LateUpdate()
        {
            if (_pipeline == null || _source == null || _source.Texture == null) return;

            _pipeline.UseAsyncReadback = _useAsyncReadback;
            _pipeline.ProcessImage(_source.Texture);

            for (var i = 0; i < JointCount; i++)
            {
                var p = _pipeline.GetKeyPoint(i);
                if (_flipX) p.x = -p.x;
                p.z *= _depthScale;
                _cache[i] = p;
            }

            // Heuristic: treat the wrist landmark as initialised once it's not at the origin.
            // The pipeline returns (0,0,0) until the first detection completes.
            if (!_hasReadback && _cache[0].sqrMagnitude > 0.0001f)
            {
                _hasReadback = true;
                if (_hideUntilDetected)
                    for (var i = 0; i < JointCount; i++) _joints[i].gameObject.SetActive(true);
            }

            for (var i = 0; i < JointCount; i++)
                _joints[i].localPosition = _cache[i];

            UpdatePositionMap();

            if (_targetVFX != null)
            {
                if (!string.IsNullOrEmpty(_positionMapProperty) && _targetVFX.HasTexture(_positionMapProperty))
                    _targetVFX.SetTexture(_positionMapProperty, _positionMap);
                if (!string.IsNullOrEmpty(_jointCountProperty) && _targetVFX.HasUInt(_jointCountProperty))
                    _targetVFX.SetUInt(_jointCountProperty, (uint)JointCount);
            }
        }

        void UpdatePositionMap()
        {
            // RGBA32F per joint: rgb = world position, a = speed (world units / second, lightly smoothed).
            // Pad to 32 pixels for power-of-two alignment; padded texels carry zero so a stray sample is harmless.
            var dt = Mathf.Max(Time.deltaTime, 1e-4f);
            var alpha = _hasReadback ? Mathf.Clamp01(dt / 0.1f) : 1f; // EMA: ~100ms response
            for (var i = 0; i < 32; i++)
            {
                if (i < JointCount)
                {
                    var w = _handRoot.TransformPoint(_cache[i]);
                    var instSpeed = (w - _prevWorld[i]).magnitude / dt;
                    _speed[i] = Mathf.Lerp(_speed[i], instSpeed, alpha);
                    _prevWorld[i] = w;
                    _pixelBuffer[i] = new Color(w.x, w.y, w.z, _speed[i]);
                }
                else
                {
                    _pixelBuffer[i] = new Color(0, 0, 0, 0);
                }
            }
            _positionMap.SetPixels(_pixelBuffer);
            _positionMap.Apply(false, false);
        }

        // MediaPipe HandPose finger groups: each row is a chain of joint indices.
        static readonly int[][] _fingers =
        {
            new[] { 0, 1, 2, 3, 4 },        // thumb
            new[] { 0, 5, 6, 7, 8 },        // index
            new[] { 0, 9, 10, 11, 12 },     // middle
            new[] { 0, 13, 14, 15, 16 },    // ring
            new[] { 0, 17, 18, 19, 20 },    // pinky
        };

        static readonly Color[] _fingerColors =
        {
            new Color(1f, 0.45f, 0.2f),     // thumb  - orange
            new Color(1f, 0.9f, 0.2f),      // index  - yellow
            new Color(0.4f, 1f, 0.4f),      // middle - green
            new Color(0.3f, 0.7f, 1f),      // ring   - blue
            new Color(0.9f, 0.4f, 1f),      // pinky  - magenta
        };

        void OnDrawGizmos()
        {
            if (!_drawJointGizmos) return;
            if (_joints == null || _joints.Length != JointCount) return;
            if (_hideUntilDetected && !_hasReadback) return;

            for (var f = 0; f < _fingers.Length; f++)
            {
                var chain = _fingers[f];
                Gizmos.color = _fingerColors[f];

                for (var k = 0; k < chain.Length; k++)
                {
                    var idx = chain[k];
                    var t = _joints[idx];
                    if (t == null) continue;

                    Gizmos.DrawSphere(t.position, _gizmoSize);

                    if (_drawBones && k > 0)
                    {
                        var prev = _joints[chain[k - 1]];
                        if (prev != null) Gizmos.DrawLine(prev.position, t.position);
                    }

#if UNITY_EDITOR
                    if (_drawJointLabels)
                        UnityEditor.Handles.Label(t.position + Vector3.up * _gizmoSize * 1.5f, idx.ToString());
#endif
                }
            }

            // Wrist highlight so it's easy to spot in the cluster of base joints.
            var wrist = _joints[0];
            if (wrist != null)
            {
                Gizmos.color = Color.white;
                Gizmos.DrawWireSphere(wrist.position, _gizmoSize * 1.6f);
            }
        }
    }
}
