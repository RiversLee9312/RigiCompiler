// STDLIB §4.2.1/§4.2.2/§4.2.4（施工块 1-5）：Queue 基本行为语料。
// FIFO 出队与出队序遍历；peek 看队首不出队；环形回绕（入队出队交替迫使
// 游标回绕后顺序与 count 不变）；扩容（初始容量 8 而元素多，含回绕态下
// 扩容，顺序不变）；空队 dequeue/peek 抛 core.NoSuchElementException（失败
// 操作不计修改、容器仍可继续使用）；tryDequeue/tryPeek 空队返回 (false, null)；
// 可空元素 Queue\<String?> 存 null 后 tryDequeue 返回 (true, null) 与空队
// (false, null) 由首项区分，dequeue/peek 正常返回 null 不抛。
// 修改检测（§4.2.4）：成功的 enqueue/dequeue/tryDequeue/clear（含空队
// clear）计修改，使既有枚举器失效——之后的 moveNext/current 抛
// core.IllegalStateException（失效优先于正常结束）；peek/tryPeek 不修改、
// 空队失败操作不计修改，枚举器照常推进。首次 moveNext 前或正常返回 false
// 后 current 抛 core.NoSuchElementException；结束后 moveNext 持续 false。
// expect-output: queue-fifo-ok
// expect-output: a;b;c;
// expect-output: queue-order-ok
// expect-output: 4;5;10;11;12;13;14;15;16;17;18;19;
// expect-output: 0;1;2;3;4;5;6;7;8;9;10;11;12;13;14;15;16;17;18;19;20;21;22;23;24;25;26;27;28;29;
// expect-output: queue-empty-dequeue
// expect-output: queue-empty-peek
// expect-output: queue-trynull-ok
// expect-output: queue-reuse-ok
// expect-output: queue-nullable-ok
// expect-output: queue-ise-enqueue
// expect-output: queue-ise-current
// expect-output: queue-ise-dequeue
// expect-output: queue-ise-trydequeue
// expect-output: queue-peek-noeffect-ok
// expect-output: queue-ise-clear
// expect-output: queue-ise-clear-empty
// expect-output: queue-empty-dequeue2
// expect-output: queue-fail-noeffect-ok
// expect-output: queue-noelem-fresh
// expect-output: queue-noelem-end
// expect-output: queue-ise-after-end
// expect-exit: 0
import core.collections.*

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("Queue 断言失败 " + step.toString()) }
}

