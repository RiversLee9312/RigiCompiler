// rich_return_nullable 语料（目录组）——richretrfix 回归：
// 函数返回含 Nullable 装箱字段的 rich struct（tag1 盒槽交付即移动契约）。
// 本文件 = 跨命名空间形态的库侧（namespace toy）；消费侧在同组 main.rg。
// 字段覆盖多槽种类混合：标量 + String（STRING 槽）+ String?/TimeStamp?/
// TimeSpan?（tag1 盒 Nullable 槽，null 与非 null）+ List 引用（tag2 槽）。
// TimeSpan 无 pub init（time.rg 可见性设计），只覆盖 null 形态 + 字段存在。
namespace toy

pub rich struct Report {
    pub var kind: i32
    pub var name: String
    pub var label: String?
    pub var stamp: core.time.TimeStamp?
    pub var span: core.time.TimeSpan?
    pub var tags: core.collections.List\<String>

    pub init(_ -> kind, _ -> name, _ -> label, _ -> stamp, _ -> span, _ -> tags) { }
}

// 直接返回 new：非 null String?/TimeStamp? + null TimeSpan?
pub func makeFull(kind: i32): Report {
    return new Report(kind, "full", "alpha",
        new core.time.TimeStamp((1000 as i64), 500), null,
        new core.collections.List\<String>())
}

// 经局部变量再返回：null String?/TimeStamp? + List 先填充
pub func makeBare(kind: i32): Report {
    const r = new Report(kind, "bare", null, null, null,
        new core.collections.List\<String>())
    r.tags.add("tag-a")
    r.tags.add("tag-b")
    return r
}

// 按值传参消费（paramfix 回归：含 Nullable 装箱字段的 rich struct 按值
// 传参——修复前 MarshalArg/prologue 的 acquire(src) 回写源槽，源块泄
// 漏 + 克隆被 callee 出口与 caller 实参槽出口双重释放，native 段错误）
pub func totalReport(r: Report): i32 {
    var total: i32 = r.kind
    const l = r.label
    if (l != null) {
        total = total + 1
    }
    const st = r.stamp
    if (st != null) {
        total = total + 2
    }
    const f = r.tags.getAtIndex((0 as i64))
    if (f != null) {
        total = total + 4
    }
    return total
}

