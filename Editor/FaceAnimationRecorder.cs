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
/// 【保存ファイル名】
/// {prefix}-{SMR名}-{Timeline名}-s{開始フレーム:D4}-e{終了フレーム:D4}-take{N}.anim
/// 例: avatar-Face-MyTimeline-s0030-e0090-take1.anim
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
        [Tooltip("ファイル名Prefix（空白可）例: avatar-name")]
        public string prefix = "";

        [Header("保存先")]
        [Tooltip("保存先フォルダ（Assets/ 以下）")]
        public string saveFolder = "Assets/Recordings";

#if UNITY_EDITOR
        // 録画開始フレーム（Inspector の録画コントロール欄に表示）
        [SerializeField, HideInInspector] private int startFrame = 0;

        // 録画状態（Custom Editor で参照）
        [SerializeField, HideInInspector] private bool isRecording = false;

        // テイク番号（EditorPrefs に保存 → Play Mode 終了でリセットされない）
        private string TakeKey => $"JayT.FaceAnimationRecorder.NextTake.{Application.dataPath}";
        public int NextTake
        {
            get => EditorPrefs.GetInt(TakeKey, 1);
            set => EditorPrefs.SetInt(TakeKey, Mathf.Max(1, value));
        }

        // REC START ボタンで明示的に録画を要求したときだけ true になるフラグ
        private bool recordingRequested = false;

        private GameObjectRecorder recorder;
        private double lastEditorTime;
        private float recordedDuration;
        private int recordingStartFrame;
        private int recordingEndFrame;

        // ---- ライフサイクル ----

        /// <summary>Play Mode 開始時に director の自動再生（Play On Awake）を停止する</summary>
        void Start()
        {
            if (!Application.isPlaying) return;
            if (director == null) return;
            if (director.state == PlayState.Playing)
            {
                director.Stop();
                Debug.Log("[FaceAnimationRecorder] director の自動再生（Play On Awake）を停止しました");
            }
        }

        void OnEnable()
        {
            SubscribeDirector(director);
        }

        void OnDisable()
        {
            UnsubscribeDirector(director);
            CancelRecording();
        }

        // ---- イベント登録 ----

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

        // ---- 公開 API ----

        public bool IsRecording => isRecording;

        /// <summary>REC START: startFrame から Timeline を再生し録画を開始する</summary>
        public void StartRecording()
        {
            if (!ValidateSetup()) return;
            if (isRecording)
            {
                Debug.LogWarning("[FaceAnimationRecorder] すでに録画中です", this);
                return;
            }

            float fps = GetFps();
            double startTime = startFrame / (double)fps;

            // director が入れ替わっている場合に備えて再登録
            UnsubscribeDirector(director);
            SubscribeDirector(director);

            recordingRequested = true;
            director.Play();
            // Play() がグラフを初期化した後に時刻を設定する
            // （Play() より前に設定するとグラフ再構築時にリセットされる）
            director.time = startTime;
            director.Evaluate(); // 強制的に startTime の位置へシーク
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
            // REC START ボタン経由の再生でなければ録画しない
            if (!recordingRequested) return;
            recordingRequested = false;

            if (isRecording) return;

            recorder = new GameObjectRecorder(targetRenderer.gameObject);
            recorder.BindComponentsOfType<SkinnedMeshRenderer>(targetRenderer.gameObject, false);

            isRecording = true;
            recordedDuration = 0f;
            recordingStartFrame = startFrame;
            lastEditorTime = EditorApplication.timeSinceStartup;

            EditorApplication.update += EditorUpdate;

            Debug.Log($"[FaceAnimationRecorder] 録画開始 (s{recordingStartFrame:D4})");
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

            float fps = GetFps();
            recordingEndFrame = recordingStartFrame + Mathf.RoundToInt(recordedDuration * fps);

            SaveClip();
        }

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

            float fps = GetFps();
            string timelineName = director.playableAsset != null ? director.playableAsset.name : "Timeline";
            string objectName = targetRenderer.gameObject.name;
            string prefixPart = string.IsNullOrEmpty(prefix) ? "" : $"{prefix}-";

            // テイク番号を自動カウントアップ（既存ファイルと重複しないまで加算）
            int take = NextTake;
            string savePath;
            do
            {
                string fileName = $"{prefixPart}{objectName}-{timelineName}-s{recordingStartFrame:D4}-e{recordingEndFrame:D4}-take{take}.anim";
                savePath = $"{saveFolder}/{fileName}";
                take++;
            } while (System.IO.File.Exists(savePath));
            take--; // ループで余分にインクリメントした分を戻す

            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(savePath);
            if (clip == null)
            {
                clip = new AnimationClip();
                AssetDatabase.CreateAsset(clip, savePath);
            }

            recorder.SaveToClip(clip, fps);
            AssetDatabase.SaveAssets();

            NextTake = take + 1; // EditorPrefs に永続保存

            recorder = null;
            Debug.Log($"[FaceAnimationRecorder] 保存完了: {savePath}  ({recordedDuration:F1}秒 @ {fps}fps)");
        }

        // ---- ユーティリティ ----

        private float GetFps()
        {
            if (director != null && director.playableAsset is TimelineAsset ta)
                return (float)ta.editorSettings.frameRate;
            return 30f;
        }

        private bool ValidateSetup()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[FaceAnimationRecorder] Play Mode でのみ録画できます", this);
                return false;
            }
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
        private SerializedProperty startFrameProp;

        void OnEnable()
        {
            startFrameProp = serializedObject.FindProperty("startFrame");
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

            // 録画中はフレーム入力を無効化
            using (new EditorGUI.DisabledScope(isRec))
            {
                serializedObject.Update();
                EditorGUILayout.PropertyField(startFrameProp, new GUIContent("Start Frame", "この位置（フレーム）から Timeline を再生して録画を開始します"));
                if (startFrameProp.intValue < 0) startFrameProp.intValue = 0;

                serializedObject.ApplyModifiedProperties();

                // テイク番号行（EditorPrefs 経由で永続化）
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    int newTake = EditorGUILayout.IntField(
                        new GUIContent("Next Take", "次の録画に付与されるテイク番号 (EditorPrefs に保存)"),
                        rec.NextTake);
                    if (EditorGUI.EndChangeCheck())
                        rec.NextTake = newTake;
                    if (GUILayout.Button("Reset", GUILayout.Width(50)))
                        rec.NextTake = 1;
                }
            }

            EditorGUILayout.Space(4);

            var prevBg = GUI.backgroundColor;

            if (!isRec)
            {
                using (new EditorGUI.DisabledScope(!Application.isPlaying))
                {
                    GUI.backgroundColor = Application.isPlaying
                        ? new Color(0.85f, 0.25f, 0.25f)
                        : Color.gray;
                    if (GUILayout.Button("●  REC START", GUILayout.Height(40)))
                        rec.StartRecording();
                }

                if (!Application.isPlaying)
                    EditorGUILayout.HelpBox("Play Mode で有効になります", MessageType.None);
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
                "2. Start Frame を設定して REC START\n" +
                "   → Timeline がその位置から再生・録画開始\n" +
                "3. Timeline 終端 または STOP & SAVE で自動保存\n\n" +
                "【ファイル名】\n" +
                "{prefix}-{SMR名}-{Timeline名}-s0000-e0000.anim\n\n" +
                "【Timeline への配置】\n" +
                "Animation Track のバインド先 = targetRenderer の GameObject",
                MessageType.Info);
        }
    }
#endif
}
