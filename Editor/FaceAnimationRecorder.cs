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

        // CustomEditor で描画するため HideInInspector
        // 同シーンならシリアライズ参照として保持、別シーンは下記2フィールドで管理
        [HideInInspector] public PlayableDirector director;

        // クロスシーン参照の永続化（シーン名＋オブジェクト名で Play Mode 時に解決）
        [SerializeField, HideInInspector] private string directorSceneName = "";
        [SerializeField, HideInInspector] private string directorObjectName = "";

        [Header("ファイル命名")]
        [Tooltip("ファイル名Prefix（空白可）例: avatar-name")]
        public string prefix = "";

        [Header("保存先")]
        [Tooltip("保存先フォルダ（Assets/ 以下）")]
        public string saveFolder = "Assets/Recordings";

#if UNITY_EDITOR
        // 録画開始フレーム（Inspector の録画コントロール欄に表示）
        [SerializeField, HideInInspector] private int startFrame = 0;

        // 録画状態・保存状態（Custom Editor で参照）
        [SerializeField, HideInInspector] private bool isRecording = false;
        private bool isSaving = false;

        // テイク番号（EditorPrefs に保存 → Play Mode 終了でリセットされない）
        private string TakeKey => $"JayT.FaceAnimationRecorder.NextTake.{Application.dataPath}";
        public int NextTake
        {
            get => EditorPrefs.GetInt(TakeKey, 1);
            set => EditorPrefs.SetInt(TakeKey, Mathf.Max(1, value));
        }

        private GameObjectRecorder recorder;
        private double lastEditorTime;
        private float recordedDuration;
        private int recordingStartFrame;
        private int recordingEndFrame;
        private Animator suppressedAnimator; // REC中に無効化したAnimator

        // ---- ライフサイクル ----

        /// <summary>Play Mode 開始時に director を解決し、自動再生（Play On Awake）を停止する</summary>
        void Start()
        {
            if (!Application.isPlaying) return;

            // director が未解決の場合、シーン名＋オブジェクト名で全ロード済みシーンを検索
            if (director == null)
                TryFindDirector();

            if (director == null) return;
            if (director.state == PlayState.Playing)
            {
                director.Stop();
                Debug.Log("[FaceAnimationRecorder] director の自動再生（Play On Awake）を停止しました");
            }
        }

        /// <summary>シーン名＋オブジェクト名で PlayableDirector を検索して director に設定する</summary>
        private void TryFindDirector()
        {
            if (string.IsNullOrEmpty(directorObjectName))
            {
                Debug.LogWarning("[FaceAnimationRecorder] Director が未設定です。Inspector で PlayableDirector をアサインしてください");
                return;
            }

            foreach (var d in FindObjectsOfType<PlayableDirector>(true))
            {
                bool sceneMatch = string.IsNullOrEmpty(directorSceneName) || d.gameObject.scene.name == directorSceneName;
                if (sceneMatch && d.gameObject.name == directorObjectName)
                {
                    director = d;
                    SubscribeDirector(director);
                    Debug.Log($"[FaceAnimationRecorder] PlayableDirector を取得: '{d.gameObject.name}' (scene: '{d.gameObject.scene.name}')");
                    return;
                }
            }

            Debug.LogWarning($"[FaceAnimationRecorder] PlayableDirector '{directorObjectName}' が見つかりません（シーン '{directorSceneName}' がロードされているか確認）");
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
            d.stopped += OnDirectorStopped;
        }

        private void UnsubscribeDirector(PlayableDirector d)
        {
            if (d == null) return;
            d.stopped -= OnDirectorStopped;
        }

        // ---- 公開 API ----

        public bool IsRecording => isRecording;
        public bool IsSaving    => isSaving;

        /// <summary>REC START: startFrame から Timeline を再生して録画を開始する</summary>
        public void StartRecording()
        {
            if (!ValidateSetup()) return;
            if (isRecording)
            {
                Debug.LogWarning("[FaceAnimationRecorder] すでに録画中です", this);
                return;
            }

            // director が入れ替わっている場合に備えて再登録
            UnsubscribeDirector(director);
            SubscribeDirector(director);

            double startTime = startFrame / (double)GetFps();

            // 手動シーク後など、どんな再生状態でも確実に startFrame から再生するため:
            //   Stop()  → 再生中・一時停止・シーク済み状態をすべてリセット
            //             （Play() は「すでに再生中」だと即 return するため Stop が必須）
            //   initialTime → Stop()+Play() でグラフが再構築された場合の開始位置
            //   Play()  → 再生開始
            //   time    → グラフが再利用された（再構築されなかった）場合の明示的なシーク
            //   Evaluate() → startFrame のブレンドシェイプ値を即時確定
            director.Stop();
            director.initialTime = startTime;
            director.Play();
            director.time = startTime;
            director.Evaluate();

            // REC中はAnimatorがblendShapeを上書きしないよう無効化
            var anim = targetRenderer.GetComponent<Animator>();
            if (anim != null && anim.enabled)
            {
                anim.enabled = false;
                suppressedAnimator = anim;
                Debug.Log("[FaceAnimationRecorder] Animator を一時無効化しました（REC終了時に復元）");
            }

            // 録画セットアップ（Evaluate() でブレンドシェイプが確定した後に開始）
            recorder = new GameObjectRecorder(targetRenderer.gameObject);
            recorder.BindComponentsOfType<SkinnedMeshRenderer>(targetRenderer.gameObject, false);
            isRecording = true;
            recordedDuration = 0f;
            recordingStartFrame = startFrame;
            lastEditorTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += EditorUpdate;

            Debug.Log($"[FaceAnimationRecorder] 録画開始 (s{recordingStartFrame:D4})");
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
            RestoreAnimator();

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
            RestoreAnimator();
            Debug.LogWarning("[FaceAnimationRecorder] 録画がキャンセルされました（保存されていません）");
        }

        private void RestoreAnimator()
        {
            if (suppressedAnimator == null) return;
            suppressedAnimator.enabled = true;
            suppressedAnimator = null;
            Debug.Log("[FaceAnimationRecorder] Animator を復元しました");
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

            isSaving = true;

            recorder.SaveToClip(clip, fps);

            // BlendShape 以外の float カーブを削除（ボーン等）
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (!binding.propertyName.StartsWith("blendShape."))
                    AnimationUtility.SetEditorCurve(clip, binding, null);
            }
            // オブジェクト参照カーブを削除（マテリアル参照等）
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                AnimationUtility.SetObjectReferenceCurve(clip, binding, null);

            AssetDatabase.SaveAssets();

            NextTake = take + 1; // EditorPrefs に永続保存

            recorder = null;
            isSaving = false;
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
        private SerializedProperty sceneNameProp;
        private SerializedProperty objectNameProp;

        void OnEnable()
        {
            startFrameProp  = serializedObject.FindProperty("startFrame");
            sceneNameProp   = serializedObject.FindProperty("directorSceneName");
            objectNameProp  = serializedObject.FindProperty("directorObjectName");
            EditorApplication.update += Repaint;
        }

        void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        public override void OnInspectorGUI()
        {
            var rec = (FaceAnimationRecorder)target;
            serializedObject.Update();

            // ---- 録画対象 ----
            EditorGUILayout.PropertyField(serializedObject.FindProperty("targetRenderer"));

            // ---- Timeline連携 ----
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Timeline連携", EditorStyles.boldLabel);

            DrawDirectorField(rec);

            // ---- その他フィールド ----
            EditorGUILayout.PropertyField(serializedObject.FindProperty("prefix"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("saveFolder"));

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("録画コントロール", EditorStyles.boldLabel);

            bool isRec    = rec.IsRecording;
            bool isSaving = rec.IsSaving;

            // 録画中・保存中はフレーム入力を無効化
            using (new EditorGUI.DisabledScope(isRec || isSaving))
            {
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

            if (isSaving)
            {
                GUI.backgroundColor = Color.gray;
                using (new EditorGUI.DisabledScope(true))
                    GUILayout.Button("●  REC START", GUILayout.Height(40));
                EditorGUILayout.Space(2);
                EditorGUILayout.HelpBox("保存中... しばらくお待ちください", MessageType.Warning);
            }
            else if (!isRec)
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
                "{prefix}-{SMR名}-{Timeline名}-s0000-e0000-take1.anim\n\n" +
                "【Timeline への配置】\n" +
                "Animation Track のバインド先 = targetRenderer の GameObject(animatorが必要)",
                MessageType.Info);
        }

        /// <summary>
        /// クロスシーン対応 Director ピッカーを描画する。
        /// ドラッグ時にシーン名＋オブジェクト名を保存し、表示もそれで解決する。
        /// </summary>
        private void DrawDirectorField(FaceAnimationRecorder rec)
        {
            // 表示用オブジェクトの解決:
            //   Play Mode → runtime で解決済みの rec.director を優先
            //   Edit Mode → シーン名＋オブジェクト名で FindObjectsOfType して逆引き
            PlayableDirector displayDirector = rec.director;
            if (displayDirector == null && !string.IsNullOrEmpty(objectNameProp.stringValue))
            {
                foreach (var d in FindObjectsOfType<PlayableDirector>(true))
                {
                    bool sceneMatch = string.IsNullOrEmpty(sceneNameProp.stringValue) || d.gameObject.scene.name == sceneNameProp.stringValue;
                    if (sceneMatch && d.gameObject.name == objectNameProp.stringValue)
                    {
                        displayDirector = d;
                        break;
                    }
                }
            }

            var label = new GUIContent(
                "Director",
                "PlayableDirectorをここにドラッグ（別シーンでも可）\n" +
                "→ シーン名＋オブジェクト名を保存し、Play Mode開始時に自動解決されます");

            EditorGUI.BeginChangeCheck();
            var picked = (PlayableDirector)EditorGUILayout.ObjectField(label, displayDirector, typeof(PlayableDirector), true);

            if (EditorGUI.EndChangeCheck())
            {
                if (picked != null)
                {
                    sceneNameProp.stringValue  = picked.gameObject.scene.name;
                    objectNameProp.stringValue = picked.gameObject.name;
                    // 同シーンなら director フィールドにも直接保持
                    rec.director = picked.gameObject.scene == rec.gameObject.scene ? picked : null;
                }
                else
                {
                    sceneNameProp.stringValue  = "";
                    objectNameProp.stringValue = "";
                    rec.director = null;
                }
                serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(rec);
            }

            // 別シーン参照の場合はシーン名とオブジェクト名を補足表示
            if (displayDirector != null && rec.director == null)
            {
                EditorGUI.indentLevel++;
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.LabelField("Scene",  sceneNameProp.stringValue,  EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("Object", objectNameProp.stringValue, EditorStyles.miniLabel);
                }
                EditorGUI.indentLevel--;
            }

            // 未設定の場合は案内を表示
            if (string.IsNullOrEmpty(objectNameProp.stringValue))
            {
                EditorGUILayout.HelpBox(
                    "未設定です。\n" +
                    "マルチシーン編集中に別シーンのPlayableDirectorをドラッグできます。",
                    MessageType.Info);
            }
        }
    }
#endif
}
