"""
静态分析树的渲染构成：不启动 Unity，直接解析 Unity YAML 资产。

为什么用静态分析：Unity 编辑器正被用户占用（项目锁），批处理无法启动。
树 prefab / 材质 / shader 都是文本 YAML，足够回答"为什么批次多、keyword 差在哪"。

解析内容：
  1. 每个树 prefab 的 LODGroup 层级与各 LOD 的 renderer
  2. 每个 renderer 的材质 → shader
  3. 材质上显式启用的 shader keyword（m_ShaderKeywords）
  4. shader 声明的 keyword（multi_compile / shader_feature）——判断哪些能切换

用法: python analyze_tree_variants.py <项目根>
"""
import os
import re
import sys
from collections import defaultdict, Counter

GUID_RE = re.compile(r"guid: ([0-9a-f]{32})")


def read(path):
    with open(path, "r", encoding="utf-8", errors="ignore") as f:
        return f.read()


def build_guid_index(root):
    """guid -> asset 路径（扫所有 .meta）"""
    idx = {}
    for dirpath, _, files in os.walk(root):
        for fn in files:
            if not fn.endswith(".meta"):
                continue
            p = os.path.join(dirpath, fn)
            try:
                head = open(p, "r", encoding="utf-8", errors="ignore").read(400)
            except OSError:
                continue
            m = GUID_RE.search(head)
            if m:
                idx[m.group(1)] = p[:-5]  # 去掉 .meta
    return idx


def parse_prefab_lods(text):
    """返回 [(lodIndex, screenHeight, [ (rendererName, materialGuid) ])]"""
    # LODGroup 块
    lod_groups = re.findall(r"--- !u!1044 &(\d+)\nLODGroup:(.*?)(?=\n--- |\Z)", text, re.S)
    result = []
    for _fid, body in lod_groups:
        # m_LODs 列表：每个 - screenRelativeTransitionHeight / renderers
        lod_blocks = re.split(r"\n  - serializedVersion:", body)
        idx = 0
        for blk in lod_blocks[1:]:
            h = re.search(r"screenRelativeTransitionHeight: ([\d.]+)", blk)
            result.append((idx, float(h.group(1)) if h else -1.0))
            idx += 1
    return result, body if False else result


def parse_renderers(text):
    """所有 MeshRenderer 的 (gameObjectName, [material guids])"""
    out = []
    for m in re.finditer(r"--- !u!23 &(\d+)\nMeshRenderer:(.*?)(?=\n--- |\Z)", text, re.S):
        body = m.group(2)
        mats = GUID_RE.findall(
            body.split("m_Materials:")[1].split("m_StaticBatchInfo")[0]
            if "m_Materials:" in body else ""
        )
        go = re.search(r"m_GameObject: \{fileID: (\d+)\}", body)
        out.append((go.group(1) if go else "?", mats))
    return out


def parse_material(text):
    """返回 (shader guid, [keywords])

    注意：Unity 2022.3 的材质 keyword 序列化字段是 m_ValidKeywords。
    旧版本用 m_ShaderKeywords，解析它在本工程会永远读到 0 项，
    从而得出「所有材质 keyword 都为空」的错误结论（2026-11-12 踩过）。
    m_InvalidKeywords 是"材质上残留但 shader 不再声明"的废弃项，不算有效 keyword。
    """
    sh = re.search(r"m_Shader: \{fileID: -?\d+, guid: ([0-9a-f]{32})", text)
    keywords = []
    m = re.search(r"m_ValidKeywords:\s*(.*?)(?=\n  m_[A-Z]|\n  disabled|\Z)", text, re.S)
    if m:
        keywords = [ln.strip().lstrip("- ").strip()
                    for ln in m.group(1).splitlines()
                    if ln.strip().startswith("-")]
    return (sh.group(1) if sh else None), keywords


