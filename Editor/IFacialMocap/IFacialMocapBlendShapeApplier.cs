using UnityEngine;
using System;
using System.Collections.Generic;
using System.Globalization;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace JayT.Facetracking.Editor.IFacialMocap
{
    /// <summary>
    /// iFacialMocapから受信したデータをアバターのBlendShapeに適用します。
    /// アバターごとに個別のマッピング上書きとリミッターが設定できます。
    /// </summary>
    public class IFacialMocapBlendShapeApplier : MonoBehaviour
    {
        /// <summary>iFacialMocapパラメーター名 → アバター側BlendShape名 の1エントリ</summary>
        [Serializable]
        public struct MappingEntry
        {
            [Tooltip("iFacialMocapが送るパラメーター名 (ドロップダウンで選択)")]
            public string ifmName;
            [Tooltip("対応させるアバター側のBlendShape名")]
            public string blendShapeName;
        }

        /// <summary>
        /// iFacialMocapパラメーターの値を [min, max] でクランプするリミッター。
        /// weightTrackingScale 適用後の値に対してクランプします。
        /// </summary>
        [Serializable]
        public struct LimitEntry
        {
            [Tooltip("リミッターを適用するiFacialMocapパラメーター名 (ドロップダウンで選択)")]
            public string ifmName;
            [Tooltip("クランプ最小値 (デフォルト: 0)")]
            public float min;
            [Tooltip("クランプ最大値 (デフォルト: 100)")]
            public float max;
            // Inspector上での初期化済みフラグ (デフォルト値の自動設定に使用)
            [HideInInspector] public bool initialized;
        }

        /// <summary>グループごとの有効フラグとスムージング強度のセット</summary>
        [Serializable]
        public struct GroupSetting
        {
            [Tooltip("このグループのトラッキングを有効にする")]
            public bool enabled;
            [Tooltip("スムージング強度。0=即時追従、1に近いほど遅く滑らか")]
            [Range(0f, 1f)]
            public float smoothing;
        }

        /// <summary>アバター1体分の設定</summary>
        [Serializable]
        public class AvatarTarget
        {
            [Tooltip("BlendShapeを適用するSkinnedMeshRenderer")]
            public SkinnedMeshRenderer renderer;

            [Tooltip("このアバター固有のマッピング上書き。固定テーブルより優先されます。\n" +
                     "左: iFacialMocap名 / 右: アバター側BlendShape名")]
            public MappingEntry[] mappingOverrides;

            [Tooltip("このアバター固有のリミッター。再生中も即時反映されます。\n" +
                     "値は weightTrackingScale 適用後にクランプされます。\n" +
                     "左: iFacialMocap名 / 中: 最小値 / 右: 最大値")]
            public LimitEntry[] limiters;

            [Header("Eye Bone")]
            [Tooltip("このアバターの眼球ボーン回転を有効にする")]
            public bool enableEyeBone = true;
            [Tooltip("左眼球ボーン。eyeLookXxx_L の値で localRotation を制御します。")]
            public Transform leftEyeBone;
            [Tooltip("右眼球ボーン。eyeLookXxx_R の値で localRotation を制御します。")]
            public Transform rightEyeBone;
            [Tooltip("眼球の上下回転スケール (度単位)。負値で上下を反転。")]
            public float eyeVerticalScale = 15f;
            [Tooltip("眼球の左右回転スケール (度単位)。負値で左右を反転。")]
            public float eyeHorizontalScale = 15f;
        }

        [Header("Source")]
        [Tooltip("データを受け取るIFacialMocapReceiverコンポーネント")]
        public IFacialMocapReceiver receiver;

        [Header("Targets")]
        [Tooltip("アバターごとの設定 (複数アバターに同時適用可)")]
        public AvatarTarget[] avatarTargets;

        [Header("TrackingSetting")]
        [Tooltip("トラッキング生値 (0–100) に最前段で掛ける倍率。\n" +
                 "例: 0.5 で動きを抑制、1.5 で大げさに。リミッターはこの後に適用されます。")]
        [Range(0f, 3f)]
        public float weightTrackingScale = 1f;

        [Header("Group Filters")]
        [Tooltip("目のBlendShapeトラッキング (eye*)")]
        public GroupSetting eye     = new GroupSetting { enabled = true };
        [Tooltip("眉のBlendShapeトラッキング (brow*)")]
        public GroupSetting brow    = new GroupSetting { enabled = true };
        [Tooltip("鼻のBlendShapeトラッキング (nose*)")]
        public GroupSetting nose    = new GroupSetting { enabled = true };
        [Tooltip("口のBlendShapeトラッキング (mouth*)")]
        public GroupSetting mouth   = new GroupSetting { enabled = true };
        [Tooltip("顎・舌・頬のBlendShapeトラッキング (jaw*, cheek*, tongueOut)")]
        public GroupSetting jaw     = new GroupSetting { enabled = true };
        [Tooltip("眼球ボーン回転 (全アバター共通)")]
        public GroupSetting eyeBone = new GroupSetting { enabled = true };

        // iFacialMocap省略名 (_L/_R) → ARKit標準名 (Left/Right) の固定マッピング
        private static readonly Dictionary<string, string> NameMapping = new Dictionary<string, string>
        {
            { "eyeBlink_L",        "eyeBlinkLeft" },
            { "eyeBlink_R",        "eyeBlinkRight" },
            { "eyeWide_L",         "eyeWideLeft" },
            { "eyeWide_R",         "eyeWideRight" },
            { "eyeSquint_L",       "eyeSquintLeft" },
            { "eyeSquint_R",       "eyeSquintRight" },
            { "eyeLookUp_L",       "eyeLookUpLeft" },
            { "eyeLookUp_R",       "eyeLookUpRight" },
            { "eyeLookDown_L",     "eyeLookDownLeft" },
            { "eyeLookDown_R",     "eyeLookDownRight" },
            { "eyeLookIn_L",       "eyeLookInLeft" },
            { "eyeLookIn_R",       "eyeLookInRight" },
            { "eyeLookOut_L",      "eyeLookOutLeft" },
            { "eyeLookOut_R",      "eyeLookOutRight" },
            { "browDown_L",        "browDownLeft" },
            { "browDown_R",        "browDownRight" },
            { "browOuterUp_L",     "browOuterUpLeft" },
            { "browOuterUp_R",     "browOuterUpRight" },
            { "cheekSquint_L",     "cheekSquintLeft" },
            { "cheekSquint_R",     "cheekSquintRight" },
            { "noseSneer_L",       "noseSneerLeft" },
            { "noseSneer_R",       "noseSneerRight" },
            { "mouthSmile_L",      "mouthSmileLeft" },
            { "mouthSmile_R",      "mouthSmileRight" },
            { "mouthFrown_L",      "mouthFrownLeft" },
            { "mouthFrown_R",      "mouthFrownRight" },
            { "mouthDimple_L",     "mouthDimpleLeft" },
            { "mouthDimple_R",     "mouthDimpleRight" },
            { "mouthUpperUp_L",    "mouthUpperUpLeft" },
            { "mouthUpperUp_R",    "mouthUpperUpRight" },
            { "mouthLowerDown_L",  "mouthLowerDownLeft" },
            { "mouthLowerDown_R",  "mouthLowerDownRight" },
            { "mouthPress_L",      "mouthPressLeft" },
            { "mouthPress_R",      "mouthPressRight" },
            { "mouthStretch_L",    "mouthStretchLeft" },
            { "mouthStretch_R",    "mouthStretchRight" },
        };

        // アバターごとのランタイムキャッシュ (BlendShapeインデックスとOverrideのみ。Limiterは毎回 avatarTargets から直接読む)
        private class AvatarCache
        {
            public int             targetIdx;       // avatarTargets 配列内のインデックス
            public SkinnedMeshRenderer smr;
            public Dictionary<string, int>    blendShapeIndex;
            public Dictionary<string, string> overrideMap;

            // 眼球ボーン
            public Transform leftEyeBone;
            public Transform rightEyeBone;

            // 眼球の視線アキュムレーター (HandleData ごとにリセット、weightTrackingScale 適用済み) = 目標値
            public float eyeUpL, eyeDownL, eyeInL, eyeOutL;
            public float eyeUpR, eyeDownR, eyeInR, eyeOutR;

            // スムージング: BlendShape ごとの目標値・グループスムージング値・スムージング済み値
            public Dictionary<string, float> targetValues   = new Dictionary<string, float>(StringComparer.Ordinal);
            public Dictionary<string, float> targetSmoothing = new Dictionary<string, float>(StringComparer.Ordinal);
            public Dictionary<string, float> smoothedValues  = new Dictionary<string, float>(StringComparer.Ordinal);

            // 眼球ボーンのスムージング済み値
            public float smoothedEyeUpL, smoothedEyeDownL, smoothedEyeInL, smoothedEyeOutL;
            public float smoothedEyeUpR, smoothedEyeDownR, smoothedEyeInR, smoothedEyeOutR;
        }

        private List<AvatarCache> avatarCaches;

        void Update()
        {
            if (avatarCaches == null || avatarCaches.Count == 0) return;

            float dt60 = Time.deltaTime * 60f;

            foreach (var cache in avatarCaches)
            {
                // BlendShape スムージング適用 (BlendShape ごとにグループのスムージング値を使用)
                foreach (var kv in cache.targetValues)
                {
                    if (!cache.blendShapeIndex.TryGetValue(kv.Key, out int idx)) continue;

                    cache.targetSmoothing.TryGetValue(kv.Key, out float s);
                    float lerpFactor = s < 0.0001f ? 1f : 1f - Mathf.Pow(s, dt60);

                    if (!cache.smoothedValues.TryGetValue(kv.Key, out float cur))
                        cur = kv.Value; // 初回は即時セット (スナップを防ぐ)

                    float next = Mathf.Lerp(cur, kv.Value, lerpFactor);
                    cache.smoothedValues[kv.Key] = next;
                    cache.smr.SetBlendShapeWeight(idx, next);
                }

                // 眼球ボーン スムージング & 適用
                ApplyEyeBonesSmoothed(cache, dt60);
            }
        }

        void OnEnable()
        {
            BuildCaches();

            if (receiver != null)
                receiver.OnDataReceived += HandleData;
            else
                Debug.LogWarning("[IFacialMocapBlendShapeApplier] receiver が設定されていません。", this);
        }

        void OnDisable()
        {
            if (receiver != null)
                receiver.OnDataReceived -= HandleData;
        }

        private void BuildCaches()
        {
            avatarCaches = new List<AvatarCache>();

            if (avatarTargets == null) return;

            for (int i = 0; i < avatarTargets.Length; i++)
            {
                var target = avatarTargets[i];
                if (target == null || target.renderer == null || target.renderer.sharedMesh == null) continue;

                var cache = new AvatarCache
                {
                    targetIdx       = i,
                    smr             = target.renderer,
                    blendShapeIndex = new Dictionary<string, int>(StringComparer.Ordinal),
                    overrideMap     = new Dictionary<string, string>(StringComparer.Ordinal),
                };

                var mesh = target.renderer.sharedMesh;
                for (int b = 0; b < mesh.blendShapeCount; b++)
                    cache.blendShapeIndex[mesh.GetBlendShapeName(b)] = b;

                if (target.mappingOverrides != null)
                {
                    foreach (var entry in target.mappingOverrides)
                    {
                        if (!string.IsNullOrEmpty(entry.ifmName) && !string.IsNullOrEmpty(entry.blendShapeName))
                            cache.overrideMap[entry.ifmName] = entry.blendShapeName;
                    }
                }

                cache.leftEyeBone  = target.leftEyeBone;
                cache.rightEyeBone = target.rightEyeBone;

                avatarCaches.Add(cache);
            }

            Debug.Log($"[IFacialMocapBlendShapeApplier] {avatarCaches.Count} アバター分のキャッシュ構築完了", this);
        }

        // メインスレッドから呼ばれる (IFacialMocapReceiver.OnDataReceived)
        private void HandleData(string data)
        {
            if (string.IsNullOrEmpty(data)) return;

            // 眼球アキュムレーターを毎フレームリセット
            foreach (var cache in avatarCaches)
            {
                cache.eyeUpL = cache.eyeDownL = cache.eyeInL = cache.eyeOutL = 0f;
                cache.eyeUpR = cache.eyeDownR = cache.eyeInR = cache.eyeOutR = 0f;
            }

            var tokens = data.Split('|');
            foreach (var token in tokens)
            {
                if (token.IndexOf('#') >= 0) continue;

                int sep = token.LastIndexOf('-');
                if (sep <= 0) continue;

                string ifmName  = token.Substring(0, sep);
                string valueStr = token.Substring(sep + 1);

                if (!float.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    continue;

                // グループフィルター (再生中も即時反映)
                if (!IsGroupEnabled(ifmName)) continue;

                // 最前段: トラッキング生値に weightTrackingScale を乗算
                val *= weightTrackingScale;

                foreach (var cache in avatarCaches)
                {
                    ApplyToAvatar(cache, ifmName, val);
                    UpdateEyeLookAccum(cache, ifmName, val);
                }
            }

        }

        // パラメーター名のプレフィックスでグループを判定し、そのグループが有効かを返す
        private bool IsGroupEnabled(string ifmName)
        {
            if (ifmName.StartsWith("eye"))   return eye.enabled;
            if (ifmName.StartsWith("brow"))  return brow.enabled;
            if (ifmName.StartsWith("nose"))  return nose.enabled;
            if (ifmName.StartsWith("mouth")) return mouth.enabled;
            if (ifmName.StartsWith("jaw") ||
                ifmName.StartsWith("cheek") ||
                ifmName == "tongueOut")      return jaw.enabled;
            return true; // 未分類は常に通す
        }

        // パラメーター名のプレフィックスでグループのスムージング値 (0–1) を返す
        private float GetGroupSmoothing(string ifmName)
        {
            if (ifmName.StartsWith("eye"))   return eye.smoothing;
            if (ifmName.StartsWith("brow"))  return brow.smoothing;
            if (ifmName.StartsWith("nose"))  return nose.smoothing;
            if (ifmName.StartsWith("mouth")) return mouth.smoothing;
            if (ifmName.StartsWith("jaw") ||
                ifmName.StartsWith("cheek") ||
                ifmName == "tongueOut")      return jaw.smoothing;
            return 0f;
        }

        private void ApplyToAvatar(AvatarCache cache, string ifmName, float val)
        {
            float groupSmoothing = GetGroupSmoothing(ifmName);

            // リミッターを avatarTargets から直接読む (再生中の変更が即時反映される)
            var limiters = avatarTargets[cache.targetIdx].limiters;
            if (limiters != null)
            {
                for (int i = 0; i < limiters.Length; i++)
                {
                    if (limiters[i].ifmName == ifmName)
                    {
                        val = Mathf.Clamp(val, limiters[i].min, limiters[i].max);
                        break;
                    }
                }
            }

            // Override マッピング (最優先)
            if (cache.overrideMap.TryGetValue(ifmName, out string overrideName))
            {
                TryApply(cache, overrideName, val, groupSmoothing);
                return;
            }

            // 直接マッチ
            if (TryApply(cache, ifmName, val, groupSmoothing)) return;

            // 固定マッピングテーブル
            if (NameMapping.TryGetValue(ifmName, out string arKitName))
                TryApply(cache, arKitName, val, groupSmoothing);
        }

        private bool TryApply(AvatarCache cache, string blendShapeName, float val, float groupSmoothing)
        {
            if (!cache.blendShapeIndex.ContainsKey(blendShapeName))
                return false;

            cache.targetValues[blendShapeName]   = Mathf.Clamp(val, 0f, 100f);
            cache.targetSmoothing[blendShapeName] = groupSmoothing;
            return true;
        }

        // eyeLookXxx 系のパラメーターのみアキュムレーターに積む (_L/_R と Left/Right 両形式対応)
        private static void UpdateEyeLookAccum(AvatarCache cache, string ifmName, float val)
        {
            switch (ifmName)
            {
                case "eyeLookUp_L":    case "eyeLookUpLeft":    cache.eyeUpL   = val; break;
                case "eyeLookDown_L":  case "eyeLookDownLeft":  cache.eyeDownL = val; break;
                case "eyeLookIn_L":    case "eyeLookInLeft":    cache.eyeInL   = val; break;
                case "eyeLookOut_L":   case "eyeLookOutLeft":   cache.eyeOutL  = val; break;
                case "eyeLookUp_R":    case "eyeLookUpRight":   cache.eyeUpR   = val; break;
                case "eyeLookDown_R":  case "eyeLookDownRight": cache.eyeDownR = val; break;
                case "eyeLookIn_R":    case "eyeLookInRight":   cache.eyeInR   = val; break;
                case "eyeLookOut_R":   case "eyeLookOutRight":  cache.eyeOutR  = val; break;
            }
        }

        // 眼球ボーンの localRotation を視線アキュムレーターからスムージングして適用する
        // X 軸: 正 = 下、負 = 上
        // Y 軸: 左目は正 = 外向き、右目は正 = 内向き (= 両目とも右を向く場合に正の Y)
        private void ApplyEyeBonesSmoothed(AvatarCache cache, float dt60)
        {
            if (!eyeBone.enabled) return;

            var target = avatarTargets[cache.targetIdx];
            if (!target.enableEyeBone) return;

            if (cache.leftEyeBone == null && cache.rightEyeBone == null) return;

            float s = eyeBone.smoothing;
            float lerpFactor = s < 0.0001f ? 1f : 1f - Mathf.Pow(s, dt60);

            cache.smoothedEyeUpL   = Mathf.Lerp(cache.smoothedEyeUpL,   cache.eyeUpL,   lerpFactor);
            cache.smoothedEyeDownL = Mathf.Lerp(cache.smoothedEyeDownL, cache.eyeDownL, lerpFactor);
            cache.smoothedEyeInL   = Mathf.Lerp(cache.smoothedEyeInL,   cache.eyeInL,   lerpFactor);
            cache.smoothedEyeOutL  = Mathf.Lerp(cache.smoothedEyeOutL,  cache.eyeOutL,  lerpFactor);
            cache.smoothedEyeUpR   = Mathf.Lerp(cache.smoothedEyeUpR,   cache.eyeUpR,   lerpFactor);
            cache.smoothedEyeDownR = Mathf.Lerp(cache.smoothedEyeDownR, cache.eyeDownR, lerpFactor);
            cache.smoothedEyeInR   = Mathf.Lerp(cache.smoothedEyeInR,   cache.eyeInR,   lerpFactor);
            cache.smoothedEyeOutR  = Mathf.Lerp(cache.smoothedEyeOutR,  cache.eyeOutR,  lerpFactor);

            float vScale = target.eyeVerticalScale;
            float hScale = target.eyeHorizontalScale;

            if (cache.leftEyeBone != null)
            {
                float xRot = (cache.smoothedEyeDownL - cache.smoothedEyeUpL)  / 100f * vScale;
                float yRot = (cache.smoothedEyeOutL  - cache.smoothedEyeInL)  / 100f * hScale;
                cache.leftEyeBone.localRotation = Quaternion.Euler(xRot, yRot, 0f);
            }

            if (cache.rightEyeBone != null)
            {
                float xRot = (cache.smoothedEyeDownR - cache.smoothedEyeUpR)  / 100f * vScale;
                float yRot = (cache.smoothedEyeInR   - cache.smoothedEyeOutR) / 100f * hScale;
                cache.rightEyeBone.localRotation = Quaternion.Euler(xRot, yRot, 0f);
            }
        }
    }

#if UNITY_EDITOR
    // iFacialMocapが送りうる全パラメーター名 (PropertyDrawer 間で共有)
    internal static class IFacialMocapIfmNames
    {
        internal static readonly string[] All = new string[]
        {
            // iFacialMocap省略名 (_L/_R形式)
            "eyeBlink_L",       "eyeBlink_R",
            "eyeWide_L",        "eyeWide_R",
            "eyeSquint_L",      "eyeSquint_R",
            "eyeLookUp_L",      "eyeLookUp_R",
            "eyeLookDown_L",    "eyeLookDown_R",
            "eyeLookIn_L",      "eyeLookIn_R",
            "eyeLookOut_L",     "eyeLookOut_R",
            "browDown_L",       "browDown_R",
            "browOuterUp_L",    "browOuterUp_R",
            "cheekSquint_L",    "cheekSquint_R",
            "noseSneer_L",      "noseSneer_R",
            "mouthSmile_L",     "mouthSmile_R",
            "mouthFrown_L",     "mouthFrown_R",
            "mouthDimple_L",    "mouthDimple_R",
            "mouthUpperUp_L",   "mouthUpperUp_R",
            "mouthLowerDown_L", "mouthLowerDown_R",
            "mouthPress_L",     "mouthPress_R",
            "mouthStretch_L",   "mouthStretch_R",
            // ARKit標準名 (対称系)
            "browInnerUp",
            "cheekPuff",
            "jawOpen", "jawForward", "jawLeft", "jawRight",
            "mouthFunnel", "mouthPucker", "mouthLeft", "mouthRight",
            "mouthRollUpper", "mouthRollLower",
            "mouthShrugUpper", "mouthShrugLower",
            "mouthClose",
            "tongueOut",
            // ARKit標準名 (Left/Right形式)
            "eyeBlinkLeft",       "eyeBlinkRight",
            "eyeWideLeft",        "eyeWideRight",
            "eyeSquintLeft",      "eyeSquintRight",
            "eyeLookUpLeft",      "eyeLookUpRight",
            "eyeLookDownLeft",    "eyeLookDownRight",
            "eyeLookInLeft",      "eyeLookInRight",
            "eyeLookOutLeft",     "eyeLookOutRight",
            "browDownLeft",       "browDownRight",
            "browOuterUpLeft",    "browOuterUpRight",
            "cheekSquintLeft",    "cheekSquintRight",
            "noseSneerLeft",      "noseSneerRight",
            "mouthSmileLeft",     "mouthSmileRight",
            "mouthFrownLeft",     "mouthFrownRight",
            "mouthDimpleLeft",    "mouthDimpleRight",
            "mouthUpperUpLeft",   "mouthUpperUpRight",
            "mouthLowerDownLeft", "mouthLowerDownRight",
            "mouthPressLeft",     "mouthPressRight",
            "mouthStretchLeft",   "mouthStretchRight",
        };

        internal static void DrawIfmNamePopup(Rect rect, SerializedProperty ifmProp)
        {
            string current    = ifmProp.stringValue ?? "";
            int    currentIdx = Array.IndexOf(All, current);

            string[] displayNames;
            int      displayIdx;
            if (currentIdx >= 0)
            {
                displayNames = All;
                displayIdx   = currentIdx;
            }
            else
            {
                displayNames    = new string[All.Length + 1];
                displayNames[0] = string.IsNullOrEmpty(current) ? "(選択してください)" : $"(カスタム: {current})";
                Array.Copy(All, 0, displayNames, 1, All.Length);
                displayIdx = 0;
            }

            int selected = EditorGUI.Popup(rect, displayIdx, displayNames);

            if (currentIdx >= 0)
                ifmProp.stringValue = All[selected];
            else if (selected > 0)
                ifmProp.stringValue = All[selected - 1];
        }
    }

    [CustomPropertyDrawer(typeof(IFacialMocapBlendShapeApplier.MappingEntry))]
    public class MappingEntryDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var ifmProp = property.FindPropertyRelative("ifmName");
            var bsProp  = property.FindPropertyRelative("blendShapeName");

            float half    = (position.width - 4f) * 0.5f;
            var leftRect  = new Rect(position.x,             position.y, half, position.height);
            var rightRect = new Rect(position.x + half + 4f, position.y, half, position.height);

            IFacialMocapIfmNames.DrawIfmNamePopup(leftRect, ifmProp);
            bsProp.stringValue = EditorGUI.TextField(rightRect, bsProp.stringValue ?? "");

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUIUtility.singleLineHeight;
    }

    [CustomPropertyDrawer(typeof(IFacialMocapBlendShapeApplier.LimitEntry))]
    public class LimitEntryDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var ifmProp  = property.FindPropertyRelative("ifmName");
            var minProp  = property.FindPropertyRelative("min");
            var maxProp  = property.FindPropertyRelative("max");
            var initProp = property.FindPropertyRelative("initialized");

            // 新規追加時のデフォルト値設定 (min=0, max=100)
            if (!initProp.boolValue)
            {
                initProp.boolValue  = true;
                minProp.floatValue  = 0f;
                maxProp.floatValue  = 100f;
            }

            float w      = position.width;
            float labelW = 26f;
            float numW   = (w * 0.5f - labelW * 2f) * 0.5f;
            float popupW = w * 0.5f - 4f;
            float x      = position.x;
            float y      = position.y;
            float h      = position.height;

            IFacialMocapIfmNames.DrawIfmNamePopup(new Rect(x, y, popupW, h), ifmProp);
            x += popupW + 4f;

            EditorGUI.LabelField(new Rect(x, y, labelW, h), "min");
            x += labelW;
            minProp.floatValue = EditorGUI.FloatField(new Rect(x, y, numW, h), minProp.floatValue);
            x += numW + 2f;

            EditorGUI.LabelField(new Rect(x, y, labelW, h), "max");
            x += labelW;
            maxProp.floatValue = EditorGUI.FloatField(new Rect(x, y, numW, h), maxProp.floatValue);

            // min <= max を保証
            if (minProp.floatValue > maxProp.floatValue)
                maxProp.floatValue = minProp.floatValue;

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUIUtility.singleLineHeight;
    }

    /// <summary>
    /// GroupSetting を1行で表示: [ラベル] [☑ 有効] [====スムージングスライダー====]
    /// </summary>
    [CustomPropertyDrawer(typeof(IFacialMocapBlendShapeApplier.GroupSetting))]
    public class GroupSettingDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var enabledProp = property.FindPropertyRelative("enabled");
            var smoothProp  = property.FindPropertyRelative("smoothing");

            float labelW  = EditorGUIUtility.labelWidth;
            float toggleW = 16f;
            float gap     = 4f;
            float sliderW = position.width - labelW - toggleW - gap;

            var labelRect  = new Rect(position.x,                          position.y, labelW,  position.height);
            var toggleRect = new Rect(position.x + labelW,                 position.y, toggleW, position.height);
            var sliderRect = new Rect(position.x + labelW + toggleW + gap, position.y, sliderW, position.height);

            EditorGUI.LabelField(labelRect, label);
            enabledProp.boolValue = EditorGUI.Toggle(toggleRect, enabledProp.boolValue);

            EditorGUI.BeginDisabledGroup(!enabledProp.boolValue);
            smoothProp.floatValue = EditorGUI.Slider(sliderRect, smoothProp.floatValue, 0f, 1f);
            EditorGUI.EndDisabledGroup();

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUIUtility.singleLineHeight;
    }
#endif
}
