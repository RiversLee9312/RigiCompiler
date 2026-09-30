// STDLIB §4.2.4：空 List 成功原地排序仍使排序前的枚举器失效。
// moveNext 与尚未开始的 current 都必须优先报告 IllegalStateException。
// expect-output: empty-sort-move-ise
// expect-output: empty-sort-current-ise
// expect-exit: 0
import core.collections.*

pub func main(): i32 {
    const empty = new List\<i32>()
    const oldMove = empty.iterate()
    const oldCurrent = empty.iterate()
    sortInPlace\<i32>(empty, func{(a: i32, b: i32): core.ComparisonResult -> compare(a, b)})
    if (empty.length != (0 as i64)) { return 1 }

    var moveInvalid = false
    try {
        oldMove.moveNext()
    } catch (e: core.IllegalStateException) {
        moveInvalid = true
    }
    if (moveInvalid == false) { return 2 }
    core.io.Console.println("empty-sort-move-ise")

    var currentInvalid = false
    try {
        oldCurrent.current()
    } catch (e: core.IllegalStateException) {
        currentInvalid = true
    }
    if (currentInvalid == false) { return 3 }
    core.io.Console.println("empty-sort-current-ise")
    return 0
}
