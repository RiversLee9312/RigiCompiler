// DA 反例矩阵（§9.3）：无 init 直接 new / 分支漏赋值 / 不调 super 且基类有义务字段
pub struct P {
    pub var x: i32
}

pub class Branch {
    pub var x: i32
    pub init(cond: bool) {
        if (cond) {
            x = 1
        }
    }
}

pub open class BaseNeed {
    pub var b: i32
    pub init(_ -> b)
}

pub class DerivedNoSuper : BaseNeed {
    pub init() {
    }
}

pub func main(): i32 {
    const p = new P()
    return 0
}
