"""Build deterministic installation and corresponding-source ZIPs from a clean release checkout."""
import argparse
import hashlib
import re
import subprocess
import sys
import zipfile
from pathlib import Path


def digest(data):
    return hashlib.sha256(data).hexdigest()


def archive(path, entries):
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as output:
        for name, data in sorted(entries.items()):
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            output.writestr(info, data)
    with zipfile.ZipFile(path) as result:
        if set(result.namelist()) != set(entries) or result.testzip() is not None:
            raise RuntimeError("Archive validation failed")
        if any(result.read(name) != data for name, data in entries.items()):
            raise RuntimeError("Archive payload differs from input")
    path.with_suffix(path.suffix + ".sha256").write_text(f"{digest(path.read_bytes())}  {path.name}\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    if subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"], cwd=root).strip():
        raise SystemExit("Commit reviewed source before packaging")
    subprocess.run([sys.executable, str(root / "tools/check_public_release.py"), "--root", str(root)], check=True)
    version = re.search(r"<Version>([^<]+)</Version>", (root / "src/ImmersiveVoice.csproj").read_text())[1]
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root).decode().strip()
    binary = args.dll.read_bytes()
    if not binary.startswith(b"MZ"):
        raise SystemExit("Expected a built Windows/.NET DLL")
    for text in (binary.decode("utf-8", errors="ignore"), binary.decode("utf-16-le", errors="ignore")):
        if re.search(r"(?i)(?:/mnt/[a-z]/|\b[a-z]:[\\/]|/(?:home|Users)/[^\s/]+/|\.pdb\b)", text):
            raise SystemExit("DLL contains a local build path or debug symbol path")
    base = f"ImmersiveVoice-{version}"
    source_url = f"https://github.com/ZombiesYard/ImmersiveVoice/releases/tag/v{version}"
    instructions = f"""Immersive Voice 沉浸式角色语音 {version}
作者：ZombiesYard
支持环境：SPT 4.1.6 / EFT 0.16.9.40743；Fika 2.4.3

安装：退出客户端，将本压缩包中的BepInEx文件夹解压到包含EscapeFromTarkov.exe的客户端根目录。
正确路径：BepInEx/plugins/ImmersiveVoice/ImmersiveVoice.dll
无需安装到SPT服务端或headless。每名希望自动说话的玩家在自己的客户端安装。

F12配置菜单：切换简体中文/English，调整频率，按需展开高级设置。
默认Pause键切换本局静默；原有手动喊话保留。
与NPC自动交流默认开启；成功回应默认间隔30秒，同一NPC的90秒窗口内最多两次回应。
按来话及关系选择敌对回应、动静提示、近处平静独白或行为吻合的友善应答；缺台词跳过。
NPC互动仍待游戏内及Fika实测。
配置首次启动自动生成：BepInEx/config/local.immersivevoice.cfg
升级保留原有配置；卸载时退出游戏并删除插件目录。

使用当前声线的已有台词；缺少对应音效保持沉默。原版声线的治疗提示存在素材登记缺口。
不保证每次动作都说话。概率、冷却、潜行、手动语音、原版讲话占用和动作结束都会影响发声。
本包不包含游戏音频、其他插件、个人配置或运行日志。

独立日志：BepInEx/ImmersiveVoiceLogs/。公开反馈前请检查并移除个人信息。
许可证：GPL-3.0-only，全文见LICENSE.txt；第三方权利见NOTICE.txt。
完整说明：https://github.com/ZombiesYard/ImmersiveVoice
对应源码：{source_url} 的 {base}-source.zip 或该版本标签。
源码提交：{revision}

SHA256SUMS.txt用于核对文件完整性，不代表病毒检测结论。
"""
    payload = {
        "BepInEx/plugins/ImmersiveVoice/ImmersiveVoice.dll": binary,
        "README-安装.txt": instructions.encode("utf-8"),
        "LICENSE.txt": (root / "LICENSE").read_bytes(),
        "NOTICE.txt": (root / "NOTICE.md").read_bytes(),
    }
    payload["SHA256SUMS.txt"] = "".join(f"{digest(data)}  {name}\n" for name, data in sorted(payload.items())).encode()
    args.output.mkdir(parents=True, exist_ok=True)
    archive(args.output / f"{base}.zip", payload)
    names = subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode().split("\0")
    source = {f"{base}-source/{name}": (root / name).read_bytes() for name in filter(None, names)}
    source[f"{base}-source/RELEASE-COMMIT.txt"] = (revision + "\n").encode()
    archive(args.output / f"{base}-source.zip", source)
    print(f"Created and verified {base}.zip and corresponding-source archive; source commit {revision}")


if __name__ == "__main__":
    main()
