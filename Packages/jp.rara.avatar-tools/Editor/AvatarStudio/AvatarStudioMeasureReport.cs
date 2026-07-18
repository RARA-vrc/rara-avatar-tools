// RARA アバター軽量化・Quest/iOS対応ツール - 実測レポート(Agent R 所有)
// 「見積り」ではなく「実測」でアバターの重さを確かめるための一式。全部このファイルに収める。
//
// 収録物(すべて namespace RARA.AvatarStudio):
//  1) AvatarStudioMeasurePrefs   … EditorPrefs のオプトイン設定(計測有効 / ▶️で表示 / 全アバター)
//  2) 直列化データ(MeasuredCategory / MeasuredAvatar / AvatarStudioMeasureData)
//  3) AvatarStudioMeasureStore   … 静的ストア + SessionState(ドメインリロード/ビルドをまたいで保持)
//  4) AvatarStudioMeasureHook    … IVRCSDKPreprocessAvatarCallback(callbackOrder=int.MaxValue で最後に走る)。
//        NDMF(-11000/-1025)・QuestBuildPreprocessor(1024)・lilToon(100)・SDKのEditorOnly除去より後に走り、
//        Play(ApplyOnPlay)でも実アップロードビルドでも「最終的な複製そのもの」を AvatarPerformance で計測する。
//        try/catch で必ず true を返す(ビルドを止めない)。
//  5) AvatarStudioBuildSizeWatcher … [InitializeOnLoad]。SDK Control Panel のビルダー
//        (IVRCSdkAvatarBuilderApi)の OnSdkBuildSuccess をリフレクションで購読し、生成された
//        アセットバンドルの実ファイルサイズ(FileInfo.Length)をストアへ結合する。
//        ※ VRCSdkControlPanel は VRC.SDKBase.Editor.dll に属し本アセンブリは参照しないため、購読は全てリフレクション。
//  6) AvatarStudioMeasureReportWindow … IMGUI レポートウィンドウ。アバターごとに総合+項目別値(ランク配色)、
//        目標比較、元アバターとの差、実測ビルドサイズ(_Quest は10MB判定)、オプトアウト、再計測ボタン。
//
// 設計(NDMF/SDK ソース調査に基づく・厳守):
//  ・Play では NDMF ApplyOnPlay が Awake/Start 中にシーン上の実体へ OnPreprocessAvatar を掛ける
//    (処理中は "(Clone)" が名前に付く → 名前照合時はサフィックス除去)。実ビルドではビルド用複製に掛かる。
//  ・isMobile = 現在のビルドターゲットが Android/iOS か(ValidationEditorHelpers.IsMobilePlatform 相当。
//    当該型は参照外アセンブリのため定義を写経)。_Opt は Windows 基準、_Quest は Mobile 基準を権威とする。
//    どちらの基準も安価に取れるため常に両方計測し、アクティブビルドターゲットに一致する側を「権威」と表示する。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;           // IVRCSDKPreprocessAvatarCallback
using VRC.SDKBase.Validation.Performance;          // AvatarPerformance
using VRC.SDKBase.Validation.Performance.Stats;    // AvatarPerformanceStats / AvatarPerformanceCategory
using VRC.SDK3.Avatars.Components;                 // VRCAvatarDescriptor
using RARA.QuestConverter;                         // QuestCompat.FindType

namespace RARA.AvatarStudio
{
    // ============================================================
    // 1) EditorPrefs のオプトイン設定
    // ============================================================

    /// <summary>実測レポートのオプトイン設定(EditorPrefs 永続。ウィンドウのトグルと結線)。</summary>
    internal static class AvatarStudioMeasurePrefs
    {
        private const string K = "RARA.AvatarStudio.MeasureReport.";

        /// <summary>ビルド/Play 時に実測を行うか(既定 有効)。オフにすると一切計測しない。</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(K + "Enabled", true);
            set => EditorPrefs.SetBool(K + "Enabled", value);
        }

        /// <summary>計測後に実測レポートを自動表示するか(▶️/ビルドごとに1回・非フォーカス。既定 有効)。</summary>
        public static bool ShowOnPlay
        {
            get => EditorPrefs.GetBool(K + "ShowOnPlay", true);
            set => EditorPrefs.SetBool(K + "ShowOnPlay", value);
        }

