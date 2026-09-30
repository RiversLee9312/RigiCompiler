// Rigi 标准库：core.collections 的通用集合算法（STDLIB §4.2.3 通用算法与
// 排序）。
//
// 契约要点（§4.2.3，逐条落实到各函数注释）：
// - API 组织（§4.2.1 第一段）：通用算法使用本 NS 的顶层泛型函数（如
//   map(source, selector)），**不做扩展成员注入**——本文件全部是
//   namespace core.collections 下的顶层 pub func，不向集合类型附加成员。
// - 集合变换**立即求值**：map/filter 返回新的 List；toArray/toList 创建
//   独立容器；复制元素值而不隐式深复制引用对象（浅复制：元素仍是同一
//   引用实例，仅容器相互独立）。
// - 输入按顺序**只遍历一次**：一律经 IEnumerable<T>.iterate() 取一个
//   枚举器顺序推进；map/filter 对访问到的元素各调用一次回调；不预先
//   枚举计数后再遍历（toArray 也是单次遍历：先收进 List 再拷贝到精确
//   尺寸 Array，输入仍只被枚举一次）。
// - **短路**：any/all/contains/find/findIndex 在结果确定后立即停止遍历
//   （不再推进枚举器、不再调用回调）。
// - 空序列：any 为 false、all 为 true、fold 返回初始值（回调零调用）。
// - contains 判等沿用既有 ==（equals-or-hash 判等链，List/Map/Set 同款
//   通道），不涉 toString。
// - find 返回 Pair<bool, T?> 区分「命中 null」与「未命中」（首项 bool）；
//   findIndex 返回零基 i64?，未命中为 null。
// - 回调语义：predicate/selector 是**普通函数**——在当前协程运行、可以
//   挂起、不自动创建 Task（普通 lambda 的既有对象模型，SYNTAX §5.2/§5.4）。
//   不得在回调中重入修改正在操作的输入或目标集合：对 List/Map/Set/Queue
//   输入，回调中的修改会经其枚举器的既有修改检测自然抛
//   core.IllegalStateException（§4.2.4，属既定行为，本文件不额外设防）；
//   经 Array/Span 借用适配器的输入遍历期间写入属不支持的用法（§4.2.4
//   末段，不承诺检测）。回调抛出的异常按既有单异常模型原样传播，不包装
//   不吞并；用户副作用不自动回滚。
// - 无序列化语义（算法是纯函数，不引入容器形态），不应用序列化注解。
// - 排序（sorted/sortInPlace）显式**稳定**：相等元素保持原相对顺序
//  （迭代式自底向上归并排序，文件尾三个 priv 助手承载核心）；排序显式
//   接收 Func<ComparisonResult, T, T>（首个泛型参数是返回类型），不假定
//   无约束 T 能使用 `<`。sortInPlace 先把元素快照进临时存储完成全部
//   比较与排序，再整体写回——比较器抛错时目标集合尚未被修改（该保证
//   不回滚回调对外部对象的副作用；原地排序不是跨协程事务）。
namespace core.collections

// map（§4.2.3）：对每个元素调用 selector 并把结果按序收进**新 List<R>**
//（立即求值：返回时全部元素已变换完成；输入恰被枚举一次，每个访问到的
// 元素各调一次 selector）。不修改输入；元素值复制（浅：引用元素两容器
// 共享实例）。
pub func map\<T, R>(source: IEnumerable\<T>, selector: Func\<R, T>): List\<R> {
    const result = new List\<R>()
    // 单次遍历：iterate() 取一个枚举器顺序推进到底（不预计数、不回退）
    const it = source.iterate()
    while (it.moveNext()) {
        // selector 是普通函数调用（当前协程；抛错原样传播并放弃 result）
        result.add(selector(it.current()))
    }
    return result
}

