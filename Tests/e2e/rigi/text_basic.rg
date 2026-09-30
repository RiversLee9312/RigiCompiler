// core.text 字符串核心操作端到端（STDLIB §4.3.1/§4.3.2 查找段/§4.3.3，
// 施工块 3-2）：固定样例组、U+0000 内容、组合字符分别计数、slice 合法/
// 非法边界、sliceCharacters/characterAt/characters 独立游标、toUtf8Span
// 副本独立性；查找（indexOf/lastIndexOf/contains/startsWith/endsWith）、
// 分割/替换/裁剪/拼接（split/replace/replaceFirst/trim/join）
// expect-output: len: 9 cc: 4
// expect-output: slice17: 中😀
// expect-output: slice21 caught
// expect-output: zero: []
// expect-output: tail: [B]
// expect-output: neg caught
// expect-output: oob caught
// expect-output: cut caught
// expect-output: sc: [中😀]
// expect-output: ca: true true true true
// expect-output: ca-oob: true true
// expect-output: iter: [A中😀B] n: 4 n2: 4
// expect-output: span len: 9
// expect-output: mut: true orig: [A]
// expect-output: nul len: 3 cc: 3 ca0: true
// expect-output: comb len: 17 cc: 6
// expect-output: comb-ca: true false
// expect-output: idx-fwd: 4 null: true empty: 0
// expect-output: idx-start: 9 empty: 9 neg: true oob: true cut: true
// expect-output: idx-last: 12 empty: 16 miss: true
// expect-output: contains: true true false false
// expect-output: affix: true true true true false false
// expect-output: split: 4 [a][][b][]
// expect-output: split-max: 2 [a][b,c] 1: [a,b,c]
// expect-output: split-empty: 1 [] sep: true max: true
// expect-output: replace: [ba] [bababa] first: [baa] [axxa]
// expect-output: replace-empty-old caught: true no-match: true
// expect-output: trim: [a b] [a b] [a b]
// expect-output: trim2: true true true
// expect-output: join: [a----b] one: 1 empty: []
// expect-exit: 0
import core.io.Console
import core.collections.*
import core.text.*

pub func main(): i32 {
    stageSlice()
    stageFind()
    stageSplitReplaceTrimJoin()
    return 0
}

// ── §4.3.1 固定样例组与切片/位置/遍历 ──
priv func stageSlice() {
    // 固定样例组：A中😀B——length 9、characterCount 4、indexOf("😀") 为 4、
    // slice(1,7) 为 中😀、slice(2,1) 报错（§4.3.1）
    const s = "A中😀B"
    Console.println("len: ${s.length} cc: ${s.characterCount}")
    Console.println("slice17: ${s.slice((1 as i64), (7 as i64))}")
    try {
        const bad = s.slice((2 as i64), (1 as i64))
        Console.println("slice21 NOT caught: [${bad}]")
    } catch (e: core.OutOfBoundException) {
        Console.println("slice21 caught")
    }
    // 合法边界：零长度返回空串、末尾是合法边界
    Console.println("zero: [${s.slice((1 as i64), (0 as i64))}]")
    Console.println("tail: [${s.slice((8 as i64))}]")
    // 非法边界：负参数/越界/切断编码序列
    try {
        const b1 = s.slice((-1 as i64))
        Console.println("neg NOT caught: [${b1}]")
    } catch (e: core.OutOfBoundException) {
        Console.println("neg caught")
    }
    try {
        const b2 = s.slice((5 as i64), (5 as i64))
        Console.println("oob NOT caught: [${b2}]")
    } catch (e: core.OutOfBoundException) {
        Console.println("oob caught")
    }
    try {
        const b3 = s.slice((2 as i64))
        Console.println("cut NOT caught: [${b3}]")
    } catch (e: core.OutOfBoundException) {
        Console.println("cut caught")
    }
    // sliceCharacters：按标量计数
    Console.println("sc: [${s.sliceCharacters((1 as i64), (2 as i64))}]")
    // characterAt：按标量索引，越界 null
    const c1: char? = s.characterAt((0 as i64))
    const c2: char? = s.characterAt((1 as i64))
    const c3: char? = s.characterAt((2 as i64))
    const c4: char? = s.characterAt((3 as i64))
    Console.println("ca: ${(c1 if? 'q') == 'A'} ${(c2 if? 'q') == '中'} ${(c3 if? 'q') == '😀'} ${(c4 if? 'q') == 'B'}")
    Console.println("ca-oob: ${isNullChar(s.characterAt((9 as i64)))} ${isNullChar(s.characterAt((-1 as i64)))}")
    // characters：每次遍历独立游标（两次遍历各自完整计数），组合字符
    //（标量序列）分别计数，不构成单个 char（§4.3.1）
    const chars = s.characters()
    var buf = ""
    var n: i64 = (0 as i64)
    for (x in chars) {
        buf = "${buf}${x}"
        n = (n + (1 as i64))
    }
    var n2: i64 = (0 as i64)
    for (y in chars) {
        n2 = (n2 + (1 as i64))
    }
    Console.println("iter: [${buf}] n: ${n} n2: ${n2}")
    // toUtf8Span：独立副本——修改缓冲区不影响原 String（§4.3.1）
    const sp = s.toUtf8Span()
    Console.println("span len: ${sp.length}")
    sp[0] = (88 as u8)
    Console.println("mut: ${(sp[0] if? (0 as u8)) == (88 as u8)} orig: [${s.slice((0 as i64), (1 as i64))}]")
    // U+0000 是普通内容，不作为终止符（§4.3.1；经 char 插值构造）
    const zero: char = (0 as char)
    const zeroStr = "a${zero}b"
    Console.println("nul len: ${zeroStr.length} cc: ${zeroStr.characterCount} ca0: ${(zeroStr.characterAt((1 as i64)) if? 'q') == zero}")
    // 组合字符序列分别计数：e + 组合重音(U+0301) + ZWJ(U+200D) +
    // 男表情(U+1F468) + ZWJ + 笔记本(U+1F4BB)——6 标量 17 字节，
    // 不构成单个 char（§4.3.1 标量不等于字素簇）
    // 经标量插值构造保证分解形态（源码 é 直接书写会是预组合 U+00E9）：
    const accentC: char = (0x301 as char)
    const zwjC: char = (0x200D as char)
    const manC: char = (0x1F468 as char)
    const laptopC: char = (0x1F4BB as char)
    const comb = "e${accentC}${zwjC}${manC}${zwjC}${laptopC}"
    Console.println("comb len: ${comb.length} cc: ${comb.characterCount}")
    // 组合序列首标量是 e、第二个标量是组合重音（分别可定位）
    Console.println("comb-ca: ${(comb.characterAt((0 as i64)) if? 'q') == 'e'} ${(comb.characterAt((1 as i64)) if? 'q') == 'e'}")
}

