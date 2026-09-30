// rich_return_nullable 语料（目录组）——消费侧 + 同文件形态（全局命名
// 空间本地 rich struct）。跨命名空间（toy.*）与同文件两形态都在本组
// main 覆盖；NativeE2E 侧经多文件对拍 Case 复用本组语料（VM↔native
// stdout 一致 + RIGI_RT_MEMTRACK 零泄漏）。
// expect-output: cross-full
// expect-output: 1
// expect-output: full
// expect-output: alpha
// expect-output: 1000
// expect-output: 500
// expect-output: span-null
// expect-output: tag-x
// expect-output: cross-bare
// expect-output: 2
// expect-output: null
// expect-output: stamp-null
// expect-output: tag-a
// expect-output: tag-b
// expect-output: same-file
// expect-output: 3
// expect-output: local
// expect-output: 2000
// expect-output: param.t1=
// expect-output: 8
// expect-output: param.t2=
// expect-output: 8
// expect-output: param.t3=
// expect-output: 6
// expect-output: param.full.kind=
// expect-output: 1
// expect-output: param.u1=
// expect-output: 7
// expect-output: param.u2=
// expect-output: 7
// expect-output: param.local.kind=
// expect-output: 4
// expect-exit: 0
import core.collections.List

// ===== 同文件形态的本地库（全局命名空间）=====

pub rich struct Local {
    pub var kind: i32
    pub var label: String?
    pub var stamp: core.time.TimeStamp?

    pub init(_ -> kind, _ -> label, _ -> stamp) { }
}

func makeLocal(kind: i32): Local {
    return new Local(kind, "local", new core.time.TimeStamp((2000 as i64), 250))
}

// 按值传参消费（paramfix：同文件形态）
func localConsume(l: Local): i32 {
    var total: i32 = l.kind
    const l2 = l.label
    if (l2 != null) {
        total = total + 1
    }
    const s2 = l.stamp
    if (s2 != null) {
        total = total + 2
    }
    return total
}

// ===== 入口 =====

pub func main(): i32 {
    // —— 跨命名空间形态：全局 ns 调 toy.make*（rich struct 返回值）——
    const full = toy.makeFull(1)
    core.io.Console.println("cross-full")
    core.io.Console.println(full.kind.toString())
    core.io.Console.println(full.name)
    const fl = full.label
    if (fl != null) {
        core.io.Console.println(fl)
    } else {
        core.io.Console.println("null")
    }
    const fst = full.stamp
    if (fst != null) {
        core.io.Console.println(fst.milliseconds.toString())
        core.io.Console.println(fst.nanoseconds.toString())
    } else {
        core.io.Console.println("null")
    }
    const fsp = full.span
    if (fsp != null) {
        core.io.Console.println("span")
    } else {
        core.io.Console.println("span-null")
    }
    full.tags.add("tag-x")
    const fx = full.tags.getAtIndex((0 as i64))
    if (fx != null) {
        core.io.Console.println(fx)
    } else {
        core.io.Console.println("null")
    }

    const bare = toy.makeBare(2)
    core.io.Console.println("cross-bare")
    core.io.Console.println(bare.kind.toString())
    const bl = bare.label
    if (bl != null) {
        core.io.Console.println(bl)
    } else {
        core.io.Console.println("null")
    }
    const bst = bare.stamp
    if (bst != null) {
        core.io.Console.println("stamp")
    } else {
        core.io.Console.println("stamp-null")
    }
    const b0 = bare.tags.getAtIndex((0 as i64))
    if (b0 != null) {
        core.io.Console.println(b0)
    } else {
        core.io.Console.println("null")
    }
    const b1 = bare.tags.getAtIndex((1 as i64))
    if (b1 != null) {
        core.io.Console.println(b1)
    } else {
        core.io.Console.println("null")
    }

    // —— 同文件形态：全局命名空间内 make + 消费 ——
    const local = makeLocal(3)
    core.io.Console.println("same-file")
    core.io.Console.println(local.kind.toString())
    const ll = local.label
    if (ll != null) {
        core.io.Console.println(ll)
    } else {
        core.io.Console.println("null")
    }
    const lst = local.stamp
    if (lst != null) {
        core.io.Console.println(lst.milliseconds.toString())
    } else {
        core.io.Console.println("null")
    }

    // —— 按值传参形态（paramfix）：返回值再按值传参 + 连续两次传同一
    // 变量 + 传参后源变量仍完好（修复前源槽被回写改指向克隆，源块泄
    // 漏、克隆被双重释放 → native 段错误）——
    const t1 = toy.totalReport(full)
    core.io.Console.println("param.t1=")
    core.io.Console.println(t1.toString())
    const t2 = toy.totalReport(full)
    core.io.Console.println("param.t2=")
    core.io.Console.println(t2.toString())
    const t3 = toy.totalReport(bare)
    core.io.Console.println("param.t3=")
    core.io.Console.println(t3.toString())
    const fk = full.kind
    core.io.Console.println("param.full.kind=")
    core.io.Console.println(fk.toString())

    const lc = makeLocal(4)
    const u1 = localConsume(lc)
    core.io.Console.println("param.u1=")
    core.io.Console.println(u1.toString())
    const u2 = localConsume(lc)
    core.io.Console.println("param.u2=")
    core.io.Console.println(u2.toString())
    const lk = lc.kind
    core.io.Console.println("param.local.kind=")
    core.io.Console.println(lk.toString())
    return 0
}
