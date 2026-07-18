# RARA Avatar Tools (アバター軽量化・Quest/iOS対応)

## 概要

VRChatアバターの軽量化とQuest/iOS対応を支援するUnityエディタ拡張ツールです。すべての操作は1つの統合ウィンドウ(Avatar Studio)で行います。Unity のメニューには次の2項目だけが追加されます。

- RARA / PC軽量化ツール: 統合ウィンドウを PC 対象で開く(ウィンドウ内で Quest にも切替可)
- RARA / Quest対応ツール: 統合ウィンドウを Quest 対象で開く(ウィンドウ内で PC にも切替可)

内部的には PC最適化エンジン(PC Optimizer)と Quest/iOS変換エンジン(Quest Converter)を共有しています。各機能の詳細は Docs フォルダを参照してください。

## AAO（Avatar Optimizer）との連携について

本ツールは anatawa12 氏の AAO: Avatar Optimizer と処理を分業する設計です。実行時は、生成した複製にのみ次のAAOコンポーネントを自動で追加・設定します（元アバターには一切追加しません）。

- Trace and Optimize … アバターを走査して自動でできる限りの最適化を行うコンポーネント（未使用ボーンの削減や自動マージなど）。追加の自動最適化のために付与します（下記の Merge Skinned Mesh / Remove Mesh By BlendShape は Trace and Optimize が無くてもビルド時にAAOが適用します）。
- Merge Skinned Mesh … 顔以外の複数の SkinnedMeshRenderer を1つの SkinnedMeshRenderer へ統合します（「SkinnedMesh統合」の実体）。統合時は BlendShape 名を自動変更して重複を避けます。レンダラーをグループ1〜8に振り分けて、まとまりごとに統合するグループ指定モードもあります（顔は常に自動保護）。
- Remove Mesh By BlendShape … 指定した BlendShape で動く頂点とポリゴンを削除します（服の下に隠れる肌などを消す「隠れメッシュ削減」の実体）。

PhysBone のマージ（設定が一致する兄弟チェーンを1本へまとめてコンポーネント数を減らす処理）は本ツール独自の実装で、AAO の Merge PhysBone は使用していません。

適用タイミング: これらの統合・削除は、アップロード/Play時のビルド（NDMF）で実行されます。そのため、生成直後の複製やエディタ上の診断値には反映されず、実際のアップロード後にさらに軽くなります。最終的な数値は VRChat SDK のビルド結果、または本ツールの実測レポート（後述）で確認してください。

AAO未導入時の挙動: AAO が無くても本ツール自体は動作します。AAO を使う機能（SkinnedMesh統合・隠れメッシュ削減など）は自動でスキップされ、ウィンドウ内に導入の案内が表示されます。導入は次のリポジトリを VCC / ALCOM に追加して行います。

- 導入リポジトリ: https://vpm.anatawa12.com/add-repo
- 公式ドキュメント: https://vpm.anatawa12.com/avatar-optimizer/ja/

謝辞: AAO: Avatar Optimizer（MIT License / anatawa12 氏）に感謝します。あわせて VRChat SDK・lilToon・NonToon・Poiyomi・Modular Avatar / NDMF にも感謝します。また、Quest/iOS変換のメッシュ削減(ポリゴン削減)は自作の QEM(Quadric Error Metrics)実装ですが、設計にあたり MIT ライセンスの UnityMeshSimplifier・Meshia を参考にしました。

## NDMFコンソールのAAO警告・最適化ログについて

変換直後の即時実測（1.4.0 以降）や ▶️（Play）・SDK ビルドを行うと、NDMF のコンソールに AAO（Avatar Optimizer）発の警告が出ることがあります。SkinnedMesh統合（Merge Skinned Mesh）まわりで代表的なのは、統合先（統合ターゲット）に Root Bone が設定されていない・統合先に Anchor Override（アンカー上書き）が設定されていない・統合ソース間で同名の BlendShape が違う値を持つ（値不一致）の3種です。いずれも 1.4.0 以前からビルド時には発生していたもので、即時実測で毎回コンソールを通るようになって見えやすくなっただけで、新しい不具合ではありません（PC軽量化・Quest対応のどちらも、本ツールが作る統合先メッシュ RARA_MergedMesh について出ます）。

1.4.1 では、このうち機械的に解消できるものを変換側で自動処理します。統合先（Merge Skinned Mesh のターゲット）へ Root Bone と Anchor Override を自動設定して統合ソースの基準へ揃え、食い違い警告を抑えます。また、アニメーションで使われていない同名 BlendShape の値衝突は、現在の値でメッシュへ焼き込んで自動固定します（現在値のまま焼くので見た目は変わりません）。アニメーションで動かしている BlendShape は勝手に固定すると表情などが壊れるため自動固定せず、変換レポートで該当シェイプを案内します（必要なら手動で調整してください）。

