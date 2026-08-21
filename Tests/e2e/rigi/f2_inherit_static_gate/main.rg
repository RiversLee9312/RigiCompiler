// W2（负例·多文件同组）：静态字段 slot: T? 使用所属类型的类型参数。
// expect-error: static members cannot use type parameter 'T' of enclosing type 'SGate'
pub class Local {
    pub init()
}
pub class DGate : SGate\<Local> {
    pub init()
}
pub func main(): i32 { return 0 }
