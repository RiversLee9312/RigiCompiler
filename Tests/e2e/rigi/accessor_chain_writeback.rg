// ============================================================================
// accessor_chain_writeback.rg —— chainfix 回归语料：值类型 receiver 写穿
// 接管的 setter 使用点可见性口径。
//   ① priv set 属性链直调方法（chainfix 主形态：修复前 P4 接管判定按
//      setter 存在性、写回构造按使用点可见性，两处口径漂移致纯读表达式
//      被误报 'X' is inaccessible——现同一谓词把关，只读 place 不接管
//      不写回）
//   ② 语句位 void 直调（语句位入口同口径）
//   ③ 深链混合可见性（外层环 priv set + 内层环 pub set：断一环全链
//      不接管，落普通路径无诊断）
//   ④ pub set 全可见链突变外溢（可见 setter 保留写回外溢语义——§10
//      值类型方法 this 突变经反向写回传播到根 place）
//   ⑤ priv set 链突变不外溢（只读 place，§10：this 修改本就不生效——
//      ② 的直调 inc 落在拷贝上，读回原值）
//   ⑥ 先绑定局部对照（与直调同结果）
// expect-output: chain-ok
// expect-output: void-ok
// expect-output: deep-ok
// expect-output: spill 2
// expect-output: readonly 3
// expect-output: localbind true
// expect-exit: 0
// ============================================================================
import core.io.Console

// 探针值类型：n 带 pub get + pub set（全可见写回通道）+ 读方法与突变方法
struct MutProbe {
    pub var n: i32 {
        pub get
        pub set(value: _) { }
    }

    pub init(_ -> n) { }

    pub func isPositive(): bool {
        return n > 0
    }

    pub func inc() {
        this.n = this.n + 1
    }
}

// 只读宿主：value 环 pub get + priv set（外部使用点只读——写通道仅
// 服务构造，§9.4.1 惯用法）
struct ReadOnlyBox {
    pub var value: MutProbe {
        pub get
        priv set(value: _) { }
    }

    pub init(_ -> value) { }
}

// 深链中间层：probe 环全 pub（与外层 priv set 环构成混合可见性深链）
struct MidLayer {
    pub var probe: MutProbe {
        pub get
        pub set(value: _) { }
    }

    pub init(_ -> probe) { }
}

struct DeepBox {
    pub var mid: MidLayer {
        pub get
        priv set(value: _) { }
    }

    pub init(_ -> mid) { }
}

// 全可见宿主：held 环 pub get + pub set（写回外溢通道保留）
struct WritableBox {
    pub var held: MutProbe {
        pub get
        pub set(value: _) { }
    }

    pub init(_ -> held) { }
}

func main(): i32 {
    const box = new ReadOnlyBox(new MutProbe(3))
    // ① priv set 属性链直调方法（chainfix 主回归形态）
    if (box.value.isPositive()) {
        Console.println("chain-ok")
    } else {
        Console.println("chain-BAD")
    }
    // ② 语句位 void 直调
    box.value.inc()
    Console.println("void-ok")
    // ③ 深链混合可见性：mid 环 priv set + probe 环 pub set
    const deep = new DeepBox(new MidLayer(new MutProbe(7)))
    if (deep.mid.probe.isPositive()) {
        Console.println("deep-ok")
    } else {
        Console.println("deep-BAD")
    }
    // ④ pub set 全可见链突变外溢（写回外溢语义钉子：1 + 1 = 2）
    const wbox = new WritableBox(new MutProbe(1))
    wbox.held.inc()
    Console.println("spill ${wbox.held.n}")
    // ⑤ priv set 链突变不外溢（② 的 inc 落在拷贝上，读回仍 3）
    Console.println("readonly ${box.value.n}")
    // ⑥ 先绑定局部对照（与直调同结果）
    const bound: MutProbe = box.value
    if (bound.isPositive()) {
        Console.println("localbind true")
    } else {
        Console.println("localbind BAD")
    }
    return 0
}