pub func main(): i32 {
    // ── FIFO：enqueue/dequeue/peek/count 与出队序遍历 ──
    const q = new Queue\<String>()
    require((q.count == (0 as i64)))
    q.enqueue("a")
    q.enqueue("b")
    q.enqueue("c")
    require((q.count == (3 as i64)))
    // peek 看队首不出队，count 不变
    require((q.peek() == "a"))
    require((q.count == (3 as i64)))
    core.io.Console.println("queue-fifo-ok")
    // iterate 按 FIFO 出队顺序（队首→队尾）
    const it = q.iterate()
    var acc: String = ""
    while (it.moveNext()) {
        acc = (acc + (it.current() + ";"))
    }
    core.io.Console.println(acc)
    // 出队顺序 a;b;c（FIFO）
    require((q.dequeue() == "a"))
    require((q.dequeue() == "b"))
    require((q.count == (1 as i64)))
    q.clear()
    require((q.count == (0 as i64)))
    core.io.Console.println("queue-order-ok")

    // ── 环形回绕：入队出队交替迫使游标回绕后再验证顺序与 count ──
    // 初始容量 8：先入 6 个、出 4 个（head 前移到 4），再入 10 个跨物理
    // 末界回绕（途中 count 达 8 触发扩容，回绕态扩容后顺序仍保持）
    const qw = new Queue\<i32>()
    var i: i32 = 0
    while (i < 6) {
        qw.enqueue(i)
        i = (i + 1)
    }
    i = 0
    while (i < 4) {
        qw.dequeue()
        i = (i + 1)
    }
    i = 10
    while (i < 20) {
        qw.enqueue(i)
        i = (i + 1)
    }
    require((qw.count == (12 as i64)))
    // 出队序 = 4,5,10,11,...,19（FIFO 不因回绕/扩容改变）
    var accW: String = ""
    while (qw.count > (0 as i64)) {
        accW = (accW + (qw.dequeue().toString() + ";"))
    }
    core.io.Console.println(accW)
    require((qw.count == (0 as i64)))

    // ── 扩容：初始容量 8 而元素多（跨 16/32 两级倍增），顺序不变 ──
    const qg = new Queue\<i32>()
    var j: i32 = 0
    while (j < 30) {
        qg.enqueue(j)
        j = (j + 1)
    }
    require((qg.count == (30 as i64)))
    var accG: String = ""
    j = 0
    while (j < 30) {
        accG = (accG + (qg.dequeue().toString() + ";"))
        j = (j + 1)
    }
    core.io.Console.println(accG)

    // ── 空队：dequeue/peek 抛 NoSuchElementException ──
    const qe = new Queue\<String>()
    try {
        qe.dequeue()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("queue-empty-dequeue")
    }
    try {
        qe.peek()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("queue-empty-peek")
    }
    // 空队 tryDequeue/tryPeek：失败为 Pair(false, null)
    const td = qe.tryDequeue()
    require((td.key == false))
    require((td.value == null))
    const tp = qe.tryPeek()
    require((tp.key == false))
    require((tp.value == null))
    core.io.Console.println("queue-trynull-ok")
    // 抛错属失败操作，容器结构仍有效：可继续入队出队（§4.2.1）
    qe.enqueue("x")
    require((qe.count == (1 as i64)))
    require((qe.dequeue() == "x"))
    require((qe.count == (0 as i64)))
    core.io.Console.println("queue-reuse-ok")

    // ── 可空元素：Queue\<String?> 存 null 后 tryDequeue (true, null) 与
    // ── 空队 (false, null) 区分；dequeue/peek 正常返回 null 不抛 ──
    const qn = new Queue\<String?>()
    qn.enqueue(null)
    qn.enqueue("n1")
    require((qn.count == (2 as i64)))
    // peek 正常返回 null（与空队抛 NoSuchElementException 区分）
    require((qn.peek() == null))
    require((qn.count == (2 as i64)))
    const tn = qn.tryDequeue()
    require((tn.key == true))
    require((tn.value == null))
    const got: String? = qn.dequeue()
    // String? 与 String 不可直接 ==（P3 同型要求）：经 as 通道解包后比较
    //（泛型实例化为可空类型时，可空值解包 as 恒必要，§3.5/S9a 同款）
    require(((got as String) == "n1"))
    require((qn.count == (0 as i64)))
    const td2 = qn.tryDequeue()
    require((td2.key == false))
    require((td2.value == null))
    core.io.Console.println("queue-nullable-ok")

    // ── 修改检测：迭代中 enqueue → 下一次 moveNext 抛 IllegalStateException，
    // ── current 亦然（失效优先）──
    const qm = new Queue\<String>()
    qm.enqueue("a")
    qm.enqueue("b")
    const itAdd = qm.iterate()
    require(itAdd.moveNext())
    qm.enqueue("c")
    try {
        itAdd.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("queue-ise-enqueue")
    }
    try {
        itAdd.current()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("queue-ise-current")
    }

    // ── 迭代中 dequeue → 失效 ──
    const qd = new Queue\<String>()
    qd.enqueue("a")
    qd.enqueue("b")
    const itDeq = qd.iterate()
    require(itDeq.moveNext())
    qd.dequeue()
    try {
        itDeq.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("queue-ise-dequeue")
    }

    // ── 迭代中 tryDequeue 成功 → 同样计修改而失效 ──
    const qt = new Queue\<String>()
    qt.enqueue("a")
    qt.enqueue("b")
    const itTry = qt.iterate()
    require(itTry.moveNext())
    const tdr = qt.tryDequeue()
    require((tdr.key == true))
    try {
        itTry.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("queue-ise-trydequeue")
    }

    // ── peek/tryPeek 不修改、不计修改：枚举器照常推进到正常结束 ──
    const qp = new Queue\<String>()
    qp.enqueue("x")
    const itPk = qp.iterate()
    require(itPk.moveNext())
    require((qp.peek() == "x"))
    const tpr = qp.tryPeek()
    require((tpr.key == true))
    require((itPk.current() == "x"))
    require((itPk.moveNext()) == false)
    core.io.Console.println("queue-peek-noeffect-ok")

    // ── 迭代中 clear（非空）→ 失效；空队 clear 同样计修改（§4.2.4）──
    const qc = new Queue\<String>()
    qc.enqueue("a")
    const itClear = qc.iterate()
    require(itClear.moveNext())
    qc.clear()
    try {
        itClear.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("queue-ise-clear")
    }
    const qce = new Queue\<String>()
    const itEmpty = qce.iterate()
    qce.clear()
    try {
        itEmpty.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("queue-ise-clear-empty")
    }

    // ── 空队失败操作不计修改：枚举器不被误判失效，moveNext 持续 false ──
    const qf = new Queue\<String>()
    const itFail = qf.iterate()
    require((itFail.moveNext()) == false)
    try {
        qf.dequeue()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("queue-empty-dequeue2")
    }
    const tdf = qf.tryDequeue()
    require((tdf.key == false))
    require((tdf.value == null))
    // 若失败操作误计修改，此处将抛 IllegalStateException 而非持续 false
    require((itFail.moveNext()) == false)
    core.io.Console.println("queue-fail-noeffect-ok")

    // ── 首次 moveNext 前 current 抛 NoSuchElementException；正常结束后
    // ── current 抛、moveNext 持续 false；结束后修改队列 → 失效优先于结束 ──
    const qn2 = new Queue\<String>()
    qn2.enqueue("k")
    const itFresh = qn2.iterate()
    try {
        itFresh.current()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("queue-noelem-fresh")
    }
    require(itFresh.moveNext())
    require((itFresh.current() == "k"))
    require((itFresh.moveNext()) == false)
    try {
        itFresh.current()
        require(false)
    } catch (e: core.NoSuchElementException) {
        core.io.Console.println("queue-noelem-end")
    }
    require((itFresh.moveNext()) == false)
    qn2.enqueue("z")
    try {
        itFresh.moveNext()
        require(false)
    } catch (e: core.IllegalStateException) {
        core.io.Console.println("queue-ise-after-end")
    }
    return 0
}