// ── §4.3.2 查找 ──
priv func stageFind() {
    // 字节布局：A(0) 中(1..3) 😀(4..7) B(8) 中(9..11) 😀(12..15)
    const s = "A中😀B中😀"
    // indexOf 命中（字节偏移 4）/未命中 null/空 needle 在 0 匹配
    Console.println("idx-fwd: ${(s.indexOf("😀") if? (-1 as i64))} null: ${isNullI64(s.indexOf("x"))} empty: ${(s.indexOf("") if? (-1 as i64))}")
    // indexOf(needle, start)：从合法字节边界向后查找；非法 start（负值/
    // 越界/切断编码序列）仍报范围错误（§4.3.2）
    Console.println("idx-start: ${(s.indexOf("中", (8 as i64)) if? (-1 as i64))} empty: ${(s.indexOf("", (9 as i64)) if? (-1 as i64))} neg: ${caughtOobI64(func{(): i64? -> s.indexOf("中", (-1 as i64))})} oob: ${caughtOobI64(func{(): i64? -> s.indexOf("中", (17 as i64))})} cut: ${caughtOobI64(func{(): i64? -> s.indexOf("中", (2 as i64))})}")
    // lastIndexOf：首版全串反向查找（命中最后的 😀 在字节 12）；
    // 空 needle 返回 length
    Console.println("idx-last: ${(s.lastIndexOf("😀") if? (-1 as i64))} empty: ${(s.lastIndexOf("") if? (-1 as i64))} miss: ${isNullI64(s.lastIndexOf("x"))}")
    // contains / startsWith / endsWith：空串恒 true；匹配区分大小写、
    // 不自动规范化（§4.3.2）
    Console.println("contains: ${s.contains("😀")} ${s.contains("")} ${s.contains("x")} ${s.contains("中B")}")
    Console.println("affix: ${s.startsWith("A中")} ${s.startsWith("")} ${s.endsWith("B中😀")} ${s.endsWith("")} ${s.startsWith("中")} ${s.endsWith("A")}")
}

