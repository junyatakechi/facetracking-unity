using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace JayT.Facetracking.Editor.IFacialMocap
{
    /// <summary>
    /// UDP Receiver for iFacialMocap.
    /// 修正版: 送信と受信を1つのUdpClientで行うことで接続安定性を向上させています。
    /// </summary>
    public class IFacialMocapReceiver : MonoBehaviour
    {
        [Header("Network Settings")]
        [Tooltip("iPhone の IP を指定 (例: 192.168.1.10)")]
        public string remoteIP = "";

        [Tooltip("PC 側受信ポート (iFacialMocap 標準は 49983)")]
        public int receivePort = 49983;

        [Header("Behavior")]
        [Tooltip("受信開始時に自動でデータ送信要求を送る")]
        public bool autoRequestStream = true;

        [Header("Debug")]
        public bool showDebugLog = true;

        [Header("Status (Read Only)")]
        [SerializeField] private bool isRunning = false;
        [SerializeField] private bool isConnected = false;
        [SerializeField] private int receivedPacketCount = 0;
        [SerializeField] private string lastReceiveInfo = "Not Connected";

        // Networking: 送受信を1つのクライアントで管理します
        private UdpClient udpClient = null;
        private Thread udpThread = null;
        private volatile bool udpThreadRunning = false;

        // Main thread marshalling
        private readonly Queue<string> incomingQueue = new Queue<string>();
        private readonly object queueLock = new object();

        // iFacialMocapデータ受信時に発火するイベント (メインスレッドから呼ばれます)
        public event Action<string> OnDataReceived;

        // Magic string for iFacialMocap (UDP)
        private const string udpMagic = "iFacialMocap_sahuasouryya9218sauhuiayeta91555dy3719";
        private const int iPhonePort = 49983;

        void Start()
        {
            if (string.IsNullOrEmpty(remoteIP))
            {
                Debug.LogWarning("[IFacialMocapReceiver] remoteIP が空です。Inspector で iPhone の IP を設定してください。");
                return;
            }

            StartReceiver();
        }

        void OnDestroy()
        {
            StopReceiver();
        }

        // メインスレッドでキューを処理
        void Update()
        {
            lock (queueLock)
            {
                while (incomingQueue.Count > 0)
                {
                    string msg = incomingQueue.Dequeue();
                    
                    receivedPacketCount++;
                    isConnected = true;
                    lastReceiveInfo = $"Last from {DateTime.Now:HH:mm:ss} len={msg.Length}";

                    if (showDebugLog)
                    {
                        // データ量が多いので、ログは間引くか短く表示するのがおすすめ
                        // Debug.Log($"[Recv] {msg}"); 
                    }

                    // --- データ処理 ---
                    // iFacialMocapのデータはパイプ記号 '|' 区切りです。
                    // 例: "mouthSmile_L-0|browDown_L-0|...|head#-1.5,2.3,0.0|..."
                    ParseAndApplyData(msg);
                }
            }

            // タイムアウト監視 (3秒以上データが来なければ切断扱い)
            if (isConnected)
            {
                if (DateTime.TryParseExact(lastReceiveInfo.Substring(10, 8), "HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out DateTime last))
                {
                    if ((DateTime.Now - last).TotalSeconds > 3)
                    {
                        isConnected = false;
                    }
                }
            }
        }

        // データ解析用（簡易実装）
        private void ParseAndApplyData(string data)
        {
            OnDataReceived?.Invoke(data);

            // ここでBlendShapeやボーン情報をパースしてアバターに適用します
            // 今回はデバッグログに一部を表示するのみとします
            if (showDebugLog && receivedPacketCount % 60 == 0) // 60フレームに1回ログ表示
            {
                // データ形式確認用: 先頭の50文字だけ表示
                string preview = data.Length > 50 ? data.Substring(0, 50) + "..." : data;
                Debug.Log($"[IFacialMocapReceiver] Data preview: {preview}");
            }
        }

        #region Public API
        public void StartReceiver()
        {
            if (udpThreadRunning)
            {
                Debug.LogWarning("[IFacialMocapReceiver] Already running.");
                return;
            }

            if (string.IsNullOrEmpty(remoteIP))
            {
                Debug.LogError("[IFacialMocapReceiver] remoteIP is not set.");
                return;
            }

            try
            {
                // 修正点: 送信と受信を兼ねたクライアントを作成し、受信ポートにバインドする
                // これにより「このポートから送信し、このポートで返信を待つ」状態を作る
                udpClient = new UdpClient(receivePort);
                
                // 受信タイムアウト設定 (受信処理がブロックし続けないように)
                udpClient.Client.ReceiveTimeout = 1000; 

                udpThreadRunning = true;
                udpThread = new Thread(UDPReceiveLoop) { IsBackground = true };
                udpThread.Start();

                isRunning = true;

                if (autoRequestStream)
                {
                    // 接続開始リクエストを送信
                    SendRequestStream();

                    // 念のため少し遅らせて再送（UDPはパケットロスがあるため）
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(200);
                        if (udpThreadRunning) SendRequestStream();
                        Thread.Sleep(500);
                        if (udpThreadRunning) SendRequestStream();
                    });
                }

                Debug.Log($"[IFacialMocapReceiver] Started on port {receivePort}. Target iPhone: {remoteIP}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[IFacialMocapReceiver] Failed to start: {e.Message}");
                StopReceiver();
            }
        }

        public void StopReceiver()
        {
            udpThreadRunning = false;
            isRunning = false;
            isConnected = false;

            try { udpThread?.Join(200); } catch { }
            try { udpClient?.Close(); udpClient?.Dispose(); } catch { }
            
            udpClient = null;
            udpThread = null;

            lock (queueLock) incomingQueue.Clear();
            Debug.Log("[IFacialMocapReceiver] Stopped.");
        }

        public bool IsRunning() => isRunning;
        #endregion

        #region Networking
        private void SendRequestStream()
        {
            if (udpClient == null) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(udpMagic);
                // 既にBind済みなので、Send時に宛先を指定して送信する
                udpClient.Send(bytes, bytes.Length, remoteIP, iPhonePort);
                
                if (showDebugLog) Debug.Log($"[Send] Magic request -> {remoteIP}:{iPhonePort}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[IFacialMocapReceiver] Send error: {e.Message}");
            }
        }

private void UDPReceiveLoop()
{
    // どのIPからでも受け取る設定
    IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
    
    Debug.LogWarning(">>> [Debug] UDP受信スレッド開始 (受信待機中...)");

    int logCount = 0;      // データ受信ログ回数制限用
    int timeoutCount = 0;  // タイムアウト回数カウント用

    while (udpThreadRunning)
    {
        try
        {
            if (udpClient == null) break;

            // 1. 最初の1回だけ待機メッセージを出す
            if (logCount == 0 && timeoutCount == 0) 
            {
                Debug.Log(">>> [Debug] Receive(...) を呼び出し。ここでデータが来るまで待機します...");
            }

            // --- ここでデータが来るまでプログラムが一時停止（ブロック）します ---
            // ※ udpClient.Client.ReceiveTimeout で設定した時間（例:1000ms）経過すると例外(TimedOut)が出ます
            byte[] data = udpClient.Receive(ref remoteEP);
            // -------------------------------------------------------------

            // 2. データが届いた場合
            if (data != null && data.Length > 0)
            {
                // 受信できたらタイムアウトカウントをリセット
                timeoutCount = 0;

                // 最初の5回だけ詳細ログを出す
                if (logCount < 5)
                {
                    string senderIP = remoteEP.Address.ToString();
                    string msg = Encoding.UTF8.GetString(data);
                    
                    // 文字列が長すぎる場合はカットして表示
                    string preview = msg.Length > 30 ? msg.Substring(0, 30) + "..." : msg;

                    Debug.Log($"<color=green>>>> [Debug] データ受信成功！</color>\n" +
                              $"送信元: {senderIP}\n" +
                              $"バイト数: {data.Length}\n" +
                              $"中身: {preview}");
                    
                    logCount++;
                }

                // メインスレッドへ渡す
                string fullMsg = Encoding.UTF8.GetString(data);
                lock (queueLock)
                {
                    incomingQueue.Enqueue(fullMsg);
                }
            }
        }
        catch (SocketException se)
        {
            // タイムアウトは正常動作（データが来ていないだけ）
            if (se.SocketErrorCode == SocketError.TimedOut)
            {
                // データをまだ一度も受信していない場合、定期的にログを出す
                // ReceiveTimeoutが1000msの場合、5回カウント＝約5秒おきに表示
                if (logCount == 0)
                {
                    timeoutCount++;
                    if (timeoutCount % 5 == 0)
                    {
                        Debug.Log($"... [Debug] まだデータが来ません (待機中... {timeoutCount}秒経過)");
                    }
                }
                continue;
            }
            
            // スレッド終了時は例外が出るので無視してループを抜ける
            if (!udpThreadRunning) break;
            
            Debug.LogError($"[Debug] ソケットエラー: {se.Message}");
        }
        catch (ThreadAbortException)
        {
            // スレッド強制終了時の例外（無視して良い）
            break;
        }
        catch (Exception e)
        {
            Debug.LogError($"[Debug] 予期せぬエラー: {e.Message}");
        }
    }
    Debug.LogWarning(">>> [Debug] UDP受信スレッド終了");
}


        #endregion
    }




#if UNITY_EDITOR
    [CustomEditor(typeof(IFacialMocapReceiver))]
    public class IFacialMocapReceiverEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var t = (IFacialMocapReceiver)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime Control", EditorStyles.boldLabel);

            GUI.enabled = Application.isPlaying;
            if (GUILayout.Button("Reconnect / Start"))
            {
                t.StopReceiver(); // 一旦停止してから再開
                t.StartReceiver();
            }
            if (GUILayout.Button("Stop"))
            {
                t.StopReceiver();
            }
            GUI.enabled = true;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("必ずPCとiPhoneを同じWi-Fiに接続してください。\nファイアウォールでブロックされていないか確認してください。", MessageType.Info);
        }
    }
#endif
}