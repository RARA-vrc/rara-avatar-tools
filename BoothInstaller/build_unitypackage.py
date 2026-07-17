#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
RARA Avatar Tools - Booth 案内 unitypackage ビルダー

.unitypackage の中身は「gzip 圧縮された tar」で、
各アセットは 32桁の16進 GUID を名前とするフォルダに格納される:

    <guid>/pathname     … プロジェクト相対パス(テキスト)
    <guid>/asset.meta   … .meta のテキスト
    <guid>/asset        … 実ファイルのバイト列(フォルダアセットでは省略)

このスクリプトは案内用ガイド(フォルダ2つ + .cs 1つ)だけを含む
    RARA-AvatarTools-VCC-Guide.unitypackage
を生成する。GUID は再現性のため固定値をハードコードしている。

使い方:
    python build_unitypackage.py
"""

import gzip
import io
import os
import sys
import tarfile

# ─────────────────────────────────────────────────────────────────────────
# パス設定(このスクリプトのある場所を基準にする)
# ─────────────────────────────────────────────────────────────────────────
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
SOURCE_ROOT = os.path.join(SCRIPT_DIR, "Source")  # Source/Assets/... を含む
OUTPUT_PATH = os.path.join(SCRIPT_DIR, "RARA-AvatarTools-VCC-Guide.unitypackage")

# プロジェクト相対のフォルダ名(日本語)
GUIDE_DIR = "Assets/RARA_導入ガイド"
EDITOR_DIR = "Assets/RARA_導入ガイド/Editor"
CS_PATH = "Assets/RARA_導入ガイド/Editor/RARABoothGuideWindow.cs"

# 実ファイル(.cs)の在り処
CS_SOURCE_FILE = os.path.join(SOURCE_ROOT, "Assets", "RARA_導入ガイド", "Editor",
                              "RARABoothGuideWindow.cs")

# ─────────────────────────────────────────────────────────────────────────
# 固定 GUID(reproducibility のため一度だけ生成してハードコード)
# ─────────────────────────────────────────────────────────────────────────
GUID_GUIDE_FOLDER = "cfb697bae9f3444f8db65ccb2f729ae7"  # Assets/RARA_導入ガイド
GUID_EDITOR_FOLDER = "a836dbdafcf441b991663c53ade27d1a"  # .../Editor
GUID_CS = "e737ef135d0446f0a37c7969c487cf19"             # .../RARABoothGuideWindow.cs

# MonoImporter が参照する MonoScript の内部 fileID(Unity 標準の固定値)
MONO_SCRIPT_FILEID = 11500000

# 再現性のための固定タイムスタンプ(1980-01-01、tar/zip の下限)
FIXED_MTIME = 315532800


# ─────────────────────────────────────────────────────────────────────────
# .meta 生成
# ─────────────────────────────────────────────────────────────────────────
def folder_meta(guid: str) -> str:
    """フォルダアセット用の DefaultImporter meta。"""
    return (
        "fileFormatVersion: 2\n"
        "guid: {guid}\n"
        "folderAsset: yes\n"
        "DefaultImporter:\n"
        "  externalObjects: {{}}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    ).format(guid=guid)


def script_meta(guid: str) -> str:
    """C# スクリプト用の MonoImporter meta。"""
    return (
        "fileFormatVersion: 2\n"
        "guid: {guid}\n"
        "MonoImporter:\n"
        "  externalObjects: {{}}\n"
        "  serializedVersion: 2\n"
        "  defaultReferences: []\n"
        "  executionOrder: 0\n"
        "  icon: {{instanceID: 0}}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    ).format(guid=guid)


# ─────────────────────────────────────────────────────────────────────────
# tar 構築ヘルパ
# ─────────────────────────────────────────────────────────────────────────
def _add_dir(tar: tarfile.TarFile, name: str) -> None:
    ti = tarfile.TarInfo(name)
    ti.type = tarfile.DIRTYPE
    ti.mode = 0o755
    ti.mtime = FIXED_MTIME
    ti.uid = ti.gid = 0
    ti.uname = ti.gname = ""
    tar.addfile(ti)


def _add_file(tar: tarfile.TarFile, name: str, data: bytes) -> None:
    ti = tarfile.TarInfo(name)
    ti.type = tarfile.REGTYPE
    ti.mode = 0o644
    ti.size = len(data)
    ti.mtime = FIXED_MTIME
    ti.uid = ti.gid = 0
    ti.uname = ti.gname = ""
    tar.addfile(ti, io.BytesIO(data))


def add_asset(tar: tarfile.TarFile, guid: str, pathname: str,
              meta_text: str, asset_bytes: bytes = None) -> None:
    """
    1 アセット分(<guid>/ 以下)を tar に追加する。
    asset_bytes を渡した場合のみ <guid>/asset を書き込む(フォルダは None)。
    """
    _add_dir(tar, guid + "/")
    if asset_bytes is not None:
        _add_file(tar, guid + "/asset", asset_bytes)
    _add_file(tar, guid + "/asset.meta", meta_text.encode("utf-8"))
    # pathname は Unity 観測どおり末尾改行なしのプレーンテキスト
    _add_file(tar, guid + "/pathname", pathname.encode("utf-8"))


# ─────────────────────────────────────────────────────────────────────────
# メイン
# ─────────────────────────────────────────────────────────────────────────
def build() -> str:
    if not os.path.isfile(CS_SOURCE_FILE):
        sys.stderr.write("ERROR: source script not found: {}\n".format(CS_SOURCE_FILE))
        sys.exit(1)

    with open(CS_SOURCE_FILE, "rb") as f:
        cs_bytes = f.read()

    # tar を一旦メモリに構築 → mtime=0 の gzip で包む(バイト再現性のため)
    tar_buf = io.BytesIO()
    with tarfile.open(fileobj=tar_buf, mode="w") as tar:
        # フォルダ(親→子の順)
        add_asset(tar, GUID_GUIDE_FOLDER, GUIDE_DIR, folder_meta(GUID_GUIDE_FOLDER))
        add_asset(tar, GUID_EDITOR_FOLDER, EDITOR_DIR, folder_meta(GUID_EDITOR_FOLDER))
        # スクリプト本体
        add_asset(tar, GUID_CS, CS_PATH, script_meta(GUID_CS), cs_bytes)

    tar_bytes = tar_buf.getvalue()

    with open(OUTPUT_PATH, "wb") as out:
        with gzip.GzipFile(filename="archtemp.tar", mode="wb",
                           fileobj=out, mtime=0) as gz:
            gz.write(tar_bytes)

    return OUTPUT_PATH


if __name__ == "__main__":
    path = build()
    size = os.path.getsize(path)
    print("Built: {}".format(path))
    print("Size : {} bytes".format(size))
    print("Assets:")
    print("  [folder] {}  (guid {})".format(GUIDE_DIR, GUID_GUIDE_FOLDER))
    print("  [folder] {}  (guid {})".format(EDITOR_DIR, GUID_EDITOR_FOLDER))
    print("  [script] {}  (guid {})".format(CS_PATH, GUID_CS))
