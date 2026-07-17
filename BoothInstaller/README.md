# BoothInstaller — RARA Avatar Tools 案内 unitypackage

Booth で配布する **案内専用の .unitypackage** を生成するためのソースとビルダーです。
生成物にはツール本体は含まれず、購入者を **VCC / ALCOM 経由の導入** に誘導する
エディタ用ガイドウィンドウだけが入っています。

## 中身

```
BoothInstaller/
├─ Source/
│  └─ Assets/RARA_導入ガイド/Editor/RARABoothGuideWindow.cs   ← ガイド本体(依存ゼロ)
├─ build_unitypackage.py         ← .unitypackage ビルダー
├─ RARA-AvatarTools-VCC-Guide.unitypackage  ← 生成物(Booth へアップロード)
├─ BOOTH-PAGE.md                 ← Booth 商品ページの下書き
└─ README.md                     ← このファイル
```

## リビルド方法

`Source/Assets/RARA_導入ガイド/Editor/RARABoothGuideWindow.cs` を編集したら、
以下を実行して `.unitypackage` を再生成します。

```bash
cd BoothInstaller
python build_unitypackage.py
```

- 出力: `BoothInstaller/RARA-AvatarTools-VCC-Guide.unitypackage`
- GUID はスクリプト内に固定値でハードコードしてあり、ビルドは再現性があります
  (同じソースなら同じ内容の .unitypackage が生成されます)
- 追加の依存パッケージは不要です(Python 標準ライブラリのみ)

### 生成物の検証(任意)

`.unitypackage` は「gzip 圧縮された tar」です。中身は次で確認できます。

```bash
tar tzf RARA-AvatarTools-VCC-Guide.unitypackage
```

各アセットは `<32桁GUID>/` フォルダに `pathname` / `asset.meta`(+ ファイルなら `asset`)
の形で格納されます。フォルダアセットには `asset` がありません。

## Booth へのアップロード手順

1. `python build_unitypackage.py` で `.unitypackage` を再生成
2. Booth の商品に **`RARA-AvatarTools-VCC-Guide.unitypackage`** をアップロード
3. 商品説明には **`BOOTH-PAGE.md` の文面** を使用(必要に応じて調整)

## 注意

- この unitypackage は「案内のみ」です。ツール本体は VCC / ALCOM のリポジトリから配信されます。
- 本ツールは **Open β** 公開中です。不具合報告先:
  - X(Twitter)DM: https://x.com/RR_vrchat
  - メール: raravrchat@gmail.com