        /// <summary>名前が _Opt/_Quest で終わらないアバターも計測対象に含めるか(既定 オフ)。</summary>
        public static bool MeasureAllAvatars
        {
            get => EditorPrefs.GetBool(K + "AllAvatars", false);
            set => EditorPrefs.SetBool(K + "AllAvatars", value);
        }
    }

    // ============================================================
    // 2) 直列化データ(JsonUtility で SessionState へ往復)
    // ============================================================

    /// <summary>1項目ぶんの実測値(PC/Quest 両基準の値・ランク)。</summary>
    [Serializable]
    public class MeasuredCategory
    {
        public string label;          // 日本語表示名(Diagnostics のラベルと一致 → 目標/差分の突き合わせに使う)
        public bool isMB;             // MB 表示か

        public bool hasPc;
        public float pcValue;
        public string pcValueText = "-";
        public string pcRating = string.Empty;   // "Excellent".."VeryPoor"

        public bool hasQuest;
        public float questValue;
        public string questValueText = "-";
        public string questRating = string.Empty;
    }

    /// <summary>1アバターぶんの実測結果。</summary>
    [Serializable]
    public class MeasuredAvatar
    {
        public string name;             // "(Clone)" を除いた名前(例: "Foo_Opt")
        public string cloneType;        // "_Opt" / "_Quest" / "" (元)
        public string authoritativeBasis; // "PC" / "Quest"(アクティブビルドターゲットに一致する基準)

        public bool measuredPc;
        public bool measuredQuest;
        public string pcOverall = string.Empty;
        public string questOverall = string.Empty;

        public long buildSizeBytes = -1;   // 実測ビルドサイズ(圧縮後・オンディスク。-1 = 未取得)
        public long uncompressedBuildSizeBytes = -1; // 実測 展開後(非圧縮)サイズ(SDK API で取得。-1 = 未取得)
        public string buildBundlePath = string.Empty;
        public double measuredAtUtc;       // 計測時刻(UTC Ticks)
        public double buildAtUtc;          // ビルドサイズ取得時刻(UTC Ticks)
        public bool fromBuild;             // true=ビルド/Playの実測 / false=エディタ再計測(スタジオ診断)
        public bool instantBake;           // true=ビルド不要の即時実測(NDMF手動ベイク)。fromBuild と併存(表示ラベル用)

        public List<MeasuredCategory> categories = new List<MeasuredCategory>();
    }

    /// <summary>ストア全体(SessionState に JSON で保持)。</summary>
    [Serializable]
    public class AvatarStudioMeasureData
    {
        public List<MeasuredAvatar> avatars = new List<MeasuredAvatar>();
    }

    // ============================================================
    // 3) 静的ストア + SessionState
    // ============================================================

    /// <summary>実測結果の保管庫。ドメインリロード・ビルドをまたいで残るよう SessionState に JSON で退避する。</summary>
    internal static class AvatarStudioMeasureStore
    {
        private const string SessionKey = "RARA.AvatarStudio.MeasureReport.Store.v1";
        private const string LastBundlePrefKey = "currentBuildingAssetBundlePath"; // SDK フォールバック

        private static AvatarStudioMeasureData _data;
        private static string _lastBuiltName;

        public static AvatarStudioMeasureData Data
        {
            get { EnsureLoaded(); return _data; }
        }

        private static void EnsureLoaded()
        {
            if (_data != null) return;
            string json = SessionState.GetString(SessionKey, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                try { _data = JsonUtility.FromJson<AvatarStudioMeasureData>(json); }
                catch { _data = null; }
            }
            if (_data == null) _data = new AvatarStudioMeasureData();
            if (_data.avatars == null) _data.avatars = new List<MeasuredAvatar>();
        }

        private static void Save()
        {
            try { SessionState.SetString(SessionKey, JsonUtility.ToJson(_data)); }
            catch (Exception ex) { Debug.LogWarning("[RARA AvatarStudio] 実測結果の保存に失敗しました: " + ex.Message); }
        }

        /// <summary>同名(同じ複製)があれば置き換え、無ければ追加する。</summary>
        public static void Record(MeasuredAvatar measured)
        {
            if (measured == null || string.IsNullOrEmpty(measured.name)) return;
            EnsureLoaded();
            for (int i = 0; i < _data.avatars.Count; i++)
            {
                if (_data.avatars[i] != null && string.Equals(_data.avatars[i].name, measured.name, StringComparison.Ordinal))
                {
                    // ビルド実測サイズは新しい計測に引き継ぐ(再計測でサイズが消えないように)。
                    if (measured.buildSizeBytes < 0 && _data.avatars[i].buildSizeBytes >= 0)
                    {
                        measured.buildSizeBytes = _data.avatars[i].buildSizeBytes;
                        measured.uncompressedBuildSizeBytes = _data.avatars[i].uncompressedBuildSizeBytes;
                        measured.buildBundlePath = _data.avatars[i].buildBundlePath;
                        measured.buildAtUtc = _data.avatars[i].buildAtUtc;
                    }
                    _data.avatars[i] = measured;
                    Save();
                    return;
                }
            }
            _data.avatars.Add(measured);
            Save();
        }

        /// <summary>直近にビルド計測したアバター名を記録(OnSdkBuildSuccess でサイズを結合する対象)。</summary>
        public static void SetLastBuilt(string name)
        {
            _lastBuiltName = name;
        }

        /// <summary>名前で実測結果を引く(見つからなければ null)。</summary>
        public static MeasuredAvatar Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            EnsureLoaded();
            foreach (MeasuredAvatar a in _data.avatars)
            {
                if (a != null && string.Equals(a.name, name, StringComparison.Ordinal)) return a;
            }
            return null;
        }

        /// <summary>
        /// ビルド成功時、生成アセットバンドルの実ファイルサイズを直近ビルドしたアバターへ結合する。
        /// bundlePath が空なら SDK の EditorPrefs フォールバックを見る。例外は握りつぶす(黙って諦める)。
        /// </summary>
        public static void RecordBuildSize(string bundlePath)
        {
            try
            {
                string path = bundlePath;
                if (string.IsNullOrEmpty(path)) path = EditorPrefs.GetString(LastBundlePrefKey, string.Empty);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                long len = new FileInfo(path).Length;

                EnsureLoaded();
                MeasuredAvatar target = null;
                if (!string.IsNullOrEmpty(_lastBuiltName)) target = Find(_lastBuiltName);
                if (target == null)
                {
                    // 直近に計測したものへ結合(名前不明時の保険)。
                    double best = double.MinValue;
                    foreach (MeasuredAvatar a in _data.avatars)
                    {
                        if (a != null && a.measuredAtUtc > best) { best = a.measuredAtUtc; target = a; }
                    }
                }
                if (target == null) return;

                target.buildSizeBytes = len;
                target.buildBundlePath = path;
                target.buildAtUtc = DateTime.UtcNow.Ticks;

                // 展開後(非圧縮)サイズは圧縮ファイルからは求められないが、SDKがビルド直後の値をキャッシュしている。
                // ValidationEditorHelpers.CheckIfUncompressedAssetBundleFileTooLarge(ContentType.Avatar, out int, isMobile)
                // をリフレクションで呼んで取得する(本アセンブリは VRCSDKBase-Editor.dll を参照しないため反射)。
                // OnSdkBuildSuccess の直後(=このメソッド内)でのみ有効。timing は未検証のため失敗・異常値は黙って無視する。
                long uncompressed;
                if (TryGetUncompressedBuildSize(len, out uncompressed))
                {
                    target.uncompressedBuildSizeBytes = uncompressed;
                }
                Save();

                if (AvatarStudioMeasureReportWindow.HasOpenInstance)
                    AvatarStudioMeasureReportWindow.RepaintIfOpen();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RARA AvatarStudio] ビルド実測サイズの記録に失敗しました: " + ex.Message);
            }
        }

        /// <summary>
        /// SDK がビルド直後にキャッシュしている「展開後(非圧縮)アセットバンドルサイズ」を
        /// ValidationEditorHelpers.CheckIfUncompressedAssetBundleFileTooLarge(ContentType.Avatar, out int, isMobile)
        /// でリフレクション取得する。取得できて妥当(0超・圧縮後サイズ以上)なら true。
        /// VRCSDKBase-Editor.dll 未参照のため全て反射。型・メソッド未解決や例外時は false(黙って諦める)。
        /// </summary>
        private static bool TryGetUncompressedBuildSize(long compressedLen, out long uncompressed)
        {
            uncompressed = -1;
            try
            {
                Type helperType = QuestCompat.FindType("VRC.SDKBase.Editor.Validation.ValidationEditorHelpers");
                Type contentType = QuestCompat.FindType("VRC.SDKBase.Editor.Validation.ContentType");
                if (helperType == null || contentType == null) return false;

                MethodInfo method = helperType.GetMethod("CheckIfUncompressedAssetBundleFileTooLarge",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new Type[] { contentType, typeof(int).MakeByRefType(), typeof(bool) }, null);
                if (method == null) return false;

                object avatarContent;
                try { avatarContent = Enum.Parse(contentType, "Avatar"); }
                catch { return false; }

                bool mobile = AvatarStudioMeasureUtil.IsMobilePlatform();
                var args = new object[] { avatarContent, 0, mobile };
                method.Invoke(null, args); // 戻り値(超過フラグ)は使わず out のサイズだけ取る
                int bytes = (int)args[1];

                // 展開後は圧縮後(オンディスク)以上のはず。0以下や圧縮後未満はキャッシュ未更新/別プラットフォーム等の
                // 疑いがあるため採用しない(誤った実測値を出さない)。
                if (bytes <= 0 || bytes < compressedLen) return false;
                uncompressed = bytes;
                return true;
            }
            catch
            {
                return false; // timing/署名不一致等は黙って諦める
            }
        }

        /// <summary>ストアを空にする(ウィンドウの「クリア」用)。</summary>
        public static void Clear()
        {
            EnsureLoaded();
            _data.avatars.Clear();
            _lastBuiltName = null;
            Save();
        }
    }

    // ============================================================
    // 4) 計測フック(Play + 実アップロードビルド 兼用)
    // ============================================================

    /// <summary>
    /// 最終複製を実測する VRChat SDK プリプロセスコールバック。callbackOrder=int.MaxValue で
    /// NDMF(Transforming -11000 / Optimizing -1025)・QuestBuildPreprocessor(1024)・lilToon(100)・
    /// SDK の EditorOnly 除去・その他あらゆる第三者プリプロセスより後(最後)に走る
    /// (参考実装 anatawa12 ActualPerformanceWindow と同方針)。よって最終的な複製そのものの姿を測れる。
    /// 本フックは計測(読み取り)のみで複製を改変しないため、最後に走っても他処理へ影響しない。
    /// </summary>
    public class AvatarStudioMeasureHook : IVRCSDKPreprocessAvatarCallback
    {
        public int callbackOrder => int.MaxValue;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            try
            {
                if (!AvatarStudioMeasurePrefs.Enabled || avatarGameObject == null) return true;
                if (avatarGameObject.GetComponent<VRCAvatarDescriptor>() == null) return true;

                string name = AvatarStudioMeasureUtil.StripCloneSuffix(avatarGameObject.name);
                string cloneType = AvatarStudioMeasureUtil.DetectCloneType(name);

                bool inScope = cloneType.Length > 0 || AvatarStudioMeasurePrefs.MeasureAllAvatars;
                if (!inScope) return true;

                MeasuredAvatar measured = AvatarStudioMeasureUtil.MeasureActual(avatarGameObject, name, cloneType);
                if (measured != null)
                {
                    AvatarStudioMeasureStore.Record(measured);
                    AvatarStudioMeasureStore.SetLastBuilt(name);
                    SchedulePopup();
                }
            }
            catch (Exception ex)
            {
                // 計測失敗でビルドは止めない。
                Debug.LogWarning("[RARA AvatarStudio] 実測フックで例外が発生しました(ビルドは続行します): " + ex.Message);
            }
            return true;
        }

        private static void SchedulePopup()
        {
            EditorApplication.delayCall += () =>
            {
                try { AvatarStudioMeasureReportWindow.ShowIfEnabled(); }
                catch (Exception ex) { Debug.LogWarning("[RARA AvatarStudio] 実測レポートの表示に失敗しました: " + ex.Message); }
            };
        }
    }

    // ============================================================
    // 5) ビルドサイズ購読(リフレクションで SDK ビルダーへ)
    // ============================================================

    /// <summary>
    /// SDK Control Panel のビルダー(IVRCSdkAvatarBuilderApi)の OnSdkBuildSuccess を購読し、
    /// 生成バンドルの実ファイルサイズをストアへ結合する。VRCSdkControlPanel は本アセンブリ非参照のため
    /// 全てリフレクション。パネルの開閉でビルダー実体が入れ替わるので軽量ポーリングで再購読する。
    /// </summary>
    [InitializeOnLoad]
    internal static class AvatarStudioBuildSizeWatcher
    {
        private static double _nextPoll;
        private static object _subscribedBuilder;
        private static EventInfo _subscribedEvent;
        private static Delegate _handler;

        static AvatarStudioBuildSizeWatcher()
        {
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                AvatarStudioMeasureReportWindow.ResetPlaySession();
        }

        private static void Update()
        {
            // 計測機能がオフのときは完全に休眠する(購読解除して以降は一切ポーリング/購読しない)。
            if (!AvatarStudioMeasurePrefs.Enabled) { Unsubscribe(); return; }
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 1.0; // 1秒間隔の軽量ポーリング
            try { TrySubscribe(); }
            catch { /* SDK未ロード等は黙って次回へ */ }
        }

        private static void TrySubscribe()
        {
            Type panelType = QuestCompat.FindType("VRCSdkControlPanel");
            if (panelType == null) return;

            // SDK Control Panel が一度も開かれていない間は静的フィールド window が null。
            // その状態で TryGetBuilder を呼ぶと SDK 側が毎フレーム Debug.LogError を出す
            // (VRCSdkControlPanelBuilder.TryGetBuilder: "Cannot get builder, SDK window is not open")。
            // パネルが閉じている間は購読を試みず、コンソールを汚さない。
            FieldInfo windowField = panelType.GetField("window",
                BindingFlags.Public | BindingFlags.Static);
            if (windowField == null || windowField.GetValue(null) == null) return;

            MethodInfo tryGet = panelType.GetMethod("TryGetBuilder",
                BindingFlags.Public | BindingFlags.Static);
            if (tryGet == null || !tryGet.IsGenericMethodDefinition) return;

            Type apiType = QuestCompat.FindType("VRC.SDK3A.Editor.IVRCSdkAvatarBuilderApi");
            if (apiType == null) return;

            object builder;
            try
            {
                MethodInfo g = tryGet.MakeGenericMethod(apiType);
                var args = new object[] { null };
                bool ok = (bool)g.Invoke(null, args);
                if (!ok || args[0] == null) { return; }
                builder = args[0];
            }
            catch { return; }

            if (ReferenceEquals(builder, _subscribedBuilder)) return; // 同一ビルダーには二重購読しない

            Unsubscribe();

            EventInfo evt = builder.GetType().GetEvent("OnSdkBuildSuccess");
            if (evt == null || evt.EventHandlerType == null) return;

            MethodInfo relay = typeof(AvatarStudioBuildSizeWatcher)
                .GetMethod(nameof(OnBuildSuccess), BindingFlags.NonPublic | BindingFlags.Static);
            Delegate handler;
            try { handler = Delegate.CreateDelegate(evt.EventHandlerType, relay); }
            catch { return; } // 署名不一致(EventHandler<string> 以外)なら諦める

            evt.AddEventHandler(builder, handler);
            _subscribedBuilder = builder;
            _subscribedEvent = evt;
            _handler = handler;
        }

        private static void Unsubscribe()
        {
            try
            {
                if (_subscribedBuilder != null && _subscribedEvent != null && _handler != null)
                    _subscribedEvent.RemoveEventHandler(_subscribedBuilder, _handler);
            }
            catch { /* ビルダー破棄済み等は無視 */ }
            _subscribedBuilder = null;
            _subscribedEvent = null;
            _handler = null;
        }

        // EventHandler<string> シグネチャ: (object sender, string bundlePath)
        private static void OnBuildSuccess(object sender, string bundlePath)
        {
            if (!AvatarStudioMeasurePrefs.Enabled) return; // オプトアウト時は記録しない
            try { AvatarStudioMeasureStore.RecordBuildSize(bundlePath); }
            catch (Exception ex) { Debug.LogWarning("[RARA AvatarStudio] OnSdkBuildSuccess 処理で例外: " + ex.Message); }
        }
    }

    // ============================================================
    // 計測ユーティリティ(フック・ウィンドウ再計測で共用)
    // ============================================================

    internal static class AvatarStudioMeasureUtil
    {
        private struct Metric
        {
            public string label;
            public AvatarPerformanceCategory category;
            public bool isMB;
            public Metric(string label, AvatarPerformanceCategory category, bool isMB = false)
            { this.label = label; this.category = category; this.isMB = isMB; }
        }

        // ラベルは AvatarStudioDiagnostics.Metrics と一致させる(目標比較・元との差の突き合わせに使う)。
        // 先頭の DownloadSize(ダウンロードサイズ)は Diagnostics 側には無いが実測レポート固有として表示する。
        private static readonly Metric[] Metrics =
        {
            new Metric("ダウンロードサイズ(推定)", AvatarPerformanceCategory.DownloadSize, true),
            new Metric("三角数(ポリゴン)", AvatarPerformanceCategory.PolyCount),
            new Metric("スキンメッシュ数", AvatarPerformanceCategory.SkinnedMeshCount),
            new Metric("メッシュ数", AvatarPerformanceCategory.MeshCount),
            new Metric("マテリアルスロット数", AvatarPerformanceCategory.MaterialCount),
            new Metric("テクスチャメモリ(MB)", AvatarPerformanceCategory.TextureMegabytes, true),
            new Metric("ボーン数", AvatarPerformanceCategory.BoneCount),
            new Metric("PhysBoneコンポーネント数", AvatarPerformanceCategory.PhysBoneComponentCount),
            new Metric("PhysBone対象Transform数", AvatarPerformanceCategory.PhysBoneTransformCount),
            new Metric("PhysBoneコライダー数", AvatarPerformanceCategory.PhysBoneColliderCount),
            new Metric("PhysBone衝突チェック数", AvatarPerformanceCategory.PhysBoneCollisionCheckCount),
            new Metric("コンタクト数", AvatarPerformanceCategory.ContactCount),
            new Metric("コンストレイント数", AvatarPerformanceCategory.ConstraintsCount),
            new Metric("アニメーター数", AvatarPerformanceCategory.AnimatorCount),
            new Metric("パーティクルシステム数", AvatarPerformanceCategory.ParticleSystemCount),
        };

        /// <summary>NDMF 処理中に付く "(Clone)" サフィックスを1つ取り除く。</summary>
        public static string StripCloneSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? string.Empty;
            const string suffix = "(Clone)";
            return name.EndsWith(suffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - suffix.Length)
                : name;
        }

        /// <summary>名前の末尾から複製種別を判定("_Opt" / "_Quest" / "")。</summary>
        public static string DetectCloneType(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            if (name.EndsWith("_Quest", StringComparison.Ordinal)) return "_Quest";
            if (name.EndsWith("_Opt", StringComparison.Ordinal)) return "_Opt";
            return string.Empty;
        }

        /// <summary>現在のビルドターゲットがモバイル(Android/iOS)か。ValidationEditorHelpers.IsMobilePlatform 相当。</summary>
        public static bool IsMobilePlatform()
        {
            BuildTarget t = EditorUserBuildSettings.activeBuildTarget;
            return t == BuildTarget.Android || t == BuildTarget.iOS;
        }

        /// <summary>
        /// 「最終複製そのもの」を AvatarPerformance で実測する(フック用)。PC(Windows)/Quest(Mobile)両基準。
        /// 権威基準はアクティブビルドターゲットに一致する側(モバイル→Quest / それ以外→PC)。
        /// 元アバターではなくビルド用複製に対して呼ばれる前提のため、複製を直接計測してよい。
        /// </summary>
        public static MeasuredAvatar MeasureActual(GameObject go, string name, string cloneType)
        {
            if (go == null) return null;

            RefreshConstraintGroups(go);

            bool mobile = IsMobilePlatform();
            var m = new MeasuredAvatar
            {
                name = name,
                cloneType = cloneType,
                authoritativeBasis = mobile ? "Quest" : "PC",
                measuredPc = true,
                measuredQuest = true,
                measuredAtUtc = DateTime.UtcNow.Ticks,
                fromBuild = true,
            };

            var pcCells = new Dictionary<AvatarPerformanceCategory, Cell>();
            var questCells = new Dictionary<AvatarPerformanceCategory, Cell>();
            m.pcOverall = ComputeInto(go, name, false, pcCells);
            m.questOverall = ComputeInto(go, name, true, questCells);

            BuildCategories(m, pcCells, questCells);
            return m;
        }

        /// <summary>
        /// スタジオ診断(AvatarStudioDiagnostics.Analyze)からエディタ再計測ぶんを作る。
        /// 元アバターを無改変で計測できる(内部で一時複製・EditorOnly除去する)ため、シーン上の実体に安全。
        /// </summary>
        public static MeasuredAvatar MeasureViaStudio(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return null;
            string name = avatar.gameObject.name;
            string cloneType = DetectCloneType(name);
            bool mobile = IsMobilePlatform();

            StudioDiagnosis diag = AvatarStudioDiagnostics.Analyze(avatar, true, true, null);

            var m = new MeasuredAvatar
            {
                name = name,
                cloneType = cloneType,
                authoritativeBasis = mobile ? "Quest" : "PC",
                measuredPc = diag.pcIncluded,
                measuredQuest = diag.questIncluded,
                pcOverall = diag.pcOverallRating,
                questOverall = diag.questOverallRating,
                measuredAtUtc = DateTime.UtcNow.Ticks,
                fromBuild = false,
            };

            foreach (StudioMetricRow r in diag.rows)
            {
                if (r == null || string.IsNullOrEmpty(r.label)) continue;
                m.categories.Add(new MeasuredCategory
                {
                    label = r.label,
                    isMB = r.isMB,
                    hasPc = r.pcHasValue,
                    pcValue = r.pcValue,
                    pcValueText = r.pcValueText,
                    pcRating = r.pcRating,
                    hasQuest = r.questHasValue,
                    questValue = r.questValue,
                    questValueText = r.questValueText,
                    questRating = r.questRating,
                });
            }
            return m;
        }

        private struct Cell { public bool has; public float value; public string rating; }

        private static string ComputeInto(GameObject go, string name, bool isMobile,
            Dictionary<AvatarPerformanceCategory, Cell> cells)
        {
            var stats = new AvatarPerformanceStats(isMobile);
            AvatarPerformance.CalculatePerformanceStats(name, go, stats, isMobile);

            foreach (Metric def in Metrics)
            {
                bool has = ReadStat(stats, def.category, out float value);
                PerformanceRating rating = stats.GetPerformanceRatingForCategory(def.category);
                cells[def.category] = new Cell { has = has, value = value, rating = rating.ToString() };
            }
            return stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall).ToString();
        }

        private static void BuildCategories(MeasuredAvatar m,
            Dictionary<AvatarPerformanceCategory, Cell> pcCells,
            Dictionary<AvatarPerformanceCategory, Cell> questCells)
        {
            foreach (Metric def in Metrics)
            {
                var c = new MeasuredCategory { label = def.label, isMB = def.isMB };
                if (pcCells.TryGetValue(def.category, out Cell pc))
                {
                    c.hasPc = pc.has;
                    c.pcValue = pc.value;
                    c.pcValueText = FormatValue(pc.value, pc.has, def.isMB);
                    c.pcRating = pc.rating;
                }
                if (questCells.TryGetValue(def.category, out Cell q))
                {
                    c.hasQuest = q.has;
                    c.questValue = q.value;
                    c.questValueText = FormatValue(q.value, q.has, def.isMB);
                    c.questRating = q.rating;
                }
                m.categories.Add(c);
            }
        }

        private static bool ReadStat(AvatarPerformanceStats s, AvatarPerformanceCategory category, out float value)
        {
            switch (category)
            {
                case AvatarPerformanceCategory.DownloadSize: return FromBytesMB(s.downloadSizeBytes, out value);
                case AvatarPerformanceCategory.PolyCount: return FromI(s.polyCount, out value);
                case AvatarPerformanceCategory.SkinnedMeshCount: return FromI(s.skinnedMeshCount, out value);
                case AvatarPerformanceCategory.MeshCount: return FromI(s.meshCount, out value);
                case AvatarPerformanceCategory.MaterialCount: return FromI(s.materialCount, out value);
                case AvatarPerformanceCategory.TextureMegabytes: return FromF(s.textureMegabytes, out value);
                case AvatarPerformanceCategory.BoneCount: return FromI(s.boneCount, out value);
                case AvatarPerformanceCategory.PhysBoneComponentCount: return FromI(s.physBone?.componentCount, out value);
                case AvatarPerformanceCategory.PhysBoneTransformCount: return FromI(s.physBone?.transformCount, out value);
                case AvatarPerformanceCategory.PhysBoneColliderCount: return FromI(s.physBone?.colliderCount, out value);
                case AvatarPerformanceCategory.PhysBoneCollisionCheckCount: return FromI(s.physBone?.collisionCheckCount, out value);
                case AvatarPerformanceCategory.ContactCount: return FromI(s.contactCount, out value);
                case AvatarPerformanceCategory.ConstraintsCount: return FromI(s.constraintsCount, out value);
                case AvatarPerformanceCategory.AnimatorCount: return FromI(s.animatorCount, out value);
                case AvatarPerformanceCategory.ParticleSystemCount: return FromI(s.particleSystemCount, out value);
                default: value = 0f; return false;
            }
        }

        private static bool FromI(int? src, out float value)
        {
            value = src.HasValue ? src.Value : 0f;
            return src.HasValue;
        }

        private static bool FromF(float? src, out float value)
        {
            value = src.HasValue ? src.Value : 0f;
            return src.HasValue;
        }

        /// <summary>バイト数(int?)を MB(float)へ。SDKのダウンロードサイズは downloadSizeBytes を正とする。</summary>
        private static bool FromBytesMB(int? bytes, out float value)
        {
            value = bytes.HasValue ? bytes.Value / (1024f * 1024f) : 0f;
            return bytes.HasValue;
        }

        private static string FormatValue(float value, bool hasValue, bool isMB)
        {
            if (!hasValue) return "-";
            return isMB
                ? value.ToString("F1", CultureInfo.InvariantCulture) + " MB"
                : value.ToString("N0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// VRCConstraintManager.Sdk_ManuallyRefreshGroups をリフレクションで呼ぶ(内部型のため直接参照不可)。
        /// AvatarStudioDiagnostics.RefreshConstraintGroups と同手順の写経。見つからなければ続行。
        /// </summary>
        private static void RefreshConstraintGroups(GameObject root)
        {
            var constraints = root.GetComponentsInChildren<VRC.Dynamics.VRCConstraintBase>(true);
            if (constraints == null || constraints.Length == 0) return;

            Type managerType = QuestCompat.FindType("VRC.Dynamics.VRCConstraintManager");
            MethodInfo method = managerType != null
                ? managerType.GetMethod("Sdk_ManuallyRefreshGroups",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new Type[] { typeof(VRC.Dynamics.VRCConstraintBase[]) }, null)
                : null;
            if (method == null) return;
            try { method.Invoke(null, new object[] { constraints }); }
            catch (Exception ex) { Debug.LogWarning("[RARA AvatarStudio] コンストレイントグループ更新に失敗: " + ex.Message); }
        }
    }

    // ============================================================
    // 6) 実測レポートウィンドウ(IMGUI)
    // ============================================================

    /// <summary>アバターごとの実測結果(総合/項目別/目標比較/元との差/実測ビルドサイズ)を並べるレポート。</summary>
    public class AvatarStudioMeasureReportWindow : EditorWindow
    {
        private const float HardDownloadCapMB = 10f; // Quest/Android のアップロード上限(圧縮後ダウンロードサイズ)
        private const float HardUncompressedCapMB = 40f; // Quest/Android のアップロード上限(展開後・非圧縮)。QuestLimits.HardUncompressedSizeCapMB と同値

        private static bool _shownThisSession;
        private static AvatarStudioMeasureReportWindow _instance;

        private Vector2 _scroll;
        // 目標比較のためにシーンからアバター設定を引いた結果のキャッシュ(Layout 時のみ更新)。
        private Dictionary<string, GoalInfo> _goalCache;
        private double _nextGoalRefresh;

        private struct GoalInfo
        {
            public bool hasSettings;
            public int pcGoalIndex;    // 0=Excellent..3=Poor
            public int questGoalIndex;
        }

        public static bool HasOpenInstance => _instance != null;

        // ------------------------------------------------------------
        // 開く
        // ------------------------------------------------------------

        [MenuItem("RARA/実測レポート", priority = 110)]
        public static void Open()
        {
            var w = GetWindow<AvatarStudioMeasureReportWindow>(false, "実測レポート", true);
            w.titleContent = new GUIContent("RARA 実測レポート");
            w.minSize = new Vector2(520f, 420f);
            w.Show();
        }

        /// <summary>フック計測後に呼ばれる。▶️/ビルドごとに1回だけ、フォーカスを奪わずに開く。</summary>
        public static void ShowIfEnabled()
        {
            if (!AvatarStudioMeasurePrefs.ShowOnPlay) return;
            if (_shownThisSession) return;
            _shownThisSession = true;

            // focus:false で Game ビューのフォーカスを奪わない。
            var w = GetWindow<AvatarStudioMeasureReportWindow>(false, "実測レポート", false);
            w.titleContent = new GUIContent("RARA 実測レポート");
            w.minSize = new Vector2(520f, 420f);
            w._goalCache = null; // 次の Layout で目標を取り直す
            w.Repaint();
        }

        /// <summary>
        /// 即時実測(ビルド不要)後にレポートを前面へ出す。force=false のときは「▶️/ビルドのたびに表示」設定に従う
        /// (オフなら開いているときだけ再描画する)。Playセッション1回制限には縛られない。
        /// </summary>
        public static void ShowForInstantMeasure(bool force)
        {
            if (!force && !AvatarStudioMeasurePrefs.ShowOnPlay)
            {
                RepaintIfOpen();
                return;
            }
            // focus:false で作業中のフォーカスを奪わない。
            var w = GetWindow<AvatarStudioMeasureReportWindow>(false, "実測レポート", false);
            w.titleContent = new GUIContent("RARA 実測レポート");
            w.minSize = new Vector2(520f, 420f);
            w._goalCache = null; // 次の Layout で目標を取り直す
            w.Repaint();
        }

        /// <summary>編集モードへ戻ったら「今回のPlayセッションでの表示済み」フラグをリセットする。</summary>
        public static void ResetPlaySession()
        {
            _shownThisSession = false;
        }

        public static void RepaintIfOpen()
        {
            if (_instance != null) _instance.Repaint();
        }

        private void OnEnable()
        {
            _instance = this;
            _goalCache = null;
        }

        private void OnDisable()
        {
            if (_instance == this) _instance = null;
        }

        // ------------------------------------------------------------
        // 描画
        // ------------------------------------------------------------

        private void OnGUI()
        {
            AvatarStudioUI.EnsureStyles();

            // IMGUI規約: 条件分岐の前にトグル値・キャッシュ・Playモードを一度だけ捕捉する。
            bool enabled = AvatarStudioMeasurePrefs.Enabled;
            bool showOnPlay = AvatarStudioMeasurePrefs.ShowOnPlay;
            bool allAvatars = AvatarStudioMeasurePrefs.MeasureAllAvatars;
            bool isPlaying = EditorApplication.isPlayingOrWillChangePlaymode;
            AvatarStudioMeasureData data = AvatarStudioMeasureStore.Data;

            // 目標キャッシュは Layout イベント時のみ更新(時間スロットル)。
            if (Event.current.type == EventType.Layout &&
                (_goalCache == null || EditorApplication.timeSinceStartup >= _nextGoalRefresh))
            {
                RefreshGoalCache(data);
                _nextGoalRefresh = EditorApplication.timeSinceStartup + 2.0;
            }

            using (var sv = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = sv.scrollPosition;

                DrawHeader();
                DrawSettingsBar(enabled, showOnPlay, allAvatars, isPlaying);

                if (data.avatars == null || data.avatars.Count == 0)
                {
                    EditorGUILayout.Space(4f);
                    EditorGUILayout.HelpBox(
                        "まだ実測結果がありません。\n"
                        + "生成した _Opt / _Quest 複製を ▶️(Play)するか、VRChat SDK で Build & Test / Upload すると、"
                        + "最終的な複製そのものを実測してここに表示します。",
                        MessageType.Info);
                    return;
                }

                foreach (MeasuredAvatar a in data.avatars)
                {
                    if (a == null) continue;
                    DrawAvatarCard(a);
                    EditorGUILayout.Space(6f);
                }
            }
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("RARA 実測レポート", AvatarStudioUI.TitleLabel);
            EditorGUILayout.LabelField(
                "「見積り」ではなく、ビルド/Play時の“最終的な複製そのもの”を VRChat SDK と同じ計算で実測した結果です。"
                + "MA/AAO/lilToon 等の適用後・EditorOnly 除去後の姿を測ります。",
                AvatarStudioUI.PurposeLabel);
            EditorGUILayout.Space(2f);
        }

        private void DrawSettingsBar(bool enabled, bool showOnPlay, bool allAvatars, bool isPlaying)
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                bool newEnabled = EditorGUILayout.ToggleLeft(
                    new GUIContent("ビルド/Play時に実測する",
                        "オフにすると計測しません(ビルドやPlayの動作自体には影響しません)"),
                    enabled);
                if (newEnabled != enabled) AvatarStudioMeasurePrefs.Enabled = newEnabled;

                using (new EditorGUI.DisabledScope(!newEnabled))
                {
                    bool newShow = EditorGUILayout.ToggleLeft(
                        new GUIContent("▶️(Play)・ビルドのたびに実測レポートを表示する",
                            "計測のたびにこのウィンドウを(フォーカスを奪わずに)前面へ出します。1回のPlay/ビルドにつき1回"),
                        showOnPlay);
                    if (newShow != showOnPlay) AvatarStudioMeasurePrefs.ShowOnPlay = newShow;

                    bool newAll = EditorGUILayout.ToggleLeft(
                        new GUIContent("_Opt / _Quest 以外のアバターも計測する",
                            "既定では名前が _Opt / _Quest で終わる複製だけを測ります。オンにすると全アバターを測ります"),
                        allAvatars);
                    if (newAll != allAvatars) AvatarStudioMeasurePrefs.MeasureAllAvatars = newAll;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("再計測",
                        isPlaying
                            ? "Play中はビルド計測が権威です。もう一度 ▶️ し直すと再計測されます"
                            : "シーン上のアバター(_Opt/_Quest、全アバターがオンなら全て)をスタジオ診断で測り直します"),
                        GUILayout.Width(90f)))
                    {
                        if (isPlaying)
                        {
                            EditorUtility.DisplayDialog("再計測",
                                "Play中の実測は、もう一度 ▶️(Play)し直すと最新の複製で計測されます。"
                                + "編集モードでの再計測はスタジオ診断で行います。", "OK");
                        }
                        else
                        {
                            RemeasureInEditMode();
                        }
                    }

                    using (new EditorGUI.DisabledScope(isPlaying))
                    {
                        if (GUILayout.Button(new GUIContent("今すぐ実測(ビルド不要)",
                            "選択中(またはシーン上)の _Opt/_Quest 複製を、ビルド時の姿(NDMF手動ベイク)で実測します。"
                            + "Play も SDK ビルドも不要です"),
                            GUILayout.Width(150f)))
                        {
                            RunInstantMeasureFromButton();
                        }
                    }

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button(new GUIContent("結果をクリア"), GUILayout.Width(100f)))
                    {
                        if (EditorUtility.DisplayDialog("実測レポート",
                            "保存されている実測結果をすべて消去します。よろしいですか?", "消去", "キャンセル"))
                        {
                            AvatarStudioMeasureStore.Clear();
                            _goalCache = null;
                        }
                    }
                }

                if (isPlaying)
                {
                    EditorGUILayout.LabelField("Play中(実ビルドと同じ計算で実測中)。", AvatarStudioUI.MiniWrapLabel);
                }
            }
        }

        // ------------------------------------------------------------
        // アバター1件のカード
        // ------------------------------------------------------------

        private void DrawAvatarCard(MeasuredAvatar a)
        {
            bool isQuest = a.cloneType == "_Quest";
            bool authoritativeIsQuest = a.authoritativeBasis == "Quest";
            MeasuredAvatar source = FindSource(a);

            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                // 見出し行: 名前 / 種別 / 実測時刻 / 権威基準。
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(a.name, EditorStyles.boldLabel, GUILayout.MinWidth(160f));
                    string kind = string.IsNullOrEmpty(a.cloneType) ? "(元アバター)" : a.cloneType;
                    EditorGUILayout.LabelField(kind, EditorStyles.miniLabel, GUILayout.Width(90f));
                    GUILayout.FlexibleSpace();
                    string measureLabel = a.instantBake
                        ? "実測(手動ベイク・ビルド不要)"
                        : (a.fromBuild ? "実測" : "診断");
                    EditorGUILayout.LabelField(
                        measureLabel + " " + FormatTime(a.measuredAtUtc),
                        EditorStyles.miniLabel, GUILayout.Width(a.instantBake ? 220f : 150f));
                }

                // 総合ランク行(PC / Quest)。権威基準を太字強調。
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("総合", EditorStyles.miniBoldLabel, GUILayout.Width(40f));
                    if (a.measuredPc) DrawOverall("PC", a.pcOverall, !authoritativeIsQuest);
                    if (a.measuredQuest) DrawOverall("Quest", a.questOverall, authoritativeIsQuest);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField("権威基準: " + a.authoritativeBasis,
                        EditorStyles.miniLabel, GUILayout.Width(110f));
                }

                DrawGoalLine(a);
                DrawBuildSizeLine(a, isQuest);

                // 即時実測(手動ベイク)の正直な注記。
                if (a.instantBake)
                {
                    EditorGUILayout.LabelField(
                        "※ ビルド不要の手動ベイク(NDMF)による実測です。ダウンロードサイズは推定値のまま"
                        + "(実ファイルサイズはSDKビルド/アップロード時のみ確定します)。lilToon等のSDKビルド固有処理"
                        + "(コールバック)は含まれません。",
                        AvatarStudioUI.MiniWrapLabel);
                }

                // 項目別テーブル。
                DrawCategoryTable(a, source);
            }
        }

        private static void DrawOverall(string label, string rating, bool authoritative)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(38f));
            Color prev = GUI.color;
            GUI.color = AvatarStudioDiagnostics.RatingColor(rating);
            var style = authoritative ? EditorStyles.boldLabel : EditorStyles.miniBoldLabel;
            EditorGUILayout.LabelField(AvatarStudioDiagnostics.DisplayRating(rating), style, GUILayout.Width(84f));
            GUI.color = prev;
        }

        /// <summary>
        /// 目標比較・元との差で使う基準を選ぶ。_Opt は PC 基準、_Quest は Quest 基準、
        /// 元(種別なし)はアクティブビルドターゲットに一致する権威基準に従う。
        /// </summary>
        private static bool UseQuestBasis(MeasuredAvatar a)
        {
            if (a.cloneType == "_Quest") return true;
            if (a.cloneType == "_Opt") return false;
            return a.authoritativeBasis == "Quest";
        }

        private void DrawGoalLine(MeasuredAvatar a)
        {
            if (_goalCache == null || !_goalCache.TryGetValue(a.name, out GoalInfo goal) || !goal.hasSettings) return;

            bool useQuest = UseQuestBasis(a);
            // 種別に応じた基準の総合ランクを目標と突き合わせる。
            string overall = useQuest ? a.questOverall : a.pcOverall;
            int goalIndex = useQuest ? goal.questGoalIndex : goal.pcGoalIndex;
            if (string.IsNullOrEmpty(overall)) return;

            bool over = AvatarStudioDiagnostics.IsOverGoal(overall, goalIndex);
            string goalName = GoalName(goalIndex);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("目標", EditorStyles.miniBoldLabel, GUILayout.Width(40f));
                Color prev = GUI.color;
                GUI.color = over ? AvatarStudioUI.OverLimitColor : AvatarStudioUI.UploadOkColor;
                EditorGUILayout.LabelField(
                    (over ? "✕ 目標未達" : "✓ 目標達成") + "(目標 " + goalName + " / "
                    + (useQuest ? "Quest" : "PC") + "総合 " + AvatarStudioDiagnostics.DisplayRating(overall) + ")",
                    AvatarStudioUI.MiniWrapLabel);
                GUI.color = prev;
            }
        }

        private void DrawBuildSizeLine(MeasuredAvatar a, bool isQuest)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("実測ビルドサイズ", EditorStyles.miniBoldLabel, GUILayout.Width(110f));
                if (a.buildSizeBytes < 0)
                {
                    EditorGUILayout.LabelField(
                        "未取得(SDKの Build & Test / Upload で生成すると実測されます)",
                        AvatarStudioUI.MiniWrapLabel);
                    return;
                }

                // 圧縮後(オンディスク)ダウンロードサイズ。Quest は10MB判定。
                float mb = a.buildSizeBytes / (1024f * 1024f);
                Color prev = GUI.color;
                bool overCap = isQuest && mb > HardDownloadCapMB;
                if (isQuest) GUI.color = overCap ? AvatarStudioUI.OverLimitColor : AvatarStudioUI.UploadOkColor;
                string verdict = isQuest
                    ? (overCap ? "  ⚠ 圧縮後10MB超過(アップロード不可)" : "  ✓ 圧縮後10MB以内")
                    : string.Empty;
                EditorGUILayout.LabelField("圧縮後 " + mb.ToString("F2", CultureInfo.InvariantCulture) + " MB" + verdict,
                    EditorStyles.miniLabel);
                GUI.color = prev;
            }

            // 展開後(非圧縮)サイズ。Quest のみ40MB判定。SDK API で実測できた場合は実測値、
            // できない場合は未取得(推定はサイズ診断側で表示)。
            if (isQuest)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("展開後(非圧縮)", EditorStyles.miniBoldLabel, GUILayout.Width(110f));
                    if (a.uncompressedBuildSizeBytes < 0)
                    {
                        EditorGUILayout.LabelField(
                            "未取得(SDKビルド時のみ実測。推定はサイズ診断を参照。上限 " + HardUncompressedCapMB.ToString("F0") + "MB)",
                            AvatarStudioUI.MiniWrapLabel);
                    }
                    else
                    {
                        float umb = a.uncompressedBuildSizeBytes / (1024f * 1024f);
                        bool overU = umb > HardUncompressedCapMB;
                        Color prev = GUI.color;
                        GUI.color = overU ? AvatarStudioUI.OverLimitColor : AvatarStudioUI.UploadOkColor;
                        string verdict = overU ? "  ⚠ 40MB超過(アップロード不可)" : "  ✓ 40MB以内";
                        EditorGUILayout.LabelField("実測 " + umb.ToString("F2", CultureInfo.InvariantCulture) + " MB" + verdict,
                            EditorStyles.miniLabel);
                        GUI.color = prev;
                    }
                }
            }
        }

        private void DrawCategoryTable(MeasuredAvatar a, MeasuredAvatar source)
        {
            if (a.categories == null || a.categories.Count == 0) return;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("項目", EditorStyles.miniBoldLabel, GUILayout.MinWidth(150f));
                EditorGUILayout.LabelField("PC", EditorStyles.miniBoldLabel, GUILayout.Width(96f));
                EditorGUILayout.LabelField("Quest", EditorStyles.miniBoldLabel, GUILayout.Width(96f));
                EditorGUILayout.LabelField("元との差", EditorStyles.miniBoldLabel, GUILayout.Width(96f));
                GUILayout.FlexibleSpace();
            }

            bool useQuest = UseQuestBasis(a);
            foreach (MeasuredCategory c in a.categories)
            {
                if (c == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(c.label), EditorStyles.miniLabel, GUILayout.MinWidth(150f));
                    DrawValueCell(c.pcValueText, c.pcRating, GUILayout.Width(96f));
                    DrawValueCell(c.questValueText, c.questRating, GUILayout.Width(96f));

                    // 元との差(種別に応じた基準の数値で比較)。
                    string deltaText = ComputeDeltaText(a, source, c, useQuest, out Color deltaColor);
                    Color prev = GUI.color;
                    GUI.color = deltaColor;
                    EditorGUILayout.LabelField(deltaText, EditorStyles.miniLabel, GUILayout.Width(96f));
                    GUI.color = prev;

                    GUILayout.FlexibleSpace();
                }
            }
        }

        private static void DrawValueCell(string text, string rating, GUILayoutOption width)
        {
            Color prev = GUI.color;
            GUI.color = AvatarStudioDiagnostics.RatingColor(rating);
            EditorGUILayout.LabelField(string.IsNullOrEmpty(text) ? "-" : text, EditorStyles.miniLabel, width);
            GUI.color = prev;
        }

        private static string ComputeDeltaText(MeasuredAvatar clone, MeasuredAvatar source, MeasuredCategory c,
            bool useQuest, out Color color)
        {
            color = GUI.color;
            if (source == null) return "-";

            MeasuredCategory sc = FindCategory(source, c.label);
            if (sc == null) return "-";

            bool cloneHas = useQuest ? c.hasQuest : c.hasPc;
            bool srcHas = useQuest ? sc.hasQuest : sc.hasPc;
            if (!cloneHas || !srcHas) return "-";

            float cv = useQuest ? c.questValue : c.pcValue;
            float sv = useQuest ? sc.questValue : sc.pcValue;
            float d = cv - sv;
            if (Mathf.Abs(d) < 0.05f) { color = GUI.color; return "±0"; }

            // 減少(軽量化)は緑、増加は赤で示す。
            color = d < 0 ? AvatarStudioUI.UploadOkColor : AvatarStudioUI.OverLimitColor;
            string num = c.isMB
                ? (d > 0 ? "+" : "") + d.ToString("F1", CultureInfo.InvariantCulture) + " MB"
                : (d > 0 ? "+" : "") + d.ToString("N0", CultureInfo.InvariantCulture);
            return num;
        }

        // ------------------------------------------------------------
        // 補助
        // ------------------------------------------------------------

        /// <summary>クローンに対応する「元アバター」の実測(名前ベース: {base}_Opt → {base})を探す。</summary>
        private static MeasuredAvatar FindSource(MeasuredAvatar clone)
        {
            if (clone == null || string.IsNullOrEmpty(clone.cloneType)) return null;
            string baseName = clone.name.Substring(0, clone.name.Length - clone.cloneType.Length);
            if (baseName.Length == 0) return null;
            return AvatarStudioMeasureStore.Find(baseName);
        }

        private static MeasuredCategory FindCategory(MeasuredAvatar a, string label)
        {
            if (a == null || a.categories == null) return null;
            foreach (MeasuredCategory c in a.categories)
            {
                if (c != null && string.Equals(c.label, label, StringComparison.Ordinal)) return c;
            }
            return null;
        }

        private static string GoalName(int goalIndex)
        {
            string[] names = AvatarStudioDiagnostics.GoalRankNames;
            return (goalIndex >= 0 && goalIndex < names.Length) ? names[goalIndex] : "?";
        }

        private static string FormatTime(double utcTicks)
        {
            if (utcTicks <= 0) return string.Empty;
            try { return new DateTime((long)utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss"); }
            catch { return string.Empty; }
        }

        /// <summary>
        /// レポート内の各アバターについて、対応する「元アバター」の保存済み設定から目標ランクを引く。
        /// シーン走査を含むため Layout イベント時のみ・時間スロットルで呼ぶ。
        /// </summary>
        private void RefreshGoalCache(AvatarStudioMeasureData data)
        {
            var cache = new Dictionary<string, GoalInfo>(StringComparer.Ordinal);
            if (data != null && data.avatars != null && data.avatars.Count > 0)
            {
                // シーン上の VRCAvatarDescriptor を名前→GameObject で引けるようにする。
                var byName = new Dictionary<string, GameObject>(StringComparer.Ordinal);
                foreach (VRCAvatarDescriptor d in UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true))
                {
                    if (d == null || EditorUtility.IsPersistent(d)) continue;
                    string n = AvatarStudioMeasureUtil.StripCloneSuffix(d.gameObject.name);
                    if (!byName.ContainsKey(n)) byName[n] = d.gameObject;
                }

                foreach (MeasuredAvatar a in data.avatars)
                {
                    if (a == null || string.IsNullOrEmpty(a.name)) continue;

                    // 設定は「元アバター名」から引く(_Opt/_Quest を外した基底名。元自身ならそのまま)。
                    string baseName = string.IsNullOrEmpty(a.cloneType)
                        ? a.name
                        : a.name.Substring(0, a.name.Length - a.cloneType.Length);

                    GameObject src = null;
                    if (baseName.Length > 0) byName.TryGetValue(baseName, out src);

                    AvatarStudioSettings settings = src != null
                        ? AvatarStudioSettingsIO.LoadSaved(src)
                        : null;

                    var info = new GoalInfo { hasSettings = settings != null };
                    if (settings != null)
                    {
                        info.pcGoalIndex = (int)settings.pcTargetRank;
                        info.questGoalIndex = settings.questGoalRank;
                    }
                    cache[a.name] = info;
                }
            }
            _goalCache = cache;
        }

        /// <summary>編集モードでの再計測。シーン上の対象アバターをスタジオ診断で測り直してストアへ反映する。</summary>
        private void RemeasureInEditMode()
        {
            int count = 0;
            try
            {
                bool all = AvatarStudioMeasurePrefs.MeasureAllAvatars;
                foreach (VRCAvatarDescriptor d in UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true))
                {
                    if (d == null || EditorUtility.IsPersistent(d)) continue;
                    string name = AvatarStudioMeasureUtil.StripCloneSuffix(d.gameObject.name);
                    string cloneType = AvatarStudioMeasureUtil.DetectCloneType(name);
                    if (cloneType.Length == 0 && !all) continue;

                    MeasuredAvatar m = AvatarStudioMeasureUtil.MeasureViaStudio(d);
                    if (m != null) { AvatarStudioMeasureStore.Record(m); count++; }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            _goalCache = null;
            if (count == 0)
            {
                EditorUtility.DisplayDialog("再計測",
                    "シーンに計測対象のアバターが見つかりませんでした。"
                    + "_Opt / _Quest 複製をシーンに置くか、上の「_Opt / _Quest 以外も計測する」をオンにしてください。", "OK");
            }
            Repaint();
        }

        /// <summary>
        /// 「今すぐ実測(ビルド不要)」ボタン。選択中(なければシーン上)の対象複製を NDMF 手動ベイクで即時実測する。
        /// 対象ゲート(_Opt/_Quest、全アバターがオンなら全て)はビルドフックと同じ。
        /// </summary>
        private void RunInstantMeasureFromButton()
        {
            if (!InstantMeasure.IsNdmfAvailable)
            {
                EditorUtility.DisplayDialog("今すぐ実測(ビルド不要)",
                    "NDMFが導入されていないため、ビルド不要の即時実測はできません。"
                    + "NDMFを導入するか、_Opt / _Quest 複製を ▶️(Play)/ SDKビルドすると実測されます。", "OK");
                return;
            }

            bool all = AvatarStudioMeasurePrefs.MeasureAllAvatars;
            var targets = new List<VRCAvatarDescriptor>();

            // まず選択中のアバター(シーン実体のみ)を対象にする。
            foreach (GameObject sel in Selection.gameObjects)
            {
                if (sel == null) continue;
                VRCAvatarDescriptor d = sel.GetComponent<VRCAvatarDescriptor>();
                if (d == null || EditorUtility.IsPersistent(d)) continue;
                if (InScope(d, all) && !targets.Contains(d)) targets.Add(d);
            }
            // 選択が対象外/空ならシーン全体から集める。
            if (targets.Count == 0)
            {
                foreach (VRCAvatarDescriptor d in UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true))
                {
                    if (d == null || EditorUtility.IsPersistent(d)) continue;
                    if (InScope(d, all) && !targets.Contains(d)) targets.Add(d);
                }
            }

            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog("今すぐ実測(ビルド不要)",
                    "実測対象のアバターが見つかりませんでした。_Opt / _Quest 複製を選択するかシーンに置いてください"
                    + "(それ以外も測るには「_Opt / _Quest 以外のアバターも計測する」をオンに)。", "OK");
                return;
            }

            int ok = 0;
            string lastError = string.Empty;
            foreach (VRCAvatarDescriptor d in targets)
            {
                if (InstantMeasure.MeasureManual(d.gameObject, out string msg)) ok++;
                else if (!string.IsNullOrEmpty(msg)) lastError = msg;
            }

            _goalCache = null;
            if (ok == 0)
            {
                EditorUtility.DisplayDialog("今すぐ実測(ビルド不要)",
                    "実測できませんでした。" + (string.IsNullOrEmpty(lastError) ? string.Empty : "\n" + lastError), "OK");
            }
            Repaint();
        }

        /// <summary>ビルドフックと同じ対象ゲート: 名前が _Opt/_Quest で終わる、または全アバター計測がオン。</summary>
        private static bool InScope(VRCAvatarDescriptor d, bool all)
        {
            string name = AvatarStudioMeasureUtil.StripCloneSuffix(d.gameObject.name);
            string cloneType = AvatarStudioMeasureUtil.DetectCloneType(name);
            return cloneType.Length > 0 || all;
        }
    }
}
#endif
