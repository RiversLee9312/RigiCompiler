// F2 字段签名闸（负例）：pub 字段持文件级私有类型——字段是泄漏源头
// 门（修复前签名泄漏检查只覆盖函数/方法，字段静默）。
// expect-error: Inconsistent accessibility: field type 'Hidden' is less accessible than field 'x'
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
pub class C {
    pub init()
    pub var x: Hidden = new Hidden()
}
pub func main(): i32 { return 0 }
