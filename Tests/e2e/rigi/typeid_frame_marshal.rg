import core.collections.*
import core.serialization.*
import core.coroutine.*
import core.io.Console
// expect-output: typeid-frame-marshal-ok
// expect-exit: 0
// typefix 回归：协程帧编组 ABI 同构判定。
//
// 缺陷形态（native 旧行为）：被协程切分的泛型方法收 Type\<T> 形参，
// 调用点传闭合 typeOf(x)（.typeid<X>）时，CoroutineSplitPass 的 tainted
// 虚调用臂按「非 FatReference ⇒ MirBoxAny」把 8B 裸 sheet 指针装箱成
// 16B 胖值，frame 字段只有 8B，StoreAt 裸 store 只搬胖值第 0 字段——
// 落槽的是视图 sheet core::Type\<X> 而非边界 sheet X，被调侧 typeNameOf
// 的 typeid 装箱 toString 输出 "core::Type\<X>"，反射双拼写分发不命中
// 抛「未登记」。VM 侧无此层，一直正常。
//
// 本语料锁定三个面：
//   1. 实参方向（修复点）：切分泛型方法收 Type\<T> 值形参 + 闭合
//      typeOf(x) 实参，typeNameOf 必须命中、is 右侧判定必须正确；
//   2. 返回方向（DONE 臂，源开放 Type\<T> → 调用点闭合 Type\<X> 结果槽，
//      走 MirCast 动态边界检查支）：cast 后 is 判定正确；
//   3. 装箱视图面（b4-2 修复的收集完备性与本修复协同）：Type 值装箱
//      进 Any 容器再读出做 is 右侧。

@Serializable()
pub shared class TvLeaf { pub var flag: bool = false }

// 挂起点（yield sleep）使方法 tainted → 协程切分 + 虚调用臂 frame 打包。
pub shared class TvProbe {
    pub async func describe\<T with Serializable>(tv: Type\<T>): String {
        yield sleep(1)
        return typeNameOf(tv)
    }

    pub async func echoType\<T with Serializable>(x: T): Type\<T> {
        yield sleep(1)
        return typeOf(x)
    }
}

pub func main(): i32 {
    const p = new TvProbe()
    const leaf = new TvLeaf()
    // 1. 实参方向：闭合 typeOf(x) → Type\<T> 形参（帧打包修复路径）。
    const name = await p.describe\<TvLeaf>(typeOf(leaf))
    if (name != "TvLeaf") { return 1 }
    // 同一实参再做 is 右侧判定（Type 值语义未被装箱污染）。
    const tl = typeOf(leaf)
    if (not (leaf is tl)) { return 2 }
    // 2. 返回方向：被调返回开放 Type\<T>，调用点收窄进闭合 Type\<TvLeaf>
    //    槽（DONE 臂 MirCast 边界检查支）。
    const t: Type\<TvLeaf> = await p.echoType\<TvLeaf>(leaf)
    if (not (leaf is t)) { return 3 }
    const other = new TvLeaf()
    if (not (other is t)) { return 4 }
    // 3. 装箱视图协同：Type 值进 Any 容器再读出。
    const boxed = core.collections.arrayOf\<Any>(1)
    boxed[0] = t
    const back = boxed[0] as Any
    const t2 = back as core.Type\<TvLeaf>
    if (not (leaf is t2)) { return 5 }
    Console.println("typeid-frame-marshal-ok")
    return 0
}