なお、AAO はビルド時に最適化の成果（削減したポリゴン数・統合したメッシュ数など）をコンソールへ出力します。行頭に「!」アイコンが付いて表示されることがありますが、これはエラーではなく AAO による正常な成果報告です。赤字のエラーでなければ変換は成功していますので、そのまま進めて問題ありません。

## Meshia との併用（推奨）

Meshia Mesh Simplification（Ram.Type-0 氏 / MIT）は、NDMF でビルド時（Play / アップロード）にメッシュを高品質に簡略化するツールです。レンダラー単位の `Meshia Mesh Simplifier`、または（Modular Avatar 導入時）アバター単位の `Meshia Cascading Avatar Mesh Simplifier` で、目標三角形数などをレンダラーごとに細かく指定できます。導入は VPM リポジトリ https://ramtype0.github.io/VpmRepository/ を VCC / ALCOM に追加して行います（公式ドキュメント https://ramtype0.github.io/Meshia.MeshSimplification/ ）。

役割分担・併用パターン:

- 品質重視（Meshia を主に）… 崩れやすい/見せ場のモデルは Meshia にレンダラー単位で任せ、EditMode プレビューで品質を追い込みます。本ツールのポリゴン削減はオフのまま（または Meshia を付けたレンダラーは本ツールの対象から外す）にして二重削減を避けます。
- 手早く予算内（本ツールを主に）… 目標ランクへ収めたいだけなら、本ツールのポリゴン削減だけを使います（目標三角形数へ自動配分・顔/髪を保護・_Quest 複製へ即時適用）。Meshia は付けません。ワンショットで完結し、二重削減の心配がない既定運用です。
- 役割分担（併用）… 顔・髪など品質が要る部位だけ Meshia で丁寧に指定し、残りは本ツールの予算配分で目標まで詰めます。同一レンダラーに両方を効かせない（レンダラー単位でどちらか一方へ割り当てる）のが最も安全です。

注意（ビルド時反映・二重削減）: Meshia の削減はビルド時に反映されるため、エディタ/診断のポリゴン数には出ません（EditMode プレビューはありますが、実メッシュの差し替えはビルド時です）。本ツールと Meshia の両方を同じ目標へ強くかけると、ビルド後に目標を大きく下回って削りすぎになります。併用時は、どちらか一方に予算（目標三角形数）を持たせ、本ツールの目標は Meshia 適用後に残る想定数を差し引いて設定してください。⑤ ポリゴン削減パネルは Meshia コンポーネントを検出すると併用注意を表示します。

謝辞: Meshia Mesh Simplification（MIT License / Ram.Type-0 氏）に感謝します。

## 実測レポート（ビルド/Play 時の実測）

AAO や Meshia、Modular Avatar による統合・削減はビルド（Play / アップロード）時に適用されるため、生成直後のエディタ数値には出ません。実測レポートは、その最終的な複製そのものを VRChat SDK と同じ計算で実測し、MA / AAO / Meshia 適用後・EditorOnly 除去後の本当の数値を確認できる機能です。メニューの RARA / 実測レポート から開けます。

- _Opt / _Quest 複製を ▶️（Play）すると、ビルド時と同じ前処理を通した最終複製を実測し、結果がポップアップ表示されます（PC 基準・Quest 基準の両方。総合ランク・項目別の値・目標との達成判定・元アバターとの差を表示）。
- VRChat SDK の Build & Test / Upload でビルドすると、生成されたアセットバンドルの実ファイルサイズも記録されます（Quest/Android は 10MB 上限との判定つき）。エディタの推定ではなく、アップロードされる実バンドルサイズです。
- 既定では名前が _Opt / _Quest で終わる複製だけを計測します。「_Opt / _Quest 以外のアバターも計測する」をオンにすると全アバターを対象にできます。
- 計測を止めたいときは、実測レポートウィンドウ上部の「ビルド/Play時に実測する」をオフにします（ビルドや Play の動作自体には影響しません）。Play/ビルドのたびの自動表示だけを止めたいときは「▶️（Play）・ビルドのたびに実測レポートを表示する」をオフにします。

## オープンベータ（Open β）・不具合報告

本ツールはオープンベータ(Open β)として公開中です。不具合報告・要望は X(Twitter) の DM (https://x.com/RR_vrchat) または メール (raravrchat@gmail.com) へお願いします。

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
