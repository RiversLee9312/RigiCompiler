// 非 rich wrapper 的 self 不是普通字段；值宿主复制后必须访问当前宿主。
// expect-output: wrapper-ok
// expect-exit: 0
@WrapperTarget(.Entity)
wrapper ReadSelf\<TTarget> {
    pub init()
    operator .proxy.read(): i32 {
        var current = self as Sample
        return inner() + current.value
    }
}

@ReadSelf
struct Sample {
    pub var value: i32
    pub init(_ -> value)
    pub func read(): i32 { return value }
}

func make(value: i32): Sample { return new Sample(value) }
func identity\<T>(value: T): T { return value }

struct Nested {
    pub var sample: Sample
    pub init(_ -> sample)
}

pub func main(): i32 {
    var original = make(3)
    var copy = original
    copy.value = 7
    if (original.read() != 6) { return 1 }
    if (copy.read() != 14) { return 2 }
    var nested = new Nested(copy)
    nested.sample.value = 9
    if (nested.sample.read() != 18) { return 4 }
    var boxed: Any = nested.sample
    var unboxed = boxed as Sample
    unboxed.value = 11
    if (unboxed.read() != 22) { return 5 }
    var passed = identity\<Sample>(unboxed)
    passed.value = 13
    if (passed.read() != 26) { return 6 }
    var list = new core.collections.List\<Sample>()
    var round = 0
    while (round < 64) {
        var item = make(round)
        if (item.read() != (round + round)) { return 3 }
        list.add(item)
        round = round + 1
    }
    round = 0
    while (round < 64) {
        var item = list.getAtIndex(round as i64) as Sample
        if (item.read() != (round + round)) { return 7 }
        round = round + 1
    }
    core.io.Console.println("wrapper-ok")
    return 0
}