// ── §4.3.3 分割/替换/裁剪/拼接 ──
priv func stageSplitReplaceTrimJoin() {
    // split：字面分隔、从左到右不重叠、保留开头/中间/末尾空项
    const parts = "a,,b,".split(",")
    var desc = ""
    // 索引 while 遍历拼接（Array 经 asEnumerable 的 for-in 在 native 宿主
    // 存在既有缺陷：arrayOf 建数组+填充后 for-in 段错误，与本块无关——
    // arrayOfElements 构造或索引读不受影响，见块报告）
    var di: i32 = 0
    while (di < parts.length) {
        desc = "${desc}[${parts[di] if? ""}]"
        di = (di + 1)
    }
    Console.println("split: ${parts.length} ${desc}")
    // maxParts：正数上限，达到后剩余文本作为最后一项；1 表示不分割
    const limited = "a,b,c".split(",", 2)
    const one = "a,b,c".split(",", 1)
    Console.println("split-max: ${limited.length} [${limited[0] if? ""}][${limited[1] if? ""}] 1: [${one[0] if? ""}]")
    // 空输入得单个空串；空 separator / maxParts ≤ 0 抛 TextArgumentException
    const emptyParts = "".split(",")
    Console.println("split-empty: ${emptyParts.length} [${emptyParts[0] if? ""}] sep: ${caughtArgArr(func{(): Array\<String> -> "a".split("")})} max: ${caughtArgArr(func{(): Array\<String> -> "a".split(",", 0)})}")
    // replace：全部从左到右不重叠（"aaa" 中 "aa"→"b" 得 "ba"）；
    // replacement 字面插入不参与本次匹配；replaceFirst 只替换首个
    Console.println("replace: [${"aaa".replace("aa", "b")}] [${"aaa".replace("a", "ba")}] first: [${"aaa".replaceFirst("a", "b")}] [${"axa".replace("x", "xx")}]")
    // 空 old 抛参数错误；无匹配返回内容相等串
    Console.println("replace-empty-old caught: ${caughtArgStr(func{(): String -> "a".replace("", "b")})} no-match: ${"abc".replace("x", "y") == "abc"}")
    // trim：Unicode 17.0 White_Space 固定集合——tab(0x09)、NBSP(U+00A0)、
    // 全角空格(U+3000) trim 掉，不删除内部空白（§4.3.3）
    const tabC: char = (9 as char)
    const nbspC: char = (0xA0 as char)
    const ideospaceC: char = (0x3000 as char)
    const t1 = "${tabC}${nbspC}a b${ideospaceC}".trim()
    const t2 = "${nbspC}a b".trimStart()
    const t3 = "a b${tabC}".trimEnd()
    Console.println("trim: [${t1}] [${t2}] [${t3}]")
    // U+200B（零宽空格）/U+FEFF（BOM）不属 White_Space——不 trim；
    // U+1680（蒙古文元音分隔空白）trim 掉
    const zwspC: char = (0x200B as char)
    const bomC: char = (0xFEFF as char)
    const mvsC: char = (0x1680 as char)
    const z1 = "${zwspC}x".trim()
    const z2 = "x${bomC}".trimEnd()
    const z3 = "${mvsC}x".trimStart()
    Console.println("trim2: ${z1 == "${zwspC}x"} ${z2 == "x${bomC}"} ${z3 == "x"}")
    // join：保留空元素，空序列得空串，单次遍历（计数枚举器验证）
    const src = arrayOfElements\<String>("a", "", "b")
    const counter = new CountingEnumerable(core.collections.asEnumerable(src))
    const joined = "--".join(counter)
    const emptyJoined = "--".join(core.collections.asEnumerable(arrayOf\<String>(0)))
    Console.println("join: [${joined}] one: ${counter.createdCount()} empty: [${emptyJoined}]")
}

// ── 探针辅助 ──

priv func isNullChar(c: char?): bool {
    return (c if? 'q') == 'q'
}

priv func isNullI64(v: i64?): bool {
    return (v if? (0 as i64)) == (0 as i64)
}

// 捕获 core.OutOfBoundException 的负例辅助（i64 调用体）
priv func caughtOobI64(body: core.Func\<i64?>): bool {
    try {
        body.call()
        return false
    } catch (e: core.OutOfBoundException) {
        return true
    }
}

// 捕获 core.text.TextArgumentException 的负例辅助（数组调用体）
priv func caughtArgArr(body: core.Func\<Array\<String>>): bool {
    try {
        body.call()
        return false
    } catch (e: core.text.TextArgumentException) {
        return true
    }
}

// 捕获 core.text.TextArgumentException 的负例辅助（字符串调用体）
priv func caughtArgStr(body: core.Func\<String>): bool {
    try {
        body.call()
        return false
    } catch (e: core.text.TextArgumentException) {
        return true
    }
}

// 计数可枚举体：包装既有 IEnumerable，统计枚举器创建次数（join 单次
// 遍历断言用，§4.3.3）
priv class CountingEnumerable implements core.collections.IEnumerable\<String> {
    priv const wrapped: core.collections.IEnumerable\<String>
    priv var created: i64

    pub init(src: core.collections.IEnumerable\<String>) {
        wrapped = src
        created = (0 as i64)
    }

    pub func createdCount(): i64 {
        return created
    }

    pub override func iterate(): core.collections.IEnumerator\<String> {
        created = (created + (1 as i64))
        return wrapped.iterate()
    }
}
