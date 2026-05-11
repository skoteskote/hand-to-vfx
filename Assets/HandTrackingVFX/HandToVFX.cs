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
        [Tooltip("If set, this Visual Effect asset is instantiated 21 times, one as a child of each joint. Each instance gets its own VisualEffect component, so trail/particle outputs follow the joint as it moves.")]
        [SerializeField] VisualEffectAsset _perJointVFX;
        [Tooltip("If true, joints whose pipeline position has not yet been initialised are hidden.")]
        [SerializeField] bool _hideUntilDetected = true;

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

                if (_perJointVFX != null)
                {
                    var vfxChild = new GameObject("VFX");
                    vfxChild.transform.SetParent(go.transform, false);
                    var vfx = vfxChild.AddComponent<VisualEffect>();
                    vfx.visualEffectAsset = _perJointVFX;
                }
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
    }
}