def parse_shader_keywords(path):
    """shader 源文件里声明的 keyword（multi_compile / shader_feature）"""
    try:
        text = read(path)
    except OSError:
        return set()
    kws = set()
    for m in re.finditer(r"#pragma\s+(?:multi_compile|shader_feature)\w*\s+(.+)", text):
        for tok in m.group(1).split():
            if tok.startswith("_"):
                kws.add(tok)
            elif tok == "_":
                pass
    # shadergraph 是 JSON，keyword 在里面
    if path.endswith(".shadergraph"):
        for m in re.finditer(r'"(m_Name|name)"\s*:\s*"(_[A-Z0-9_]+)"', text):
            kws.add(m.group(2))
        for m in re.finditer(r"m_KeywordDeclaration|_KEYWORD", text):
            pass
        # ShaderGraph 的 keyword 名通常出现在 m_Keywords
        for m in re.finditer(r'"_?([A-Z][A-Z0-9_]{3,})"', text):
            pass
    return kws


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else "E:/FishDemo"
    assets = os.path.join(root, "Assets")
    guid_idx = build_guid_index(assets)
    print(f"索引 {len(guid_idx)} 个资产 GUID")

    tree_dir = os.path.join(
        assets, "Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Prefabs/Trees")
    prefabs = sorted(f for f in os.listdir(tree_dir) if f.endswith(".prefab"))
    print(f"树 prefab 数 = {len(prefabs)}")

    # shader -> 声明 keyword
    shader_kw_cache = {}

    report = []
    shader_counter = Counter()
    variant_groups = defaultdict(list)   # (shader, tuple(kws)) -> [prefab/LOD]

    for pf in prefabs:
        path = os.path.join(tree_dir, pf)
        text = read(path)
        name = pf[:-7]

        lods, _ = parse_prefab_lods(text)
        renderers = parse_renderers(text)

        report.append(f"\n-- {name} --")
        report.append(f"   LODGroup 级数 = {len(lods)}: " +
                      ", ".join(f"L{i}@{h:.2f}" for i, h in lods))
        report.append(f"   MeshRenderer 数 = {len(renderers)}")

        for go_id, mat_guids in renderers:
            for mg in mat_guids:
                mpath = guid_idx.get(mg)
                if not mpath:
                    report.append(f"   renderer(go {go_id}) 材质 guid {mg} 未找到")
                    continue
                sguid, kws = parse_material(read(mpath))
                spath = guid_idx.get(sguid, "")
                sname = os.path.basename(spath) if spath else f"(未知 {sguid})"

                if spath and spath not in shader_kw_cache:
                    shader_kw_cache[spath] = parse_shader_keywords(spath)

                active = tuple(sorted(k for k in kws if k in shader_kw_cache.get(spath, set()))) \
                    if shader_kw_cache.get(spath) else tuple(sorted(kws))

                shader_counter[sname] += 1
                variant_groups[(sname, active)].append(f"{name}")

                report.append(f"   [{os.path.basename(mpath)}] shader={sname}")
                report.append(f"      材质 keyword({len(kws)}) = {', '.join(kws) if kws else '(无)'}")
                decl = shader_kw_cache.get(spath, set())
                report.append(f"      shader 声明 keyword 数 = {len(decl)}")
                report.append(f"      → 变体 = {sname} | {active if active else '(无 keyword)'}")

    report.append("\n\n===== 汇总 =====")
    report.append(f"不同 shader = {len(shader_counter)}")
    for s, n in shader_counter.most_common():
        report.append(f"   {s}: {n} 次")
    report.append("")
    report.append(f"不同 shader+keyword 组合 = {len(variant_groups)}（每组至少 1 次独立提交）")
    for (s, kws), users in sorted(variant_groups.items(), key=lambda x: -len(x[1])):
        report.append(f"   [{len(users)}] {s} | {kws if kws else '(无 keyword)'}")
        for u in sorted(set(users))[:8]:
            report.append(f"        {u}")

    out = os.path.join(root, "Logs", "tree-variants.txt")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(report))
    print(f"写出 {out}")
    for line in report[-40:]:
        print(line)


if __name__ == "__main__":
    main()
