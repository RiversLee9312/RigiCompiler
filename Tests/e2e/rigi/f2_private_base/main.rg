// F2/V5b（负例）：pub 类继承文件级私有 open 基类——继承可见性单调性
// （C# CS0060 式，修复前无此检查静默放行）。
// expect-error: Inconsistent accessibility: base class 'SecretBase' is less accessible than class 'Exposed'
open class SecretBase {
    pub init()
    pub func secret(): i32 { return 1 }
}
pub class Exposed : SecretBase {
    pub init()
}
pub func main(): i32 { return 0 }