// filter（§4.2.3）：predicate 为 true 的元素按原顺序收进**新 List<T>**
//（立即求值；输入恰被枚举一次，每个访问到的元素各调一次 predicate；
// 全部元素都会被访问——filter 无短路语义）。浅复制元素值。
pub func filter\<T>(source: IEnumerable\<T>, predicate: Func\<bool, T>): List\<T> {
    const result = new List\<T>()
    const it = source.iterate()
    while (it.moveNext()) {
        const item = it.current()
        if (predicate(item)) {
            result.add(item)
        }
    }
    return result
}

// fold（§4.2.3）：显式接收初始值，按序折叠 acc = folder(acc, item)；
// 空序列直接返回初始值（folder 零调用）。输入恰被枚举一次。
pub func fold\<T, R>(source: IEnumerable\<T>, initial: R, folder: Func\<R, R, T>): R {
    var acc: R = initial
    const it = source.iterate()
    while (it.moveNext()) {
        // folder 首参是累积值、次参是当前元素（Func 首个泛型参数是返回
        // 类型：Func<R, R, T> 即 call(acc: R, item: T): R）
        acc = folder(acc, it.current())
    }
    return acc
}

// any（§4.2.3）：存在使 predicate 为 true 的元素即返回 true；**短路**——
// 命中后立即停止遍历（后续元素不再访问、predicate 不再调用）。空序列
// 返回 false（predicate 零调用）。
pub func any\<T>(source: IEnumerable\<T>, predicate: Func\<bool, T>): bool {
    const it = source.iterate()
    while (it.moveNext()) {
        if (predicate(it.current())) {
            return true
        }
    }
    return false
}

// all（§4.2.3）：所有元素都使 predicate 为 true 才返回 true；**短路**——
// 首个 false 即返回 false 并停止遍历。空序列返回 true（vacuous truth，
// predicate 零调用）。
pub func all\<T>(source: IEnumerable\<T>, predicate: Func\<bool, T>): bool {
    const it = source.iterate()
    while (it.moveNext()) {
        if ((predicate(it.current())) == false) {
            return false
        }
    }
    return true
}

// contains（§4.2.3）：是否含与 item 相等的元素；**短路**——命中即停。
// 判等沿用既有 ==（equals-or-hash 判等链，与 List.contains/Map 键/Set
// 元素同通道，绝不涉 toString）：NaN 不等于自身是 == 的自然结果，±0 按
// 数值相等。T 实例化为可空类型时 null 元素与 null 实参按 == 的 null 判等
//（同 List.slotEquals 的 null 通道）。输入恰被枚举一次。
pub func contains\<T>(source: IEnumerable\<T>, item: T): bool {
    const it = source.iterate()
    while (it.moveNext()) {
        if (it.current() == item) {
            return true
        }
    }
    return false
}

// find（§4.2.3）：首个使 predicate 为 true 的元素以 Pair(true, 元素) 返回
// 并**短路**（后续不再访问）；遍历结束仍未命中返回 Pair(false, null)。
// 首项 bool 使「命中 null 元素」（T 实例化为可空类型时）与「未命中」
// 可区分：二者第二项都是 null，由首项裁决。T → T? 恒可赋值（§3.4），
// 命中值经可空视图装入 Pair。
pub func find\<T>(source: IEnumerable\<T>, predicate: Func\<bool, T>): core.Pair\<bool, T?> {
    const it = source.iterate()
    while (it.moveNext()) {
        const item = it.current()
        if (predicate(item)) {
            const boxed: T? = item
            return new core.Pair\<bool, T?>(true, boxed)
        }
    }
    return new core.Pair\<bool, T?>(false, null)
}

// findIndex（§4.2.3）：首个命中元素的**零基**位置（i64?，与 List.length/
// indexOf 同宽）；**短路**——命中即停；未命中返回 null。空序列返回 null。
pub func findIndex\<T>(source: IEnumerable\<T>, predicate: Func\<bool, T>): i64? {
    var index: i64 = (0 as i64)
    const it = source.iterate()
    while (it.moveNext()) {
        if (predicate(it.current())) {
            return index
        }
        index = (index + (1 as i64))
    }
    return null
}

