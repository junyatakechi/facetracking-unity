using UnityEngine;
using UnityEngine.Timeline;
using UnityEngine.Playables;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
#endif

/// <summary>
/// BlendShape録画コンポーネント。
///
/// 【動作モード】
/// Play Mode 推奨。フェイストラッキング（IFacialMocapBlendShapeApplier）が
/// Play Mode でのみ動作するため、録画も Play Mode で行う。
///
/// 【録画フロー】
/// Inspector の REC START ボタン
///   → director.Play() + 録画開始
///   → フェイストラッキングが BlendShape を更新し続ける
///   → STOP & SAVE ボタン または Timeline 終端
///   → .anim ファイル保存
///   → Timeline の Animation Track に手動配置して確認
///
/// 【保存パス】
/// {saveFolder}/{prefix}-{SMR名}-{Timeline名}.anim
/// </summary>
namespace JayT.Facetracking.Editor
{
    [ExecuteAlways]
    public class FaceAnimationRecorder : MonoBehaviour
    {
        [Header("録画対象（必須）")]
        [Tooltip("BlendShapeを持つSkinnedMeshRenderer")]
        public SkinnedMeshRenderer targetRenderer;

        [Header("Timeline連携（必須）")]
        public PlayableDirector director;

        [Header("ファイル命名")]
        [Tooltip("ファイル名Prefix（空白可）例: take01")]
        public string prefix = "";

        [Header("保存先")]
        [Tooltip("保存先フォルダ（Assets/ 以下）")]
        public string saveFolder = "Assets/Recordings";

#if UNITY_EDITOR
        // Inspector で録画状態を表示（読み取り専用）
        [Header("Status (Read Only)")]
        [SerializeField, HideInInspector] private bool isRecording = false;

        private GameObjectRecorder recorder;
        private double lastEditorTime;
        private float recordedDuration;

        // ---- イベント登録管理 ----

        void OnEnable()
        {
            SubscribeDirector(director);
        }

        void OnDisable()
        {
            UnsubscribeDirector(director);
            CancelRecording();
        }

        private void SubscribeDirector(PlayableDirector d)
        {
            if (d == null) return;
            d.played += OnDirectorPlayed;
            d.stopped += OnDirectorStopped;
        }

        private void UnsubscribeDirector(PlayableDirector d)
        {
            if (d == null) return;
            d.played -= OnDirectorPlayed;
            d.stopped -= OnDirectorStopped;
        }

        // ---- 公開 API（Inspector ボタンから呼ばれる）----

        public bool IsRecording => isRecording;

        /// <summary>REC START: director を先頭から再生し録画を開始する</summary>
        public void StartRecording()
        {
            if (!ValidateSetup()) return;
            if (isRecording)
            {
                Debug.LogWarning("[FaceAnimationRecorder] すでに録画中です", this);
                return;
            }

            // director が変更されている場合に備えて再登録
            UnsubscribeDirector(director);
            SubscribeDirector(director);

            director.time = 0;
            director.Play();
            // 以降は OnDirectorPlayed が録画を開始する
        }

        /// <summary>STOP & SAVE: director を停止し録画データを保存する</summary>
        public void StopRecording()
        {
            if (!isRecording)
            {
                Debug.LogWarning("[FaceAnimationRecorder] 録画していません", this);
                return;
            }
            director.Stop();
            // 以降は OnDirectorStopped が保存処理を行う
        }

        // ---- 録画コア ----

        private void OnDirectorPlayed(PlayableDirector pd)
        {
            if (isRecording) return;

            // recorder root = targetRenderer.gameObject
            // → 生成クリップのパスが targetRenderer.gameObject 基準になる
            // → Timeline の Animation Track を targetRenderer.gameObject にバインドして使用
            recorder = new GameObjectRecorder(targetRenderer.gameObject);
            recorder.BindComponentsOfType<SkinnedMeshRenderer>(targetRenderer.gameObject, false);

            isRecording = true;
            recordedDuration = 0f;
            lastEditorTime = EditorApplication.timeSinceStartup;

            // EditorApplication.update = Edit Mode / Play Mode 両方で毎フレーム動く
            EditorApplication.update += EditorUpdate;

            Debug.Log("[FaceAnimationRecorder] 録画開始");
        }

