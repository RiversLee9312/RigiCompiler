// 独立宿主参数：inner 的值宿主写入、空/有状态环、复制后重新绑定和多次交错调用。
// expect-output: self-parameter-ok
// expect-exit: 0
@WrapperTarget(.Entity)
wrapper CountCalls\<TTarget> {
    pub var calls: i32
    pub init() { calls = 0 }
    operator .proxy.bump(delta: i32): i32 {
        calls = calls + 1
        var result = inner(delta)
        var current = self as Counter
        return (result + current.value) + calls
    }
}

@WrapperTarget(.Entity)
wrapper Forward {
    pub init()
    operator .proxy.bump(delta: i32): i32 { return inner(delta) }
}

@CountCalls
@Forward
struct Counter {
    pub var value: i32
    pub init(_ -> value)
    pub func bump(delta: i32): i32 {
        value = value + delta
        return value
    }
}

@WrapperTarget(.Entity)
wrapper Observe\<TTarget> {
    pub init()
    operator .proxy.bump(delta: i32): i32 {
        var before = (self as ObjectCounter).value
        var result = inner(delta)
        var after = (self as ObjectCounter).value
        return (result + before) + after
    }
}

@Observe
class ObjectCounter {
    pub var value: i32
    pub init(_ -> value)
    pub func bump(delta: i32): i32 {
        value = value + delta
        return value
    }
}

pub func main(): i32 {
    var a = new Counter(2)
    var b = a
    if (a.bump(3) != 11) { return 1 }
    if (a.value != 5) { return 2 }
    if (b.bump(7) != 19) { return 3 }
    if (b.value != 9) { return 4 }
    var c = a
    if (c.bump(1) != 14) { return 5 }
    if (a.value != 5) { return 6 }
    var box: Any = c
    var copied = box as Counter
    if (copied.bump(2) != 19) { return 7 }
    var object = new ObjectCounter(4)
    if (object.bump(3) != 18) { return 8 }
    var i = 0
    while (i < 64) {
        var current = new Counter(i)
        if (current.bump(1) != (((i + 1) * 2) + 1)) { return 9 }
        if (current.value != (i + 1)) { return 10 }
        i = i + 1
    }
    core.io.Console.println("self-parameter-ok")
    return 0
}
