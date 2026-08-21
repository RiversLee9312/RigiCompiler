// DA 正例矩阵（§9.3）：初值 / 映射 / 体赋值 / Nullable / 调 super 的子类
import core.io.Console

pub class WithInit {
    pub var x: i32 = 41
}

pub class WithMapping {
    pub var x: i32
    pub init(_ -> x)
}

pub class WithBody {
    pub var x: i32
    pub var s: String
    pub init(v: i32) {
        x = v
        s = "v=" + v.toString()
    }
}

pub class WithNullable {
    pub var x: i32?
    pub init()
}

pub open class Base {
    pub var b: i32
    pub init(_ -> b)
}

pub class Derived : Base {
    pub var d: i32
    pub init(_ -> d, bv: i32) {
        super(bv)
    }
}

pub func main(): i32 {
    const a = new WithInit()
    const b = new WithMapping(1)
    const c = new WithBody(2)
    const d = new WithNullable()
    const e = new Derived(3, 4)
    Console.println((((a.x + b.x) + c.x) + e.d).toString())
    return 0
}
