# RARA Avatar Tools (アバター軽量化・Quest/iOS対応)

## 概要

VRChatアバターの軽量化とQuest/iOS対応を支援するUnityエディタ拡張ツールです。すべての操作は1つの統合ウィンドウ(Avatar Studio)で行います。Unity のメニューには次の2項目だけが追加されます。

- RARA / PC軽量化ツール: 統合ウィンドウを PC 対象で開く(ウィンドウ内で Quest にも切替可)
- RARA / Quest対応ツール: 統合ウィンドウを Quest 対象で開く(ウィンドウ内で PC にも切替可)

内部的には PC最適化エンジン(PC Optimizer)と Quest/iOS変換エンジン(Quest Converter)を共有しています。各機能の詳細は Docs フォルダを参照してください。

## AAO（Avatar Optimizer）との連携について

本ツールは anatawa12 氏の AAO: Avatar Optimizer と処理を分業する設計です。実行時は、生成した複製にのみ次のAAOコンポーネントを自動で追加・設定します（元アバターには一切追加しません）。

- Trace and Optimize … アバターを走査して自動でできる限りの最適化を行うコンポーネント（未使用ボーンの削減や自動マージなど）。追加の自動最適化のために付与します（下記の Merge Skinned Mesh / Remove Mesh By BlendShape は Trace and Optimize が無くてもビルド時にAAOが適用します）。
- Merge Skinned Mesh … 顔以外の複数の SkinnedMeshRenderer を1つの SkinnedMeshRenderer へ統合します（「SkinnedMesh統合」の実体）。統合時は BlendShape 名を自動変更して重複を避けます。
- Remove Mesh By BlendShape … 指定した BlendShape で動く頂点とポリゴンを削除します（服の下に隠れる肌などを消す「隠れメッシュ削減」の実体）。

適用タイミング: これらの統合・削除は、アップロード/Play時のビルド（NDMF）で実行されます。そのため、生成直後の複製やエディタ上の診断値には反映されず、実際のアップロード後にさらに軽くなります。最終的な数値は VRChat SDK のビルド結果で確認してください。

AAO未導入時の挙動: AAO が無くても本ツール自体は動作します。AAO を使う機能（SkinnedMesh統合・隠れメッシュ削減など）は自動でスキップされ、ウィンドウ内に導入の案内が表示されます。導入は次のリポジトリを VCC / ALCOM に追加して行います。

- 導入リポジトリ: https://vpm.anatawa12.com/add-repo
- 公式ドキュメント: https://vpm.anatawa12.com/avatar-optimizer/ja/

謝辞: AAO: Avatar Optimizer（MIT License / anatawa12 氏）に感謝します。あわせて VRChat SDK・lilToon・NonToon・Poiyomi・Modular Avatar / NDMF にも感謝します。

## 導入（VCC / ALCOM）

1. 追加ページ https://rara-vrc.github.io/rara-avatar-tools/ を開き、「VCC / ALCOM に追加」ボタンを押します。VCC（VRChat Creator Companion）または ALCOM が起動し、リポジトリの追加確認画面が開きます。
2. 手動で追加する場合は、VCC / ALCOM の Settings > Packages > Add Repository に次の URL を貼り付けます。
   - https://rara-vrc.github.io/rara-avatar-tools/index.json
3. 対象のアバタープロジェクトを VCC / ALCOM で開き、Manage Project の一覧から RARA Avatar Tools を追加します。

### 依存ツール（AAO の導入推奨）

本ツール群は Avatar Optimizer（AAO / com.anatawa12.avatar-optimizer）と併用する前提の機能を含みます。未導入の場合は、次のリポジトリを VCC / ALCOM に追加して AAO を導入してください。

- https://vpm.anatawa12.com/add-repo

## トラブルシューティング

Quest/iOS変換後に、見えるべきものが消えた、または消えるべきものが見えている場合は、統合ウィンドウの⑥マテリアル(Questパネル)で該当マテリアルの変換方法を選び直して再生成してください(見せたい場合は Toon Standard(不透明)や乗算・加算、消したい場合は 非表示)。急ぎの場合は、生成された _Quest 複製のマテリアルのシェーダーを Inspector で直接 VRChat/Mobile/Toon Standard などの Quest対応シェーダーへ変更しても表示できます。ただし再生成すると生成マテリアルは同じ場所へ上書きされ、この手動変更は失われるため、恒久的にはパネルでの変換方法指定をおすすめします。Quest上では VRChat/Mobile 系のシェーダーだけが正しく表示され、それ以外はフォールバックします。アップロード前に Build & Test で実機相当の見え方を確認してください。

## 利用規約

本パッケージの利用規約はMITライセンスに準じます。詳細は同梱の LICENSE を参照してください。

## リンク

- ドキュメント: https://github.com/RARA-vrc/rara-avatar-tools
- 変更履歴: https://github.com/RARA-vrc/rara-avatar-tools
