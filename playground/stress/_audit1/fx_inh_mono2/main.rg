// fx_inh_mono2（负例）：pub 类的构造基类实参为文件级私有类型——
// 单调性检查递归构造实参后在此拦截（修复前只比定义级，静默通过）
pub class HBox : Box\<Hidden> {
    pub init() { super(new Hidden()) }
}
pub func main(): i32 { return 0 }
