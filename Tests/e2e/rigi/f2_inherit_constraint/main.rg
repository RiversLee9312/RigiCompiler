// F2/V-C（负例·多文件同组）：继承子句是填入点——构造基类 Cage\<i32>
// 违反 T extends Animal，InheritanceResolver 登记、InheritanceFillInChecker
// 收口（修复前继承子句不经 CheckConstructedType，静默通过）。
// expect-error: Type argument 'i32' does not satisfy the 'Extends Animal' constraint of 'T'
pub class BadCage : Cage\<i32> {
    pub init()
}
pub func main(): i32 { return 0 }