// toArray（§4.2.3）：创建**独立** Array<T>（长度恰为元素个数）。仍是输入
// 的单次遍历：先把元素收进动态 List（不预先枚举计数后再遍历——那会对
// 输入枚举两次），随后对已收好的 List 做一次内存内拷贝到精确尺寸数组
//（拷贝阶段不再触达输入）。Array 长度为 i32（§4.2.1 宽度规则）；元素
// 个数超过 i32 上限时 List.add 经 grow 的容量检查先抛
// core.OutOfBoundException，此处 length 不会窄化回绕。
pub func toArray\<T>(source: IEnumerable\<T>): Array\<T> {
    // 第一段：单次遍历输入，收进动态缓冲
    const buffer = new List\<T>()
    const it = source.iterate()
    while (it.moveNext()) {
        buffer.add(it.current())
    }
    // 第二段：精确尺寸独立数组（浅复制元素值：引用元素仍是同一实例）
    const result = arrayOf\<T>((buffer.length as i32))
    var i: i64 = (0 as i64)
    while (i < buffer.length) {
        // getAtIndex 界内读非 null（i 恒在 0..length-1）；泛型参数的
        // Nullable<T> 不参与 smart cast 收窄（§3.5/S9a），as 恒必要
        result[(i as i32)] = (buffer.getAtIndex(i) as T)
        i = (i + (1 as i64))
    }
    return result
}

// toList（§4.2.3）：创建**独立**新 List<T>（与输入容器互不影响——增删
// 任一侧都不改变另一侧；浅复制元素值：引用元素两容器共享同一实例）。
// 输入恰被枚举一次。map/filter 即「变换 + 本函数收集」的组合语义。
pub func toList\<T>(source: IEnumerable\<T>): List\<T> {
    const result = new List\<T>()
    const it = source.iterate()
    while (it.moveNext()) {
        result.add(it.current())
    }
    return result
}

// sorted（§4.2.3 排序）：按 comparer 升序返回**新 List<T>**——**稳定**
// 排序（比较结果相等的元素保持原相对顺序），实现为迭代式自底向上归并
// 排序（简洁且最坏 O(n log n)，无需递归泛型）。输入恰被枚举一次（先收
// 进临时存储，全部比较与排序在临时 Array 上完成，**不修改输入**）；
// 浅复制元素值。comparer 是普通函数——在当前协程运行、可以挂起、不自动
// 创建 Task；调用者提供的 comparer 须保持自洽、传递且在本次排序期间稳定
//（§4.2.3，本函数不校验）。comparer 抛出的异常按既有单异常模型原样传播：
// 此时返回值尚未产生、输入未受影响（可能已产生的临时快照随之废弃）。
pub func sorted\<T>(source: IEnumerable\<T>, comparer: Func\<core.ComparisonResult, T, T>): List\<T> {
    // 第一段：单次遍历输入，元素收进动态缓冲（不预先枚举计数后再遍历）
    const buffer = new List\<T>()
    const it = source.iterate()
    while (it.moveNext()) {
        buffer.add(it.current())
    }
    // 第二段：缓冲转入临时 Array（比较与排序都在临时存储上进行）
    const n: i64 = buffer.length
    const items = arrayOf\<T>((n as i32))
    var fill: i64 = (0 as i64)
    while (fill < n) {
        items[(fill as i32)] = (buffer.getAtIndex(fill) as T)
        fill = (fill + (1 as i64))
    }
    // 第三段：稳定归并排序（0/1 元素时排序循环自然零趟）
    const done = mergeSortBuffer\<T>(items, comparer)
    // 第四段：排序结果按序装入新 List 返回（与输入容器互不影响）
    const result = new List\<T>()
    var w: i64 = (0 as i64)
    while (w < n) {
        result.add((done[(w as i32)] as T))
        w = (w + (1 as i64))
    }
    return result
}

