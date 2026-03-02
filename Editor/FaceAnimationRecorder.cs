using UnityEngine;
using UnityEngine.Timeline;
using UnityEngine.Playables;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
#endif

/// <summary>
/// PlayableDirectorの再生/停止イベントに連動して
/// BlendShapeを録画し、AnimationClipとして保存する。
///
/// 保存ファイル名の形式:
///   {prefix}-{GameObjectName}-{TimelineAsset名}-{startFrame}f-{endFrame}f.anim
///   例: take01-Face-MyTimeline-0f-120f.anim
///
/// 使い方:
/// 1. このスクリプトをフェイスのGameObjectにアタッチ
/// 2. Inspector で各フィールドを設定
/// 3. Timelineを再生するだけで録画・保存が自動で行われる
/// </summary>
///
///
namespace JayT.Facetracking.Editor{
public class FaceAnimationRecorder : MonoBehaviour
{
    [Header("録画対象")]
    [Tooltip("BlendShapeを持つSkinnedMeshRenderer（未指定なら自動取得）")]
    public SkinnedMeshRenderer targetRenderer;

    [Header("Timeline連携")]
    public PlayableDirector director;

    [Header("ファイル命名")]
    [Tooltip("ファイル名の先頭につけるPrefix")]
    public string prefix = "";

    [Header("保存先フォルダ")]
    [Tooltip("保存先フォルダ（Assets/以下）")]
    public string saveFolder = "Assets/Recordings";

    // 内部状態
#if UNITY_EDITOR
    private GameObjectRecorder recorder;
#endif
    private bool isRecording = false;
    private int startFrame = 0;

    void Start()
    {
        if (targetRenderer == null)
            targetRenderer = GetComponentInChildren<SkinnedMeshRenderer>();

        if (director == null)
        {
            Debug.LogError("[FaceAnimationRecorder] PlayableDirectorが未設定です");
            return;
        }

        director.played += OnDirectorPlayed;
        director.stopped += OnDirectorStopped;
    }

    void OnDestroy()
    {
        if (director == null) return;
        director.played -= OnDirectorPlayed;
        director.stopped -= OnDirectorStopped;
    }

    private void OnDirectorPlayed(PlayableDirector pd)
    {
#if UNITY_EDITOR
        if (isRecording) return;

        recorder = new GameObjectRecorder(gameObject);
        recorder.BindComponentsOfType<SkinnedMeshRenderer>(gameObject, true);

        startFrame = TimeToFrame(pd.time, pd);
        isRecording = true;

        Debug.Log($"[FaceAnimationRecorder] 録画開始 - frame: {startFrame}");
#endif
    }

    private void OnDirectorStopped(PlayableDirector pd)
    {
#if UNITY_EDITOR
        if (!isRecording) return;
        isRecording = false;

        int endFrame = TimeToFrame(pd.time, pd);
        SaveClip(endFrame);

        Debug.Log($"[FaceAnimationRecorder] 録画終了 - frame: {endFrame}");
#endif
    }

    void LateUpdate()
    {
#if UNITY_EDITOR
        if (!isRecording || recorder == null) return;
        recorder.TakeSnapshot(Time.deltaTime);
#endif
    }

    private void SaveClip(int endFrame)
    {
#if UNITY_EDITOR
        if (!System.IO.Directory.Exists(saveFolder))
            System.IO.Directory.CreateDirectory(saveFolder);

        string timelineName = director.playableAsset != null
            ? director.playableAsset.name
            : "Timeline";
        string objectName = targetRenderer != null
            ? targetRenderer.gameObject.name
            : gameObject.name;
        string prefixPart = string.IsNullOrEmpty(prefix) ? "" : $"{prefix}-";

        // {prefix}-{GameObjectName}-{TimelineAsset名}-{startFrame}f-{endFrame}f
        string fileName = $"{prefixPart}{objectName}-{timelineName}-{startFrame}f-{endFrame}f.anim";
        string savePath = $"{saveFolder}/{fileName}";

        AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(savePath);
        if (clip == null)
        {
            clip = new AnimationClip();
            AssetDatabase.CreateAsset(clip, savePath);
        }

        recorder.SaveToClip(clip);
        AssetDatabase.SaveAssets();

        Debug.Log($"[FaceAnimationRecorder] 保存完了: {savePath}");
#endif
    }

    private int TimeToFrame(double time, PlayableDirector pd)
    {
        double fps = 30.0;
        if (pd.playableAsset is TimelineAsset ta)
            fps = ta.editorSettings.frameRate;

        return Mathf.RoundToInt((float)(time * fps));
    }
}
}
