# Changelog

## [1.0.4] - 2026-07-17
### Fixed
- アップロード可否の判定と表記を公式仕様に合わせて訂正: アップロードのハードブロックはダウンロードサイズ上限(ビルド後圧縮10MB / 展開後40MB)超過のみで発生し、パフォーマンスランクでは決まりません。Quest 診断の可否フラグ(`canUploadToAndroid` / `questCanUpload`)をサイズ基準へ変更しました。
- Very Poor を「モバイルへアップロード不可」とする誤表記を全面撤去。Very Poor は既定で相手にフォールバック表示(Show Avatar で表示可)、Dynamics 上限超過分野があると揺れ物が全停止、という正確な警告へ差し替えました(公式が将来の表示制限を予告しているため引き続き Poor 以内を推奨)。
- 対象: Quest Converter / Avatar Studio の診断ラベル・ヘルプ・用語解説、および README・Docs・導入ページ。

## [1.0.1] - 2026-07-16
### Fixed
- asmdef参照不足の修正: `jp.rara.avatar-tools.Editor` に `VRC.SDK3A.Editor` 参照を追加。VCCインストール環境で `VRC.SDK3.Avatars.AvatarDynamicsSetup` が解決できずに発生していた `CS0234`（ComponentRemover.cs）を解消しました。

## [1.0.0]
- 初版リリース。Quest Converter・PC Optimizer・Avatar Studio を収録。

## 1.0.5
- ドキュメント冒頭に「AAO(Avatar Optimizer)との連携について」を追加 (使用する3コンポーネント: Trace and Optimize / Merge Skinned Mesh / Remove Mesh By BlendShape の役割・複製にのみ付与・ビルド時適用・未導入時の挙動・anatawa12氏への謝辞)
- ツール内ヘルプにAAO連携の要約と公式ドキュメントを開くボタンを追加

## 1.0.4
- 診断の「Androidアップロード可否」を公式仕様に合わせて修正 (可否はダウンロードサイズのみで判定。Very Poor はアップロード可能で、既定ではフォールバック表示・相手の Show Avatar で表示可・揺れ物は上限超過分野があると全停止・公式が将来の表示制限を予告)
- ツール内・README・導入ページの旧記述「Very Poor はモバイルへアップロード不可」(2023年以前の仕様) をすべて置換

## 1.0.3
- 「見た目がおかしいとき」の注意書きを追加 (マテリアルパネル・変換結果・ドキュメント。見せたい/消したい両方向の対処と、手動シェーダー変更は再生成で上書きされる注意)
- GitHub README と導入ページ (rara-vrc.github.io) に全機能の使用方法マニュアルを掲載 (実装との照合・完全性監査済み)

## 1.0.2
- メニューを1項目に統合 (RARA > アバター軽量化・Quest・iOS対応ツール)
- ヘルプを全面改訂 (軽くなる仕組みの平易な説明、実装とのファクトチェック済み)
- 雑にQuest対応プリセットの透過処理を「自動再現」に変更 (ストッキング等が消えず乗算で透ける)
- 非表示になったマテリアル行に「不透明にする」「乗算で再現」ボタンを追加
- IMGUIの再描画エラー対策、プレビューの1フレーム遅延修正、Docsの導入手順をVPM前提に更新