// sortInPlace（§4.2.3 排序）：对目标 List 原地**稳定**排序（与 sorted
// 同一归并核心、同一稳定性契约）。§4.2.3 的事故保证：先把元素整体快照
// 进临时 Array，全部比较与排序在快照上完成后**再整体写回**——比较器
// 抛错时目标集合尚未被修改、保持原内容（该保证不回滚回调对外部对象
// 造成的副作用；原地排序不是跨协程事务）。写回走 List 公开 API
// setAtIndex（写回相同值同样计修改），自然触发既有修改检测使已有枚举器
// 失效（§4.2.4：原地排序使枚举器失效）；空表无元素可写回，比较成功后
// 经 List 内部入口显式标记失效。comparer 契约同 sorted（普通函数/
// 可挂起/须自洽传递且期间
// 稳定，异常原样传播且此时目标未被修改）。
pub func sortInPlace\<T>(target: List\<T>, comparer: Func\<core.ComparisonResult, T, T>) {
    // 第一段：目标元素整体快照进临时 Array——此后比较器抛错也不及目标
    const n: i64 = target.length
    const snapshot = arrayOf\<T>((n as i32))
    var r: i64 = (0 as i64)
    while (r < n) {
        snapshot[(r as i32)] = (target.getAtIndex(r) as T)
        r = (r + (1 as i64))
    }
    // 第二段：快照上稳定归并排序
    const done = mergeSortBuffer\<T>(snapshot, comparer)
    // 空表没有 setAtIndex 写回；排序成功后仍须令先前的枚举器失效。
    if (n == (0 as i64)) { target.invalidateAfterEmptySort() }
    // 第三段：整体写回（公开 setAtIndex 通道，逐槽触发 §4.2.4 修改失效）
    var w: i64 = (0 as i64)
    while (w < n) {
        target.setAtIndex(w, (done[(w as i32)] as T))
        w = (w + (1 as i64))
    }
}

// ── 比较辅助函数（§4.2.3：提供常用标量和 String 的比较辅助）──
// 语言不假定无约束 T 能使用 `<`（排序比较由 operator compareTo 承载，
// SYNTAX §13.2），因此比较辅助**按类型给同名重载**而非单泛型函数；
// 数值重载一律沿用既有内建 `<`/`>`（由 compareTo 三态推导），本文件
// 不重复实现任何数值次序语义。String 重载例外：语言至今未给 String
// 提供次序比较运算（内建运算面只有 ==/!=/+），该重载按 §4.3.2 的
// 标量字典序契约自行实现（经 toUtf8Span 字节通道，见下方注释）。

// 整型重载（i8/i16/i32/i64）：内建数值序（有符号直接比较）。
pub func compare(a: i8, b: i8): core.ComparisonResult {
    if (a < b) { return .LesserThanAnother }
    if (a > b) { return .GreaterThanAnother }
    return .Equal
}

pub func compare(a: i16, b: i16): core.ComparisonResult {
    if (a < b) { return .LesserThanAnother }
    if (a > b) { return .GreaterThanAnother }
    return .Equal
}

pub func compare(a: i32, b: i32): core.ComparisonResult {
    if (a < b) { return .LesserThanAnother }
    if (a > b) { return .GreaterThanAnother }
    return .Equal
}

pub func compare(a: i64, b: i64): core.ComparisonResult {
    if (a < b) { return .LesserThanAnother }
    if (a > b) { return .GreaterThanAnother }
    return .Equal
}

// 无符号整型重载（u8/u16/u32/u64）：内建数值序（无符号直接比较，非
// 补码位型序）。
pub func compare(a: u8, b: u8): core.ComparisonResult {
    if (a < b) { return .LesserThanAnother }
    if (a > b) { return .GreaterThanAnother }
    return .Equal
}

pub func compare(a: u16, b: u16): core.ComparisonResult {
    if (a < b) { return .LesserThanAnother }
    if (a > b) { return .GreaterThanAnother }
    return .Equal
}

