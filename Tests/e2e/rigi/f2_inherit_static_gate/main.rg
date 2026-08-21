// F2/V-Cb（负例·多文件同组）：继承子句是填入点——SGate\<Local> 的
// 静态字段 slot 代入后非共享安全（闸门 1 经实参收口）。
// expect-error: Global or static field 'slot' must have a shared-safe type
pub class Local {
    pub init()
}
pub class DGate : SGate\<Local> {
    pub init()
}
pub func main(): i32 { return 0 }
