// F2 访问器签名闸（负例）：priv 字段 + 显式 pub 访问器——访问器签名
// = 字段类型、可见性可独立于字段（§9.4.1），pub getter/setter 把私有
// 类型泄出（修复前访问器签名不参检）。
// expect-error: Inconsistent accessibility: return type 'Hidden' is less accessible than getter 'x'
// expect-error: Inconsistent accessibility: parameter type 'Hidden' is less accessible than setter 'x'
class Hidden {
    pub init()
}
pub class C {
    pub init(_ -> x)
    priv var x: Hidden {
        pub get
        pub set
    }
}
pub func main(): i32 { return 0 }