pub func compare(a: u32, b: u32): core.ComparisonResult {
    if (a < b) { return .LesserThanAnother }
    if (a > b) { return .GreaterThanAnother }
    return .Equal
}

pub func compare(a: u64, b: u64): core.ComparisonResult {
    if (a < b) { return .LesserThanAnother }
    if (a > b) { return .GreaterThanAnother }
    return .Equal
}

// 浮点重载（float/double）落实 §4.2.3 的浮点排序规则：NaN 排在所有非
// NaN 值之后、NaN 之间排序相等、±0 排序相等，其余按数值序。**此规则
// 只作用于本排序辅助**：不改变 `==` 语义（NaN != NaN、±0 相等的既有
// 数值相等规则），也不改变 Map/Set 的判等通道。
pub func compare(a: float, b: float): core.ComparisonResult {
    // NaN 判定：NaN 是唯一不等于自身的值（x == x 为 false 即 NaN）
    if (not (a == a)) {
        if (not (b == b)) { return .Equal }     // 双 NaN：排序相等
        return .GreaterThanAnother              // a NaN、b 非 NaN：a 排尾
    }
    if (not (b == b)) { return .LesserThanAnother } // b NaN、a 非 NaN：b 排尾
    // 非 NaN 段：内建数值序；±0 经数值 == 天然相等（排序相等）
    if (a < b) { return .LesserThanAnother }
    if (a > b) { return .GreaterThanAnother }
    return .Equal
}

pub func compare(a: double, b: double): core.ComparisonResult {
    // NaN 判定与规则同 float 重载（§4.2.3 浮点排序规则两宽度一致）
    if (not (a == a)) {
        if (not (b == b)) { return .Equal }     // 双 NaN：排序相等
        return .GreaterThanAnother              // a NaN、b 非 NaN：a 排尾
    }
    if (not (b == b)) { return .LesserThanAnother } // b NaN、a 非 NaN：b 排尾
    // 非 NaN 段：内建数值序；±0 经数值 == 天然相等（排序相等）
    if (a < b) { return .LesserThanAnother }
    if (a > b) { return .GreaterThanAnother }
    return .Equal
}

// String 重载（§4.2.3「String 的比较辅助函数」+ §4.3.2 次序统一契约）：
// Unicode **标量字典序**——与合法 UTF-8 的**无符号字节字典序**等价
//（UTF-8 编码保持标量序：多字节序列首字节区间随标量区间单调，续字节
// 0x80-0xBF 不与任何标量起始字节混淆，故首个不等字节必落在同一位置
// 的两个完整标量编码之间且序与标量值一致）；公共前缀逐字节相等时
// **短串在前**（memcmp 同款语义）。实现经 toUtf8Span（§4.3.1，独立
// 字节副本）逐字节 u8 无符号比较，与 native 侧 rigi_string_compare
//（rigi_rt/shim.c）同语义——双宿主同源；==/!= 的内容相等不受影响
//（UTF-8 编码唯一，字节序列相等当且仅当串相等）。
pub func compare(a: String, b: String): core.ComparisonResult {
    // 两串各拷出一份 UTF-8 字节（toUtf8Span 返回独立副本，互不影响）
    const left = a.toUtf8Span()
    const right = b.toUtf8Span()
    // 公共前缀段逐字节比较：首个不等字节即定序（u8 内建无符号数值序）
    var common: i32 = left.length
    if (right.length < common) { common = right.length }
    var i: i32 = 0
    while (i < common) {
        // 界内读非 null（i 恒在 0..shared-1），if? 解包哨兵不生效
        const x = (left[i] if? (0 as u8))
        const y = (right[i] if? (0 as u8))
        if (x < y) { return .LesserThanAnother }
        if (x > y) { return .GreaterThanAnother }
        i = (i + 1)
    }
    // 公共前缀耗尽：短串在前（前缀相同时短者判 Lesser）
    if (left.length < right.length) { return .LesserThanAnother }
    if (left.length > right.length) { return .GreaterThanAnother }
    return .Equal
}