        private void EditorUpdate()
        {
            if (!isRecording || recorder == null)
            {
                EditorApplication.update -= EditorUpdate;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            float dt = (float)(now - lastEditorTime);
            lastEditorTime = now;
            recordedDuration += dt;

            recorder.TakeSnapshot(dt);
        }

        private void OnDirectorStopped(PlayableDirector pd)
        {
            if (!isRecording) return;

            EditorApplication.update -= EditorUpdate;
            isRecording = false;

            SaveClip();
        }

        /// <summary>録画を保存せずにキャンセルする（OnDisable 時など）</summary>
        private void CancelRecording()
        {
            if (!isRecording) return;
            EditorApplication.update -= EditorUpdate;
            isRecording = false;
            recorder = null;
            Debug.LogWarning("[FaceAnimationRecorder] 録画がキャンセルされました（保存されていません）");
        }

        // ---- 保存 ----

        private void SaveClip()
        {
            if (recorder == null)
            {
                Debug.LogError("[FaceAnimationRecorder] recorder が null です");
                return;
            }

            if (!System.IO.Directory.Exists(saveFolder))
                System.IO.Directory.CreateDirectory(saveFolder);

            float fps = 30f;
            string timelineName = "Timeline";
            if (director.playableAsset is TimelineAsset ta)
            {
                fps = (float)ta.editorSettings.frameRate;
                timelineName = ta.name;
            }

            string objectName = targetRenderer.gameObject.name;
            string prefixPart = string.IsNullOrEmpty(prefix) ? "" : $"{prefix}-";
            string fileName = $"{prefixPart}{objectName}-{timelineName}.anim";
            string savePath = $"{saveFolder}/{fileName}";

            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(savePath);
            if (clip == null)
            {
                clip = new AnimationClip();
                AssetDatabase.CreateAsset(clip, savePath);
            }

            // fps を指定してキーフレームを正しい間隔で保存
            recorder.SaveToClip(clip, fps);
            AssetDatabase.SaveAssets();

            recorder = null;
            Debug.Log($"[FaceAnimationRecorder] 保存完了: {savePath}  ({recordedDuration:F1}秒 @ {fps}fps)");
        }

        // ---- バリデーション ----

        private bool ValidateSetup()
        {
            if (targetRenderer == null)
            {
                Debug.LogError("[FaceAnimationRecorder] SkinnedMeshRenderer が未設定です", this);
                return false;
            }
            if (director == null)
            {
                Debug.LogError("[FaceAnimationRecorder] PlayableDirector が未設定です", this);
                return false;
            }
            if (string.IsNullOrEmpty(saveFolder))
            {
                Debug.LogError("[FaceAnimationRecorder] 保存先フォルダが未設定です", this);
                return false;
            }
            return true;
        }
#endif
    }

    // ============================================================
    // Custom Inspector
    // ============================================================
#if UNITY_EDITOR
    [CustomEditor(typeof(FaceAnimationRecorder))]
    public class FaceAnimationRecorderEditor : UnityEditor.Editor
    {
        void OnEnable()
        {
            // 録画状態の変化を Inspector に即時反映するため毎フレーム再描画
            EditorApplication.update += Repaint;
        }

        void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("録画コントロール", EditorStyles.boldLabel);

            var rec = (FaceAnimationRecorder)target;
            bool isRec = rec.IsRecording;

            var prevBg = GUI.backgroundColor;

            if (!isRec)
            {
                GUI.backgroundColor = new Color(0.85f, 0.25f, 0.25f);
                if (GUILayout.Button("●  REC START", GUILayout.Height(40)))
                    rec.StartRecording();
            }
            else
            {
                GUI.backgroundColor = new Color(1f, 0.5f, 0.1f);
                if (GUILayout.Button("■  STOP & SAVE", GUILayout.Height(40)))
                    rec.StopRecording();

                EditorGUILayout.Space(2);
                EditorGUILayout.HelpBox("録画中... フェイスを動かしてください", MessageType.Warning);
            }

            GUI.backgroundColor = prevBg;

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "【使い方】\n" +
                "1. Unity を Play Mode にしてフェイストラッキングを起動\n" +
                "2. REC START → Timeline が先頭から再生・録画開始\n" +
                "3. Timeline が終端に達するか STOP & SAVE を押すと\n" +
                "   saveFolder に .anim が保存される\n\n" +
                "【Timeline への配置】\n" +
                "保存した .anim → Timeline の Animation Track にドラッグ\n" +
                "Animation Track のバインド先 = targetRenderer の GameObject",
                MessageType.Info);
        }
    }
#endif
}
