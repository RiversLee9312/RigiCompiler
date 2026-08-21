// V-C（负例）：继承子句未走填入点——Cage 的 T extends Animal 被 i32 违反。
// 修复后 InheritanceResolver 对构造基类跑 CheckConstructedType
pub class BadCage : Cage\<i32> {
    pub init()
}
pub func main(): i32 { return 0 }
