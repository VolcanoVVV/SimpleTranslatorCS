"""Build server ZIPs and optionally bilingual client ZIPs from verified Release binaries.

From the repository root: python tools/package_release.py --server-only
The file map is deliberately explicit so local translations, keys and caches
cannot enter a release merely because they exist in a developer's install.
"""

from __future__ import annotations

import hashlib
import importlib.util
import argparse
import re
import xml.etree.ElementTree as ET
from pathlib import Path
from posixpath import dirname, join, normpath
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


ROOT = Path(__file__).resolve().parents[1]
CLIENT = ROOT.parent / "ConfigurationManager.zh-cn"
OUTPUT = ROOT / "releases"
SERVER_VERSION = ET.parse(ROOT / "SimpleTranslatorCS.csproj").findtext("./PropertyGroup/Version")
SERVER_GUIDES = (
    "PLAYER_GUIDE.zh-CN.md", "AI_MAINTAINER_GUIDE.zh-CN.md", "GLOSSARY_EDITOR.zh-CN.md",
    "GLOSSARY_EDITOR_MAINTENANCE.zh-CN.md", "VERSION_POLICY.zh-CN.md",
)


def load_client_release():
    source = CLIENT / "tools" / "package_client_release.py"
    if not source.is_file():
        raise FileNotFoundError("缺少相邻的 ConfigurationManager.zh-cn 仓库。仅打包服务端时请加 --server-only。")
    spec = importlib.util.spec_from_file_location("configurationmanager_client_release", source)
    release = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(release)
    return release


def add_file(archive: ZipFile, source: Path, target: str) -> None:
    if not source.is_file():
        raise FileNotFoundError(source)
    info = ZipInfo(target, (2026, 11, 26, 0, 0, 0))
    info.compress_type = ZIP_DEFLATED
    info.external_attr = 0o644 << 16
    archive.writestr(info, source.read_bytes())


def add_text(archive: ZipFile, target: str, value: str) -> None:
    info = ZipInfo(target, (2026, 11, 26, 0, 0, 0))
    info.compress_type = ZIP_DEFLATED
    info.external_attr = 0o644 << 16
    archive.writestr(info, value.encode("utf-8"))


def add_directory(archive: ZipFile, target: str) -> None:
    info = ZipInfo(target, (2026, 11, 26, 0, 0, 0))
    info.external_attr = (0o755 << 16) | 0x10
    archive.writestr(info, b"")


def add_user_guide(archive: ZipFile) -> None:
    add_file(archive, ROOT / "README.md", "README.zh-CN.md")
    for name in SERVER_GUIDES:
        add_file(archive, ROOT / "docs" / name, "docs/" + name)
    for name in ("index.html", "glossary-editor.js"):
        add_file(archive, ROOT / "tools" / "glossary-editor" / name, "tools/glossary-editor/" + name)


def server_package(target: str, folder: str) -> Path:
    name = f"SimpleTranslatorCS-{SERVER_VERSION}-SPT-{target}.zip"
    destination = OUTPUT / name
    prefix = "user/mods/Volcano-SimpleTranslator/"
    with ZipFile(destination, "w") as archive:
        add_user_guide(archive)
        add_file(archive, ROOT / folder / "SimpleTranslatorCS.dll", prefix + "SimpleTranslatorCS.dll")
        add_file(archive, ROOT / "config.jsonc", prefix + "config.jsonc")
        add_file(archive, ROOT / "docs" / "MODEL_DISCOVERY_AND_RELEASE_2026-10-06.zh-CN.md", prefix + "docs/模型获取与日志说明.md")
        add_file(archive, ROOT / "docs" / "PROVIDERS_LIVE_SYNC_2026-10-07.zh-CN.md", prefix + "docs/供应商与热同步说明.md")
        add_file(archive, ROOT / "docs" / "VERSION_POLICY.zh-CN.md", prefix + "docs/VERSION_POLICY.zh-CN.md")
        add_file(archive, ROOT / "docs" / "SERVER_WORKBENCH_DISCOVERY_FIX_2026-10-07.zh-CN.md", prefix + "docs/SERVER_WORKBENCH_DISCOVERY_FIX_2026-10-07.zh-CN.md")
        add_file(archive, ROOT / "glossary" / "eft.zh-CN.example.tsv", prefix + "glossary/eft.zh-CN.example.tsv")
        add_file(archive, ROOT / "docs" / "LEGACY_GLOSSARY_QUIET_SYNC_2026-10-07.zh-CN.md", prefix + "docs/LEGACY_GLOSSARY_QUIET_SYNC_2026-10-07.zh-CN.md")
        add_directory(archive, prefix + "translations/ch/")
        add_text(
            archive,
            "安装说明.txt",
            f"SimpleTranslatorCS {SERVER_VERSION}，适用于 SPT {target}。\n"
            "请先阅读压缩包首页 README.zh-CN.md；术语操作在 docs/GLOSSARY_EDITOR.zh-CN.md；离线工具在 tools/glossary-editor/index.html。\n"
            "将压缩包内容解压到 SPT 服务端根目录（4.0 通常为游戏下的 SPT，4.1/5.0 通常为 SPT_Runtime；其中应有 user/mods）。\n"
            "普通译文统一放在 translations/ch，工作台保存为 模组名_API.jsonc、模组名_MT.jsonc 或 模组名_Manual.jsonc。\n"
            "启动兼容读取旧分层和 db/locales/ch，旧译文不移动。此发布包不含预置译文、密钥和缓存。\n"
            "服务端翻译任务由发起者在客户端填写自己的 API 密钥；旧服务端密钥文件不再使用。\n"
            "配套客户端须支持服务端工作台协议。模型自动查询并保留手动输入；玩家只可使用管理员批准的模型。\n"
            "供应商预设与 F12 共用；本地和自定义接口需管理员权限。验证管理员密钥为绿色后再开始管理员翻译。\n"
            "正式译文发布后通知新版客户端拉取增量，空闲时不会轮询 live 路径。同步不消耗翻译 token。\n"
            "翻译日志：游戏根目录/API翻译日志/SimpleTranslatorCS；独立服务器使用服务器根目录。输入、进度、错误分别记录，密钥不入日志。\n"
            "术语表：Mod/glossary/eft.zh-CN.tsv，首次从 example.tsv 创建；之后只读取用户编辑文件。仅发送匹配词条，受字符及条目上限控制。\n"
            "升级时请先备份原有 mod 目录和译文；覆盖安装不会清除已有文件。\n",
        )
    return destination


