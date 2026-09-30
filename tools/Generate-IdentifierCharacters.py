#!/usr/bin/env python3
"""从固定 BMP 字母/数字真值生成 C# 与 Rigi 共用的分类镜像。

真值为 65536 个字符的 ASCII 文本：L=字母、D=Nd、.=其余；
输入文件由 .NET 10.0.11/10.0.12 的 char.IsLetter/IsDigit 逐码元导出，
升级时先核对 Windows/Linux 两侧全量哈希，再同时重生两份视图。
"""
import argparse
import hashlib
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parent.parent
CS = ROOT / "Lexer" / "IdentifierCharacters.cs"
RG = ROOT / "stdlib" / "core" / "serialization.rg"
EXPECTED_SHA256 = "16e9c532abee5ecf8bda353626cac146aa9f32ce0e3958ee677b1a58add14a98"
BEGIN = "BEGIN GENERATED BMP RANGES"
END = "END GENERATED BMP RANGES"


def ranges(values, kind):
    result = []
    for cp, value in enumerate(values):
        if value != kind:
            continue
        if result and cp == result[-1][1] + 1:
            result[-1][1] = cp
        else:
            result.append([cp, cp])
    return result


def replace_generated(path, block, verify):
    old = path.read_text(encoding="utf-8")
    pattern = r"(?<=" + BEGIN + r"\n).*?\s*(?=(?:// |# )?" + END + r")"
    found = re.search(pattern, old, flags=re.S)
    if not found:
        raise ValueError(f"缺少数据边界：{path}")
    updated = old[:found.start()] + block + ("        " if path == CS else "") + old[found.end():]
    if verify:
        if old != updated:
            raise ValueError(f"镜像与真值不一致：{path}")
    else:
        path.write_text(updated, encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("truth", type=pathlib.Path, nargs="?", help="可选：升级时来自 .NET 双平台核对的 65536 字符类别真值")
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    if args.truth is None:
        source = CS.read_text(encoding="utf-8")
        arrays = {}
        for name in ("LetterStarts", "LetterEnds", "DigitStarts", "DigitEnds"):
            match = re.search(rf"int\[\] {name} = \{{ ([0-9, ]+) \}};", source)
            if not match:
                raise ValueError(f"缺少 C# 单一分类源：{name}")
            arrays[name] = [int(n) for n in match.group(1).split(", ")]
        bitmap = bytearray(b"." * 65536)
        for label, value in (("Letter", ord("L")), ("Digit", ord("D"))):
            starts, ends = arrays[label + "Starts"], arrays[label + "Ends"]
            if len(starts) != len(ends):
                raise ValueError("区间起点和终点长度不符")
            for start, end in zip(starts, ends):
                if not (0 <= start <= end < 65536) or any(bitmap[start:end + 1].replace(b".", b"")):
                    raise ValueError("分类区间重叠或越界")
                bitmap[start:end + 1] = bytes([value]) * (end - start + 1)
        values = bytes(bitmap)
    else:
        values = args.truth.read_bytes()
    if len(values) != 65536 or set(values) - {ord("L"), ord("D"), ord(".")}:
        raise ValueError("真值必须恰好 65536 个 L/D/. ASCII 字符")
    actual = hashlib.sha256(values).hexdigest()
    if actual != EXPECTED_SHA256:
        raise ValueError(f"真值 SHA256 与固定 .NET 版本不符：{actual}")
    letters, digits = ranges(values, ord("L")), ranges(values, ord("D"))
    if len(letters) != 380 or len(digits) != 37:
        raise ValueError("分类区间数量不符")
    cs_lines = []
    for label, rows in (("Letter", letters), ("Digit", digits)):
        for edge, index in (("Starts", 0), ("Ends", 1)):
            cs_lines.append(f"        internal static readonly int[] {label}{edge} = {{ " +
                            ", ".join(str(pair[index]) for pair in rows) + " };\n")
    rg_lines = []
    for label, rows in (("Letter", letters), ("Digit", digits)):
        for edge, index in (("Starts", 0), ("Ends", 1)):
            rg_lines.append(f"priv var identifier{label}{edge}: Array\\<i64> = " +
                            "core.collections.arrayOfElements\\<i64>(" +
                            ", ".join(str(pair[index]) for pair in rows) + ")\n")
    replace_generated(CS, "".join(cs_lines), args.verify)
    replace_generated(RG, "".join(rg_lines), args.verify)
    print(f"BMP 真值吻合 SHA256={actual}，字母 {len(letters)} 区间，数字 {len(digits)} 区间，镜像{'一致' if args.verify else '已生成'}")


if __name__ == "__main__":
    main()
