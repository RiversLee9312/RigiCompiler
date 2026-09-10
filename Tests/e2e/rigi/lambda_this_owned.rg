// 闭包离开宿主工厂、跨挂起及容器移除后仍须保活 this，包含宿主↔闭包的环。
// expect-output: capture-owned-ok
// expect-exit: 0
class Owner {
    pub var value: i32
    pub var text: String
    pub var retained: Func\<String>?
    pub init(_ -> value, _ -> text) { retained = null }
    pub func reader(): Func\<String> {
        var result = func{(): String -> { yield
            return@_ this.text + this.value.toString()
        }}
        retained = result
        return result
    }
}

func make(n: i32): Func\<String> {
    var owner = new Owner(n, "owner-")
    return owner.reader()
}

pub func main(): i32 {
    var readers = new core.collections.List\<Func\<String>>()
    var i = 0
    while (i < 96) {
        readers.add(make(i))
        i = i + 1
    }
    i = 0
    while (i < 96) {
        var read = readers.getAtIndex(i as i64) as Func\<String>
        if (read() != ("owner-" + i.toString())) { return 1 }
        i = i + 1
    }
    var last = make(123)
    i = 0
    while (i < 96) {
        readers.removeAt(0 as i64)
        i = i + 1
    }
    if (last() != "owner-123") { return 2 }
    core.io.Console.println("capture-owned-ok")
    return 0
}