def inspect_server_package(path: Path) -> None:
    with ZipFile(path) as archive:
        names = archive.namelist()
        required = {"README.zh-CN.md", "tools/glossary-editor/index.html", "tools/glossary-editor/glossary-editor.js",
                    *("docs/" + name for name in SERVER_GUIDES)}
        if not required.issubset(names):
            raise RuntimeError(f"Missing homepage guide/editor assets: {path}")
        if any(name.endswith("/eft.zh-CN.tsv") for name in names):
            raise RuntimeError(f"Active personal glossary in release: {path}")
        for name in ("README.zh-CN.md", "docs/PLAYER_GUIDE.zh-CN.md", "docs/AI_MAINTAINER_GUIDE.zh-CN.md",
                     "docs/GLOSSARY_EDITOR.zh-CN.md", "docs/GLOSSARY_EDITOR_MAINTENANCE.zh-CN.md"):
            for target in re.findall(r'\[[^\]]+\]\(([^)]+)\)', archive.read(name).decode("utf-8")):
                if "://" in target or target.startswith("#"):
                    continue
                resolved = normpath(join(dirname(name), target.split("#", 1)[0]))
                if resolved not in names:
                    raise RuntimeError(f"Broken manual link {name} -> {target}")
        if len(names) != len(set(names)):
            raise RuntimeError(f"ZIP contains duplicate paths: {path}")
        actual_json = [name for name in names if name.lower().endswith((".json", ".jsonc"))]
        if actual_json != ["user/mods/Volcano-SimpleTranslator/config.jsonc"]:
            raise RuntimeError(f"Unexpected server JSON: {actual_json}")
        bad = [name for name in names if any(word in name.lower() for word in ("secret", "admin", "cache", ".bak", ".pdb"))]
        if bad:
            raise RuntimeError(f"Private or debug file in release: {bad}")
        if archive.testzip() is not None:
            raise RuntimeError(f"Corrupt ZIP: {path}")


def main() -> None:
    global OUTPUT
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=OUTPUT, help="Release folder within this project's releases directory")
    parser.add_argument("--server-only", action="store_true", help="Package the three server targets without the client repository")
    args = parser.parse_args()
    OUTPUT = args.output.resolve()
    if not OUTPUT.is_relative_to(ROOT / "releases"):
        raise ValueError("Release output must stay within this project's releases directory")
    OUTPUT.mkdir(exist_ok=True, parents=True)
    client_release = None if args.server_only else load_client_release()
    packages = [server_package(target, folder) for target, folder in
                (("4.0", "dist-4.0"), ("4.1", "dist"), ("5.0be", "dist-5.0be"))]
    for path in packages:
        inspect_server_package(path)
    if client_release is not None:
        for runtime, binary in (("Mono-SPT4x", "bin/Release"), ("IL2CPP-SPT5.0be", "bin/IL2CPP/Release")):
            for language in client_release.CONTENT.LANGUAGES:
                path = client_release.client_package(runtime, CLIENT / binary, OUTPUT, language=language)
                client_release.inspect_client_package(path)
                packages.append(path)
    checksums = "".join(
        f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n"
        for path in packages
    )
    checksums_path = OUTPUT / "SHA256SUMS.txt"
    checksums_path.write_text(checksums, encoding="utf-8")
    if client_release is not None:
        client_release.inherit_release_permissions(checksums_path)
    for path in packages:
        print(f"{path.name}: {path.stat().st_size} bytes")
    print("SHA256SUMS.txt")


if __name__ == "__main__":
    main()