// ── 排序内部助手（priv）：迭代式自底向上归并排序 ──
// 归并排序选择理由：**稳定**（相等元素保持原相对顺序是 §4.2.3 契约）、
// 最坏 O(n log n)、迭代实现无需递归泛型实例化。排序全程只经 comparer
// 比较，不要求 T 具备任何运算符。

// 归并一步：把 src 的两个相邻有序段 [lo,mid) 与 [mid,hi) 稳定归并进
// dst 的 [lo,hi)（dst 同段在写前不被读，每轮全宽覆盖）。
priv func mergeRun\<T>(src: Array\<T>, lo: i64, mid: i64, hi: i64, dst: Array\<T>, comparer: Func\<core.ComparisonResult, T, T>) {
    var i: i64 = lo
    var j: i64 = mid
    var k: i64 = lo
    while ((i < mid) and (j < hi)) {
        // 稳定性关键：左段元素「不晚于」右段元素时取左段——比较并列
        // （Equal）时左段元素原本就在右段元素之前，先出即保持原相对顺序
        if (notAfter\<T>((src[(i as i32)] as T), (src[(j as i32)] as T), comparer)) {
            dst[(k as i32)] = (src[(i as i32)] as T)
            i = (i + (1 as i64))
        } else {
            dst[(k as i32)] = (src[(j as i32)] as T)
            j = (j + (1 as i64))
        }
        k = (k + (1 as i64))
    }
    // 某段耗尽：剩余段（本就有序）按序抄入
    while (i < mid) {
        dst[(k as i32)] = (src[(i as i32)] as T)
        i = (i + (1 as i64))
        k = (k + (1 as i64))
    }
    while (j < hi) {
        dst[(k as i32)] = (src[(j as i32)] as T)
        j = (j + (1 as i64))
        k = (k + (1 as i64))
    }
}

// 归并排序主体：对 items[0..length) 稳定排序，返回承载结果的缓冲（两
// 缓冲逐轮交替：宽度 1、2、4、… 各归并一趟，最后一趟的结果在返回值）。
// 0/1 元素时循环零趟、原缓冲返回（零比较）。宽度全程 i64（长度上限为
// i32 上限，翻倍不会溢出）。
priv func mergeSortBuffer\<T>(items: Array\<T>, comparer: Func\<core.ComparisonResult, T, T>): Array\<T> {
    const n: i64 = (items.length as i64)
    const scratch = arrayOf\<T>((n as i32))
    var active: Array\<T> = items
    var other: Array\<T> = scratch
    var width: i64 = (1 as i64)
    while (width < n) {
        var start: i64 = (0 as i64)
        while (start < n) {
            // 相邻两有序段 [start, start+width) 与 [start+width, start+2*width)，
            // 越过尾部时收敛到 n（乘法保持 i64 同型——字面量显式 as）
            var mid: i64 = (start + width)
            if (mid > n) { mid = n }
            var hi: i64 = (start + (width * (2 as i64)))
            if (hi > n) { hi = n }
            mergeRun\<T>(active, start, mid, hi, other, comparer)
            start = (start + (width * (2 as i64)))
        }
        // 一趟完成：交换两缓冲角色（active 成为新一轮的有序段来源）
        const swap = active
        active = other
        other = swap
        width = (width * (2 as i64))
    }
    return active
}

// comparer 结果判「a 不晚于 b」（LesserThanAnother 或 Equal）：归并取左
// 段的条件，并列时先取左段即稳定性的全部来源。经 is 判 case（枚举比较
// 不走数值通道）。
priv func notAfter\<T>(a: T, b: T, comparer: Func\<core.ComparisonResult, T, T>): bool {
    if (comparer(a, b) is .GreaterThanAnother) { return false }
    return true
}
